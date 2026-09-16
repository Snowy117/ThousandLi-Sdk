using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed partial class ExpertVariableUpdateExecutionTests
{
    private static void AssertSectionsInOrder(string prompt)
    {
        var basic = prompt.IndexOf("<BasicInformation>", StringComparison.Ordinal);
        var previous = prompt.IndexOf("<PreviousState>", StringComparison.Ordinal);
        var information = prompt.IndexOf("<NewInformation>", StringComparison.Ordinal);
        var schema = prompt.IndexOf("<StateSchema>", StringComparison.Ordinal);
        var task = prompt.IndexOf("<Task>", StringComparison.Ordinal);
        Assert.True(basic >= 0 && basic < previous && previous < information && information < schema && schema < task);
    }

    private static List<BasicAiStreamEvent> MainStreamEvents(
        string narrative,
        string? reasoning,
        string? metadata = null)
    {
        var events = new List<BasicAiStreamEvent>();
        if (reasoning is not null)
            events.Add(new BasicAiReasoningStreamEvent(reasoning));
        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")));
        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.PropertyName("/narrative", "narrative")));
        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/narrative")));
        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", narrative)));
        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/narrative")));
        if (metadata is not null)
        {
            events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.PropertyName("/summary", "summary")));
            events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/summary")));
            events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/summary", metadata)));
            events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/summary")));
        }

        events.Add(new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectCompleted("")));
        return events;
    }

    private static BasicAiCompletionResult Completion(
        string json,
        string? reasoning = null)
    {
        using var document = JsonDocument.Parse(json);
        return new BasicAiCompletionResult(document.RootElement.Clone(), reasoning);
    }

    private static TestExpert CreateBoundExpert(
        IRuntimeBasicAi basicAi,
        IReadOnlySet<string>? metadataFieldNames = null)
    {
        var expert = new TestExpert(metadataFieldNames ?? SEmptyFields);
        expert.Bind(new LocalExpertExecutionContext(
            basicAi,
            new BoundPlayerProfile(TestSupport.PlayerId, "Player", string.Empty),
            NullLogger.Instance));
        return expert;
    }

    private sealed class TestExpert(IReadOnlySet<string> metadataFieldNames) : AbstractLongTextWritingExpert
    {
        protected override IReadOnlySet<string> MetadataFieldNames => metadataFieldNames;

        protected override Task<ExpertCompletionResult> StreamAsyncCore(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test expert is invoked through ExpertVariableUpdateExecution directly.");

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test expert is invoked through ExpertVariableUpdateExecution directly.");
    }

    private sealed class RecordingSink : IJsonExpertStreamEventSink
    {
        private readonly Dictionary<string, List<string>> _chunks = new(StringComparer.Ordinal);

        public ValueTask OnEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken = default)
        {
            if (streamEvent is not JsonStreamStringChunkEvent chunk)
                return ValueTask.CompletedTask;

            if (!_chunks.TryGetValue(chunk.Path, out var values))
            {
                values = [];
                _chunks.Add(chunk.Path, values);
            }

            values.Add(chunk.Value);
            return ValueTask.CompletedTask;
        }

        public List<string> GetChunks(string path) =>
            _chunks.TryGetValue(path, out var values) ? values : [];
    }

    private sealed class CannedBasicAi(
        IReadOnlyList<IReadOnlyList<BasicAiStreamEvent>>? streams = null,
        IReadOnlyList<BasicAiCompletionResult>? completions = null,
        Exception? completionFailure = null) : IRuntimeBasicAi
    {
        public IReadOnlyList<BasicAiModelDescriptor> AvailableModels => [];

        private readonly Queue<IReadOnlyList<BasicAiStreamEvent>> _streams = new(streams ?? []);
        private readonly Queue<BasicAiCompletionResult> _completions = new(completions ?? []);

        public List<BasicAiRequest> StreamRequests { get; } = [];

        public List<BasicAiRequest> CompletionRequests { get; } = [];

        public async IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
            BasicAiRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            await Task.CompletedTask;
            foreach (var evt in _streams.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }
        }

        public Task<BasicAiCompletionResult> CompleteAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletionRequests.Add(request);
            if (completionFailure is not null && _completions.Count == 0)
                return Task.FromException<BasicAiCompletionResult>(completionFailure);
            return Task.FromResult(_completions.Dequeue());
        }
    }
}
