using System.Text;
using ThousandLi.Contracts;
using CompletionResult = ThousandLi.Contracts.ExpertCompletionResult;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The single-call execution-flow tool for concrete experts: performs one BasicAi call, forwards
/// every JSON stream event to the expert's sink (no property-name filtering), captures declared
/// metadata root fields, forwards reasoning to the registered handler, and returns the completion
/// artifact. The tool performs no schema construction and no sink-level filtering of its own.
/// </summary>
public static class ExpertExecution
{
    /// <summary>
    /// Performs one streaming BasicAi call. Reasoning deltas are forwarded per-delta to the
    /// registered handler and also accumulated into the returned result.
    /// </summary>
    public static async Task<CompletionResult> StreamOnceAsync(
        IExpertExecutionParticipant expert,
        BasicAiRequest request,
        IJsonExpertStreamEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        var metadataCapture = new MetadataCapture(expert.MetadataFieldNames);
        var reasoningHandler = expert.ReasoningHandler;
        var reasoning = new StringBuilder();
        await foreach (var basicAiEvent in expert.BasicAi
                           .StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (basicAiEvent)
            {
                case BasicAiJsonStreamEvent jsonEvent:
                    await DispatchEventAsync(jsonEvent.Event, sink, metadataCapture, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BasicAiReasoningStreamEvent reasoningEvent:
                    reasoning.Append(reasoningEvent.Delta);
                    await RaiseReasoningAsync(reasoningHandler, reasoningEvent.Delta, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }

        return new CompletionResult(metadataCapture.Captured, reasoning.Length == 0 ? null : reasoning.ToString());
    }

    /// <summary>
    /// Performs one non-streaming BasicAi call, replays the completion JSON through the sink as
    /// stream events, and forwards the full reasoning text once as a single delta.
    /// </summary>
    public static async Task<CompletionResult> CompleteOnceAsync(
        IExpertExecutionParticipant expert,
        BasicAiRequest request,
        IJsonExpertStreamEventSink sink,
        CancellationToken cancellationToken = default)
    {
        var (result, _) = await CompleteOnceWithCompletionAsync(expert, request, sink, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The completion-bearing variant used by follow-up orchestrators that need the raw BasicAi
    /// completion (for example the shared variable-update second pass) in addition to the result.
    /// </summary>
    internal static async Task<(CompletionResult Result, BasicAiCompletionResult Completion)>
        CompleteOnceWithCompletionAsync(
            IExpertExecutionParticipant expert,
            BasicAiRequest request,
            IJsonExpertStreamEventSink sink,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        var completion = await expert.BasicAi.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        var metadataCapture = new MetadataCapture(expert.MetadataFieldNames);
        foreach (var streamEvent in ExpertJsonStreamEventMapper.ParseElementEvents(completion.Json))
            await DispatchEventAsync(streamEvent, sink, metadataCapture, cancellationToken).ConfigureAwait(false);
        if (completion.Reasoning is { } fullReasoning)
            await RaiseReasoningAsync(expert.ReasoningHandler, fullReasoning, cancellationToken).ConfigureAwait(false);
        return (new CompletionResult(metadataCapture.Captured, completion.Reasoning), completion);
    }

    private static async ValueTask DispatchEventAsync(
        JsonStreamEvent streamEvent,
        IJsonExpertStreamEventSink sink,
        MetadataCapture metadataCapture,
        CancellationToken cancellationToken)
    {
        await sink.OnEventAsync(streamEvent, cancellationToken).ConfigureAwait(false);
        if (metadataCapture.OnEvent(streamEvent) is { } captured)
            metadataCapture.Commit(captured.Field, captured.Value);
    }

    private static ValueTask RaiseReasoningAsync(
        Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? reasoningHandler,
        string delta,
        CancellationToken cancellationToken) =>
        reasoningHandler?.Invoke(new ReasoningDeltaEvent(delta), cancellationToken) ?? ValueTask.CompletedTask;

    /// <summary>
    /// Accumulates string chunks of declared metadata root properties and yields the completed
    /// field value on the string-completed event. Fields completed without any chunk (an empty
    /// string) are not captured.
    /// </summary>
    private sealed class MetadataCapture(IReadOnlySet<string> fieldNames)
    {
        private readonly Dictionary<string, string> _captured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StringBuilder> _buffers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _paths = fieldNames.ToDictionary(
            field => "/" + field,
            field => field,
            StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string>? Captured =>
            _captured.Count == 0 ? null : _captured.AsReadOnly();

        public (string Field, string Value)? OnEvent(JsonStreamEvent streamEvent)
        {
            switch (streamEvent)
            {
                case JsonStreamStringChunkEvent chunk when _paths.ContainsKey(chunk.Path):
                    GetOrCreateBuffer(chunk.Path).Append(chunk.Value);
                    return null;
                case JsonStreamStringCompletedEvent completed
                    when _paths.TryGetValue(completed.Path, out var field)
                    && _buffers.TryGetValue(completed.Path, out var buffer)
                    && buffer.Length > 0:
                    return (field, buffer.ToString());
                default:
                    return null;
            }
        }

        public void Commit(string field, string value) => _captured[field] = value;

        private StringBuilder GetOrCreateBuffer(string path) =>
            _buffers.TryGetValue(path, out var buffer) ? buffer : _buffers[path] = new StringBuilder();
    }
}
