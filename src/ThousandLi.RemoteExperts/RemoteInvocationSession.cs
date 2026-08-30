using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// The per-frame callback of <see cref="RemoteInvocationSession" />: returns a non-null value to
/// complete the invocation with it, or <see langword="null" /> to keep streaming. Terminal
/// <c>failed</c>/<c>cancelled</c> classification is owned by the session; the handler observes
/// event, dataRequest, and completed frames after ordinal deduplication.
/// </summary>
/// <typeparam name="T">The invocation result type.</typeparam>
/// <param name="snapshot">The started invocation's status snapshot.</param>
/// <param name="frame">The decoded stream frame.</param>
/// <param name="cancellationToken">The combined timeout/caller cancellation token.</param>
public delegate ValueTask<T?> RemoteInvocationFrameHandler<T>(
    RemoteInvocationSnapshot snapshot,
    RemoteExpertStreamFrame frame,
    CancellationToken cancellationToken) where T : class;

/// <summary>
/// The single shared execution flow behind every remote-invocation consumer (the
/// <see cref="RemoteInvocationRunner" /> Playground tool face and the game-facing remote proxy):
/// binding resolution, catalog contract precheck, idempotent start, timeout/cancellation
/// propagation with best-effort DELETE, and the SSE stream loop with ordinal deduplication,
/// Last-Event-ID reconnect, and terminal-frame classification. Consumers only supply the
/// per-frame handler.
/// </summary>
internal sealed class RemoteInvocationSession(RemoteExpertClient client, RemoteInvocationRunnerOptions options)
{
    private readonly RemoteExpertClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RemoteInvocationRunnerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Starts one invocation and streams it to completion. The idempotency key defaults to a fresh
    /// random key; the timeout is enforced locally and forwarded as <c>timeoutSeconds</c>.
    /// </summary>
    /// <typeparam name="T">The invocation result type.</typeparam>
    /// <param name="contract">The contract identity triple required from the platform.</param>
    /// <param name="input">The opaque contract input JSON.</param>
    /// <param name="idempotencyKey">The idempotency key, or <see langword="null" /> for a fresh random key.</param>
    /// <param name="clientCorrelation">An optional client-side correlation label.</param>
    /// <param name="handleFrame">The per-frame handler.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async ValueTask<T> ExecuteAsync<T>(
        ExpertContractDescriptor contract,
        JsonElement input,
        string? idempotencyKey,
        string? clientCorrelation,
        RemoteInvocationFrameHandler<T> handleFrame,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(handleFrame);
        cancellationToken.ThrowIfCancellationRequested();

        var expertPackageId = ResolveBinding(contract.Id);
        await EnsureContractCompatibleAsync(contract, cancellationToken).ConfigureAwait(false);

        var startRequest = new RemoteInvocationStartRequest(
            contract,
            expertPackageId,
            input,
            idempotencyKey: idempotencyKey ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            timeoutSeconds: _options.Timeout is { } timeout
                ? (int)Math.Ceiling(timeout.TotalSeconds)
                : null,
            clientCorrelation: clientCorrelation);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Timeout is { } configuredTimeout)
            timeoutCts.CancelAfter(configuredTimeout);
        var token = timeoutCts.Token;

        RemoteInvocationSnapshot? snapshot = null;
        try
        {
            snapshot = await _client.StartInvocationAsync(startRequest, token).ConfigureAwait(false);
            return await StreamToCompletionAsync(snapshot, handleFrame, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
            await TryCancelRemotelyAsync(snapshot).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                throw new RemoteInvocationTimeoutException(
                    $"The remote expert invocation '{snapshot?.ExpertInvocationId ?? "(unknown)"}' exceeded " +
                    $"the configured timeout of {_options.Timeout}.",
                    innerException: ex);
            throw new RemoteInvocationCancelledException(
                $"The remote expert invocation '{snapshot?.ExpertInvocationId ?? "(unknown)"}' was cancelled.",
                innerException: ex);
        }
    }

    private string ResolveBinding(string contractId)
    {
        if (_options.ContractBindings.TryGetValue(contractId, out var expertPackageId))
            return expertPackageId;
        var bound = _options.ContractBindings.Count == 0
            ? "none"
            : string.Join(", ", _options.ContractBindings.Keys
                .Order(StringComparer.Ordinal)
                .Select(id => $"'{id}'"));
        throw new RemoteExpertException(
            $"No remote Expert Package binding is configured for contract '{contractId}'. Configured bindings: {bound}.");
    }

    private async ValueTask EnsureContractCompatibleAsync(
        ExpertContractDescriptor contract,
        CancellationToken cancellationToken)
    {
        var contracts = await _client.ListContractsAsync(cancellationToken).ConfigureAwait(false);
        var catalog = contracts.FirstOrDefault(entry =>
            string.Equals(entry.ContractId, contract.Id, StringComparison.Ordinal));
        if (catalog is null)
        {
            var registered = contracts.Count == 0
                ? "none"
                : string.Join(", ", contracts
                    .Select(entry => entry.ContractId)
                    .Order(StringComparer.Ordinal)
                    .Select(id => $"'{id}'"));
            throw new RemoteUnknownContractException(
                $"Contract '{contract.Id}' is not registered on the remote platform. Registered contracts: {registered}.");
        }

        if (catalog.Version.Supports(contract.Version) &&
            string.Equals(catalog.Fingerprint, contract.Fingerprint, StringComparison.Ordinal))
            return;

        throw new RemoteContractMismatchException(
            $"Contract mismatch for '{contract.Id}': the invocation requires {contract.Version} " +
            $"with fingerprint '{contract.Fingerprint}', but the remote catalog has {catalog.Version} " +
            $"with fingerprint '{catalog.Fingerprint}'.",
            requiredVersion: contract.Version.ToString(),
            requiredFingerprint: contract.Fingerprint,
            availableVersion: catalog.Version.ToString(),
            availableFingerprint: catalog.Fingerprint);
    }

    private async ValueTask<T> StreamToCompletionAsync<T>(
        RemoteInvocationSnapshot snapshot,
        RemoteInvocationFrameHandler<T> handleFrame,
        CancellationToken token)
        where T : class
    {
        var lastSeen = -1L;
        var attempt = 0;
        while (true)
        {
            try
            {
                await foreach (var frame in _client
                                   .StreamEventsAsync(snapshot.ExpertInvocationId, lastSeen, token)
                                   .ConfigureAwait(false))
                {
                    T? result;
                    switch (frame)
                    {
                        case RemoteExpertFailedFrame failed:
                            throw RemoteExpertExceptionFactory.Create(
                                failed.Code,
                                $"The remote expert invocation '{snapshot.ExpertInvocationId}' failed: {failed.Message}");
                        case RemoteExpertCancelledFrame:
                            throw new RemoteInvocationCancelledException(
                                $"The remote expert invocation '{snapshot.ExpertInvocationId}' was cancelled.");
                        case RemoteExpertEventFrame eventFrame:
                            // A reconnect replay can re-deliver already-seen ordinals; each frame must
                            // be observed exactly once (semantic events and data responses alike).
                            if (eventFrame.Ordinal <= lastSeen)
                                continue;
                            lastSeen = eventFrame.Ordinal;
                            result = await handleFrame(snapshot, eventFrame, token).ConfigureAwait(false);
                            break;
                        case RemoteExpertDataRequestFrame dataRequestFrame:
                            if (dataRequestFrame.Ordinal <= lastSeen)
                                continue;
                            lastSeen = dataRequestFrame.Ordinal;
                            result = await handleFrame(snapshot, dataRequestFrame, token).ConfigureAwait(false);
                            break;
                        case RemoteExpertCompletedFrame completedFrame:
                            result = await handleFrame(snapshot, completedFrame, token).ConfigureAwait(false);
                            if (result is null)
                                throw new RemoteProtocolException(
                                    "The completed frame handler declined to finish the invocation.");
                            return result;
                        default:
                            throw new RemoteProtocolException(
                                $"Unknown stream frame '{frame.GetType().Name}'.");
                    }

                    if (result is not null)
                        return result;
                }

                throw new IOException("The event stream ended without a terminal frame.");
            }
            catch (Exception ex) when (IsReconnectable(ex, token))
            {
                attempt++;
                if (attempt > _options.MaxReconnects)
                    throw new RemoteExpertException(
                        $"Lost the event stream of expert invocation '{snapshot.ExpertInvocationId}' " +
                        $"after {attempt} connection attempts.",
                        errorCode: null,
                        statusCode: null,
                        innerException: ex);
            }
        }
    }

    private static bool IsReconnectable(Exception exception, CancellationToken token) =>
        !token.IsCancellationRequested &&
        exception is IOException or HttpRequestException or SocketException;

    private async ValueTask TryCancelRemotelyAsync(RemoteInvocationSnapshot? snapshot)
    {
        // Best-effort cancellation: the DELETE is idempotent on the platform side, and a failure to
        // reach the platform must not mask the original timeout/cancellation with its own error.
        if (snapshot is null)
            return;
        try
        {
            await _client.CancelInvocationAsync(snapshot.ExpertInvocationId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the caller observes the timeout/cancelled exception instead.
        }
    }
}
