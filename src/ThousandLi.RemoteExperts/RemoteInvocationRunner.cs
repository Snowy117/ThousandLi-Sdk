using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// Options for <see cref="RemoteInvocationRunner" />. <see cref="ContractBindings" /> maps a contract
/// id to the Expert Package canonical id that executes it on the platform (mirroring the slice-2
/// local explicit-binding semantics); contracts without a binding fail deterministically before any
/// HTTP traffic.
/// </summary>
public sealed record RemoteInvocationRunnerOptions
{
    public RemoteInvocationRunnerOptions(
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
/// Executes expert invocations on the remote platform: binding resolution, catalog precheck,
/// idempotent start, SSE streaming with deduplicating reconnect, cancellation propagation
/// (DELETE), and timeout. The idempotency key is the request
/// <see cref="ExpertInvocationRequest.ChannelKey" /> when present, so replaying a channel key
/// resumes the same remote invocation instead of executing twice. The shared execution flow lives
/// in the internal <c>RemoteInvocationSession</c>, which the game-facing remote proxy consumes as
/// well; this runner is the Playground/protocol tool face over raw semantic events.
/// </summary>
public sealed class RemoteInvocationRunner(RemoteExpertClient client, RemoteInvocationRunnerOptions options)
{
    private static readonly JsonElement NullOutput = CreateNullElement();

    private readonly RemoteExpertClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RemoteInvocationRunnerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Starts, streams, and completes one remote expert invocation.</summary>
    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);

        var session = new RemoteInvocationSession(_client, _options);
        return await session.ExecuteAsync(
            request.Contract,
            request.Input,
            request.ChannelKey,
            request.ScenarioId,
            (snapshot, frame, token) => HandleFrameAsync(snapshot, frame, events, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ExpertInvocationResult?> HandleFrameAsync(
        RemoteInvocationSnapshot snapshot,
        RemoteExpertStreamFrame frame,
        IExpertSemanticEventSink events,
        CancellationToken token)
    {
        switch (frame)
        {
            case RemoteExpertEventFrame eventFrame:
                await events.WriteAsync(
                    new ExpertSemanticEvent(eventFrame.EventType, eventFrame.Payload),
                    token).ConfigureAwait(false);
                return null;
            case RemoteExpertCompletedFrame completed:
                return new ExpertInvocationResult(
                    snapshot.ExpertInvocationId,
                    completed.Output ?? NullOutput);
            default:
                throw new RemoteProtocolException($"Unexpected stream frame '{frame.GetType().Name}'.");
        }
    }

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}
