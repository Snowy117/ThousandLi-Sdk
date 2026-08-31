using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// Deterministic recording-replay runner backed by semantic recordings: recordings are
/// consumed in registration order (mirroring <c>RecordedBasicAi</c>), each invocation's contract
/// must match the next recording's contract, and exhaustion fails fast. Committed recordings
/// re-emit their recorded events and result; aborted and error recordings reproduce the recorded
/// failure. Use <see cref="ExpertRecordingComparison"/> to compare a fresh live invocation
/// against a recording with layered determinism.
/// </summary>
public sealed class RecordingReplayExpertRunner : IExpertRunner
{
    private readonly Queue<ExpertInvocationRecording> _recordings;
    private readonly List<ExpertInvocationRequest> _invocations = [];
    private int _sequence;

    public RecordingReplayExpertRunner(IEnumerable<ExpertInvocationRecording> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        var materialized = recordings.ToArray();
        foreach (var recording in materialized)
            ArgumentNullException.ThrowIfNull(recording);
        _recordings = new Queue<ExpertInvocationRecording>(materialized);
    }

    /// <summary>Requests received so far, in call order, for post-hoc assertions.</summary>
    public IReadOnlyList<ExpertInvocationRequest> Invocations => [.. _invocations];

    public int RemainingRecordings => _recordings.Count;

    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        _invocations.Add(request);
        if (_recordings.Count == 0)
            throw new InvalidOperationException(
                $"No recorded invocations remain; the invocation for contract '{request.ContractId}' exceeds "
                + $"the recording (request {_invocations.Count}).");
        var recording = _recordings.Dequeue();
        if (!string.Equals(recording.ContractId, request.ContractId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Replayed recording {_invocations.Count} targets contract '{recording.ContractId}', " +
                $"but the invocation requires '{request.ContractId}'.");
        }

        if (recording.Terminal.Status != ExpertRecordingTerminal.Committed)
        {
            throw new InvalidOperationException(
                $"The replayed invocation for contract '{recording.ContractId}' failed as recorded " +
                $"({recording.Terminal.Status}): {recording.Terminal.Error ?? recording.Terminal.Status}");
        }

        foreach (var semanticEvent in recording.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await events.WriteAsync(
                new ExpertSemanticEvent(semanticEvent.EventType, semanticEvent.Payload),
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ExpertInvocationResult(
            $"replay-{Interlocked.Increment(ref _sequence):D8}",
            recording.Terminal.Output ?? throw new InvalidOperationException(
                $"The committed recording for '{recording.ContractId}' is missing its output."));
    }
}
