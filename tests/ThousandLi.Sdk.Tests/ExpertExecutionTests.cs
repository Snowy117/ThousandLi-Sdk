using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ExpertCompletionResult = ThousandLi.Contracts.ExpertCompletionResult;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertExecutionTests
{
    private static readonly BasicAiRequest s_request =
        new("test-model", [BasicAiMessage.User("test")], AiJsonSchema.Object());

    private static readonly IReadOnlySet<string> s_emptyFields = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> s_afterThinkingOnly =
        new HashSet<string>(["afterThinking"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> s_afterThinkingAndFormat =
        new HashSet<string>(["afterThinking", "afterFormat"], StringComparer.Ordinal);

    [Fact]
    public async Task StreamOnceAsync_ForwardsJsonEventsToSink()
    {
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/content")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/content", "hello ")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/content", "world")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/content")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectCompleted("")),
        ], s_emptyFields);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal(["hello ", "world"], sink.GetChunks("/content"));
        Assert.Null(result.Metadata);
        Assert.Null(result.Reasoning);
    }

    [Fact]
    public async Task StreamOnceAsync_CapturesDeclaredMetadataFields()
    {
        var (expert, sink) = CreateStreamExpertAndSink(MetadataEvents("deep thought", "formatted"),
            s_afterThinkingAndFormat);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.NotNull(result.Metadata);
        Assert.Equal("deep thought", result.Metadata!["afterThinking"]);
        Assert.Equal("formatted", result.Metadata["afterFormat"]);
    }

    [Fact]
    public async Task StreamOnceAsync_ForwardsReasoningDeltasToHandler()
    {
        var received = new List<string>();
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiReasoningStreamEvent("partial "),
            new BasicAiReasoningStreamEvent("reasoning"),
        ], s_emptyFields);
        expert.WithReasoningHandler((delta, _) =>
        {
            received.Add(delta.Delta);
            return ValueTask.CompletedTask;
        });

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal(["partial ", "reasoning"], received);
        Assert.Equal("partial reasoning", result.Reasoning);
    }

    [Fact]
    public async Task StreamOnceAsync_NoReasoningHandler_DoesNotThrow()
    {
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiReasoningStreamEvent("reasoning"),
        ], s_emptyFields);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal("reasoning", result.Reasoning);
    }

    [Fact]
    public async Task StreamOnceAsync_IgnoresUndeclaredMetadataFields()
    {
        var (expert, sink) = CreateStreamExpertAndSink(MetadataEvents("deep thought", "formatted"),
            s_afterThinkingOnly);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.NotNull(result.Metadata);
        Assert.Single(result.Metadata!);
        Assert.Equal("deep thought", result.Metadata["afterThinking"]);
    }

    [Fact]
    public async Task StreamOnceAsync_AccumulatesMultiChunkMetadataField()
    {
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/afterThinking")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterThinking", "thinking ")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterThinking", "very ")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterThinking", "deeply")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/afterThinking")),
        ], s_afterThinkingOnly);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal("thinking very deeply", result.Metadata!["afterThinking"]);
    }

    [Fact]
    public async Task StreamOnceAsync_EmptyMetadataFieldNotCaptured()
    {
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/afterThinking")),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/afterThinking")),
        ], s_afterThinkingOnly);

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task StreamOnceAsync_MixedJsonAndReasoningEvents_AllForwardedCorrectly()
    {
        var reasoningDeltas = new List<string>();
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiReasoningStreamEvent("step 1"),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/content", "hello")),
            new BasicAiReasoningStreamEvent("step 2"),
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/content")),
        ], s_emptyFields);
        expert.WithReasoningHandler((delta, _) =>
        {
            reasoningDeltas.Add(delta.Delta);
            return ValueTask.CompletedTask;
        });

        var result = await ExpertExecution.StreamOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal(["step 1", "step 2"], reasoningDeltas);
        Assert.Equal(["hello"], sink.GetChunks("/content"));
        Assert.Equal("step 1step 2", result.Reasoning);
    }

    [Fact]
    public async Task StreamOnceAsync_PropagatesCancellation()
    {
        var (expert, sink) = CreateStreamExpertAndSink(
        [
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/content", "hello")),
        ], s_emptyFields);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ExpertExecution.StreamOnceAsync(expert, s_request, sink, cancelled.Token));
    }

    [Fact]
    public async Task StreamOnceAsync_NullArguments_ThrowArgumentNullException()
    {
        var (expert, sink) = CreateStreamExpertAndSink([], s_emptyFields);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.StreamOnceAsync(null!, s_request, sink, TestSupport.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.StreamOnceAsync(expert, null!, sink, TestSupport.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.StreamOnceAsync(expert, s_request, null!, TestSupport.CancellationToken));
    }

    [Fact]
    public async Task CompleteOnceAsync_ForwardsEventsFromParsedCompletion()
    {
        var expert = CreateBoundExpert(
            new CannedCompletionBasicAi(new BasicAiCompletionResult(
                TestSupport.Json("""{"content":"hello world"}"""),
                reasoning: null)),
            s_emptyFields);
        var sink = new RecordingSink();

        var result = await ExpertExecution.CompleteOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.NotEmpty(sink.GetChunks("/content"));
        Assert.Null(result.Reasoning);
    }

    [Fact]
    public async Task CompleteOnceAsync_CapturesDeclaredMetadataFields()
    {
        var expert = CreateBoundExpert(
            new CannedCompletionBasicAi(new BasicAiCompletionResult(
                TestSupport.Json("""{"afterThinking":"deep thought","afterFormat":"formatted"}"""), null)),
            s_afterThinkingAndFormat);
        var sink = new RecordingSink();

        var result = await ExpertExecution.CompleteOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.NotNull(result.Metadata);
        Assert.Equal("deep thought", result.Metadata!["afterThinking"]);
        Assert.Equal("formatted", result.Metadata["afterFormat"]);
    }

    [Fact]
    public async Task CompleteOnceAsync_ForwardsReasoningAsSingleDelta()
    {
        var reasoningDeltas = new List<string>();
        var expert = CreateBoundExpert(
            new CannedCompletionBasicAi(new BasicAiCompletionResult(
                TestSupport.Json("""{"content":"hello"}"""), "full reasoning text")),
            s_emptyFields);
        expert.WithReasoningHandler((delta, _) =>
        {
            reasoningDeltas.Add(delta.Delta);
            return ValueTask.CompletedTask;
        });
        var sink = new RecordingSink();

        var result = await ExpertExecution.CompleteOnceAsync(expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal(["full reasoning text"], reasoningDeltas);
        Assert.Equal("full reasoning text", result.Reasoning);
    }

    [Fact]
    public async Task CompleteOnceAsync_NullArguments_ThrowArgumentNullException()
    {
        var expert = CreateBoundExpert(
            new CannedCompletionBasicAi(new BasicAiCompletionResult(TestSupport.Json("{}"), null)),
            s_emptyFields);
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.CompleteOnceAsync(null!, s_request, sink, TestSupport.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.CompleteOnceAsync(expert, null!, sink, TestSupport.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExpertExecution.CompleteOnceAsync(expert, s_request, null!, TestSupport.CancellationToken));
    }

    [Fact]
    public async Task CompleteOnceWithCompletionAsync_ReturnsCompletionAlongsideResult()
    {
        var completion = new BasicAiCompletionResult(
            TestSupport.Json("""{"afterThinking":"deep thought"}"""), "provider reasoning");
        var expert = CreateBoundExpert(new CannedCompletionBasicAi(completion), s_afterThinkingOnly);
        var sink = new RecordingSink();

        var (result, returnedCompletion) = await ExpertExecution.CompleteOnceWithCompletionAsync(
            expert, s_request, sink, TestSupport.CancellationToken);

        Assert.Equal("deep thought", result.Metadata!["afterThinking"]);
        Assert.Equal("provider reasoning", result.Reasoning);
        Assert.Same(completion, returnedCompletion);
    }

    [Fact]
    public async Task ExpertCompleteAsyncOverride_DelegatesToExpertExecution()
    {
        var basicAi = new CannedStreamBasicAi(
        [
            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/content", "hello")),
        ]);
        var expert = new DelegatingExpert(s_request, s_emptyFields);
        expert.Bind(CreateContext(basicAi));

        var result = await expert.StreamAsync(TestSupport.CancellationToken);

        Assert.True(expert.Invoked);
        Assert.Equal(["hello"], expert.Sink.GetChunks("/content"));
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void ExpertExecution_ExposesSingleCallStaticToolContract()
    {
        var type = typeof(ExpertExecution);
        Assert.True(type.IsAbstract && type.IsSealed);

        foreach (var methodName in new[] { nameof(ExpertExecution.StreamOnceAsync), nameof(ExpertExecution.CompleteOnceAsync) })
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var parameters = method!.GetParameters();
            Assert.Equal(4, parameters.Length);
            Assert.Equal(typeof(IExpertExecutionParticipant), parameters[0].ParameterType);
            Assert.Equal(typeof(BasicAiRequest), parameters[1].ParameterType);
            Assert.Equal(typeof(IJsonExpertStreamEventSink), parameters[2].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[3].ParameterType);
            Assert.Equal(typeof(Task<>).MakeGenericType(typeof(ExpertCompletionResult)), method.ReturnType);
        }
    }

    private static IReadOnlyList<BasicAiStreamEvent> MetadataEvents(string afterThinking, string afterFormat) =>
    [
        new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.PropertyName("", "afterThinking")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/afterThinking")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterThinking", afterThinking)),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/afterThinking")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.PropertyName("", "afterFormat")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/afterFormat")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterFormat", afterFormat)),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/afterFormat")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectCompleted("")),
    ];

    private static LocalExpertExecutionContext CreateContext(IRuntimeBasicAi basicAi) =>
        new(basicAi, new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona"), NullLogger.Instance);

    private static TestExpert CreateBoundExpert(IRuntimeBasicAi basicAi, IReadOnlySet<string> metadataFields)
    {
        var expert = new TestExpert(metadataFields);
        expert.Bind(CreateContext(basicAi));
        return expert;
    }

    private static (TestExpert Expert, RecordingSink Sink) CreateStreamExpertAndSink(
        IReadOnlyList<BasicAiStreamEvent> events,
        IReadOnlySet<string> metadataFields)
    {
        var expert = CreateBoundExpert(new CannedStreamBasicAi(events), metadataFields);
        return (expert, new RecordingSink());
    }

    private sealed class TestExpert(IReadOnlySet<string> metadataFields) : RuntimeLongTextWritingExpertBase
    {
        protected internal override IReadOnlySet<string> MetadataFieldNames => metadataFields;

        public override Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The test expert is invoked through ExpertExecution directly.");

        public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The test expert is invoked through ExpertExecution directly.");
    }

    private sealed class DelegatingExpert(
        BasicAiRequest request,
        IReadOnlySet<string> metadataFields) : RuntimeLongTextWritingExpertBase
    {
        public bool Invoked { get; private set; }

        public RecordingSink Sink { get; } = new();

        protected internal override IReadOnlySet<string> MetadataFieldNames => metadataFields;

        public override async Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default)
        {
            Invoked = true;
            return await ExpertExecution.StreamOnceAsync(this, request, Sink, cancellationToken);
        }

        public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The delegating expert only exercises the streaming path.");
    }

    private sealed class RecordingSink : IJsonExpertStreamEventSink
    {
        private readonly ConcurrentDictionary<string, List<string>> _chunks = new();

        public ValueTask OnEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken = default)
        {
            if (streamEvent is JsonStreamStringChunkEvent chunk)
                _chunks.GetOrAdd(chunk.Path, _ => []).Add(chunk.Value);
            return ValueTask.CompletedTask;
        }

        public List<string> GetChunks(string path) =>
            _chunks.TryGetValue(path, out var chunks) ? chunks : [];
    }

    private sealed class CannedStreamBasicAi(IReadOnlyList<BasicAiStreamEvent> events) : IRuntimeBasicAi
    {
        public IReadOnlyList<BasicAiModelDescriptor> AvailableModels => [];

        // Deliberately a synchronous replay iterator: every canned event is already materialized,
        // so no asynchronous operation exists to await. Cancellation is honored between events.
        [SuppressMessage("ReSharper", "AsyncMethodWithoutAwait",
            Justification = "The canned sequence is fully materialized; iteration is intentionally synchronous.")]
        public async IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
            BasicAiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            foreach (var streamEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
            }
        }

        public Task<BasicAiCompletionResult> CompleteAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The canned stream BasicAi does not complete.");
    }

    private sealed class CannedCompletionBasicAi(BasicAiCompletionResult completion) : IRuntimeBasicAi
    {
        public IReadOnlyList<BasicAiModelDescriptor> AvailableModels => [];

        public IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The canned completion BasicAi does not stream.");

        public Task<BasicAiCompletionResult> CompleteAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(completion);
        }
    }
}
