using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// Options for <see cref="RemoteExpertExecutor" />. <see cref="ContractBindings" /> maps a contract
/// id to the Expert Package canonical id that executes it on the platform (mirroring the slice-2
/// local explicit-binding semantics); contracts without a binding fail deterministically before any
/// HTTP traffic.
/// </summary>
public sealed record RemoteExpertExecutorOptions
{
    public RemoteExpertExecutorOptions(
        IReadOnlyDictionary<string, string>? contractBindings = null,
        TimeSpan? timeout = null,
        int maxReconnects = 3)
    {
        var bindings = contractBindings ?? new Dictionary<string, string>();
        foreach (var binding in bindings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.Value);
        }

        if (timeout is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegative(maxReconnects);

        ContractBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
        Timeout = timeout;
        MaxReconnects = maxReconnects;
    }

    /// <summary>Contract id to Expert Package id bindings, compared ordinally.</summary>
    public IReadOnlyDictionary<string, string> ContractBindings { get; }

    /// <summary>
    /// The execution timeout. It is enforced locally and forwarded to the platform as
    /// <c>timeoutSeconds</c> (rounded up); the platform timeout failure carries the stable
    /// <c>timeout</c> code.
    /// </summary>
    public TimeSpan? Timeout { get; }

    /// <summary>How many times a broken event stream is reconnected (with Last-Event-ID resume) before failing.</summary>
    public int MaxReconnects { get; }
}

/// <summary>
/// Executes expert invocations on the remote platform through the <see cref="IExpertExecutor" />
/// seam: binding resolution, catalog precheck, idempotent start, SSE streaming with deduplicating
/// reconnect, cancellation propagation (DELETE), and timeout. The idempotency key is the request
/// <see cref="ExpertInvocationRequest.ChannelKey" /> when present, so replaying a channel key
/// resumes the same remote invocation instead of executing twice.
/// </summary>
public sealed class RemoteExpertExecutor(RemoteExpertClient client, RemoteExpertExecutorOptions options)
    : IExpertExecutor
{
    private static readonly JsonElement NullOutput = CreateNullElement();

    private readonly RemoteExpertClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RemoteExpertExecutorOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();

        var expertPackageId = ResolveBinding(request.Contract.Id);
        await EnsureContractCompatibleAsync(request, cancellationToken).ConfigureAwait(false);

        var startRequest = new RemoteInvocationStartRequest(
            request.Contract,
            expertPackageId,
            request.Input,
            idempotencyKey: request.ChannelKey ??
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            timeoutSeconds: _options.Timeout is { } timeout
                ? (int)Math.Ceiling(timeout.TotalSeconds)
                : null,
            clientCorrelation: request.ScenarioId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Timeout is { } configuredTimeout)
            timeoutCts.CancelAfter(configuredTimeout);
        var token = timeoutCts.Token;

        RemoteInvocationSnapshot? snapshot = null;
        try
        {
            snapshot = await _client.StartInvocationAsync(startRequest, token).ConfigureAwait(false);
            return await StreamToCompletionAsync(snapshot, events, token).ConfigureAwait(false);
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
        ExpertInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var contracts = await _client.ListContractsAsync(cancellationToken).ConfigureAwait(false);
        var catalog = contracts.FirstOrDefault(contract =>
            string.Equals(contract.ContractId, request.Contract.Id, StringComparison.Ordinal));
        if (catalog is null)
        {
            var registered = contracts.Count == 0
                ? "none"
                : string.Join(", ", contracts
                    .Select(contract => contract.ContractId)
                    .Order(StringComparer.Ordinal)
                    .Select(id => $"'{id}'"));
            throw new RemoteUnknownContractException(
                $"Contract '{request.Contract.Id}' is not registered on the remote platform. Registered contracts: {registered}.");
        }

        if (catalog.Version.Supports(request.Contract.Version) &&
            string.Equals(catalog.Fingerprint, request.Contract.Fingerprint, StringComparison.Ordinal))
            return;

        throw new RemoteContractMismatchException(
            $"Contract mismatch for '{request.Contract.Id}': the invocation requires {request.Contract.Version} " +
            $"with fingerprint '{request.Contract.Fingerprint}', but the remote catalog has {catalog.Version} " +
            $"with fingerprint '{catalog.Fingerprint}'.",
            requiredVersion: request.Contract.Version.ToString(),
            requiredFingerprint: request.Contract.Fingerprint,
            availableVersion: catalog.Version.ToString(),
            availableFingerprint: catalog.Fingerprint);
    }

    private async ValueTask<ExpertInvocationResult> StreamToCompletionAsync(
        RemoteInvocationSnapshot snapshot,
        IExpertSemanticEventSink events,
        CancellationToken token)
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
                    switch (frame)
                    {
                        case RemoteExpertEventFrame eventFrame:
                            // A reconnect replay can re-deliver already-seen ordinals; the sink must
                            // observe each semantic event exactly once.
                            if (eventFrame.Ordinal <= lastSeen)
                                continue;
                            lastSeen = eventFrame.Ordinal;
                            await events.WriteAsync(
                                new ExpertSemanticEvent(eventFrame.EventType, eventFrame.Payload),
                                token).ConfigureAwait(false);
                            break;
                        case RemoteExpertCompletedFrame completed:
                            return new ExpertInvocationResult(
                                snapshot.ExpertInvocationId,
                                completed.Output ?? NullOutput);
                        case RemoteExpertFailedFrame failed:
                            throw RemoteExpertExceptionFactory.Create(
                                failed.Code,
                                $"The remote expert invocation '{snapshot.ExpertInvocationId}' failed: {failed.Message}");
                        case RemoteExpertCancelledFrame:
                            throw new RemoteInvocationCancelledException(
                                $"The remote expert invocation '{snapshot.ExpertInvocationId}' was cancelled.");
                        default:
                            throw new RemoteProtocolException(
                                $"Unknown stream frame '{frame.GetType().Name}'.");
                    }
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

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}
