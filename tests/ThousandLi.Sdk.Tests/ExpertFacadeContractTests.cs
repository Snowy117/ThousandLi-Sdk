using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertFacadeContractTests
{
    [Fact]
    public async Task UseResolvesConcreteExpertAndFluentChainExecutes()
    {
        var facade = new ContextSupportFakeFacade();
        var builder = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPlayerPersona("persona")
            .WithCurrentState("{}")
            .WithHistoryBuckets(new InMemoryBucketStub())
            .WithReasoningHandler((_, _) => ValueTask.CompletedTask);

        var result = await builder.StreamAsync(TestSupport.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata!.ContainsKey("afterFormat"));
        Assert.Equal("reasoned", result.Reasoning);
    }

    [Fact]
    public void FluentChainReturnsSameBuilderInstance()
    {
        var facade = new ContextSupportFakeFacade();
        var builder = facade.Use<AbstractLongTextWritingExpert>();

        Assert.Same(builder, builder.WithWorldSettings("world"));
        Assert.Same(builder, builder.WithPlayerInput("input"));
        Assert.Same(builder, builder.WithPlayerPersona("persona"));
        Assert.Same(builder, builder.WithCurrentState("{}"));
        Assert.Same(builder, builder.WithPrimaryOutput(
            new JsonPrimaryOutput("content", AiJsonSchema.String(), (_, _) => ValueTask.CompletedTask)));
    }

    [Fact]
    public async Task StreamAsyncRequiresCategoryInputs()
    {
        var facade = new ContextSupportFakeFacade();
        var builder = facade.Use<AbstractLongTextWritingExpert>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.StreamAsync(TestSupport.CancellationToken));

        Assert.Contains("world settings", exception.Message);
    }

    [Fact]
    public void AiSchemaStringRoundTripsThroughAstJson()
    {
        var schema = AiJsonSchema.String();

        var json = schema.ToJson();

        Assert.Equal("string", json.GetProperty("kind").GetString());
        var restored = AiJsonSchema.FromJson(json);
        Assert.IsType<AiPrimitiveSchema>(restored);
        Assert.Equal("string", ((AiPrimitiveSchema)restored).Type);
    }

    [Fact]
    public void AiSchemaObjectRoundTripsWithPropertyMetadata()
    {
        var schema = AiJsonSchema.Object(
            AiJsonSchema.Required("narrative", AiJsonSchema.String(), order: 0, description: "story"),
            AiJsonSchema.Optional("actionOptions", AiJsonSchema.Array(AiJsonSchema.String()), order: 1));

        var restored = AiJsonSchema.FromJson(schema.ToJson());

        var objectSchema = Assert.IsType<AiObjectSchema>(restored);
        Assert.Equal(2, objectSchema.Properties.Count);
        Assert.Equal("narrative", objectSchema.Properties[0].Name);
        Assert.Equal(0, objectSchema.Properties[0].Order);
        Assert.Equal("story", objectSchema.Properties[0].Description);
        Assert.False(objectSchema.Properties[0].Optional);
        Assert.True(objectSchema.Properties[1].Optional);
        Assert.IsType<AiArraySchema>(objectSchema.Properties[1].Schema);
    }

    [Fact]
    public void JsonPrimaryOutputRejectsJsonPointerUnsafePropertyName()
    {
        Assert.Throws<ArgumentException>(() => new JsonPrimaryOutput(
            "bad/name", AiJsonSchema.String(), (_, _) => ValueTask.CompletedTask));
        Assert.Throws<ArgumentException>(() => new JsonPrimaryOutput(
            "bad~name", AiJsonSchema.String(), (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void FeaturesCarryCallbacks()
    {
        var timeTags = new TimeTagsFeature(
            (_, _) => ValueTask.CompletedTask,
            onEndTag: null);
        var actionOptions = new ActionOptionsFeature(
            maxCount: 6,
            (_, _) => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask);

        Assert.NotNull(timeTags.OnStartTag);
        Assert.Null(timeTags.OnEndTag);
        Assert.Equal(6, actionOptions.MaxCount);
        Assert.NotNull(actionOptions.OnOptionCompleted);
        Assert.NotNull(actionOptions.OnArrayStarted);
        Assert.NotNull(actionOptions.OnArrayCompleted);
    }

    [Fact]
    public void ExpertCompletionResultDefensivelyCopiesMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["afterThinking"] = "x" };
        var result = new ExpertCompletionResult(metadata);

        metadata["afterThinking"] = "mutated";

        Assert.Equal("x", result.Metadata!["afterThinking"]);
        Assert.IsNotType<Dictionary<string, string>>(result.Metadata);
    }

    [Fact]
    public void JsonStreamEventFactoriesProduceExpectedKinds()
    {
        var chunk = JsonStreamEvent.StringChunk("/content", "hi");
        var arrayStarted = JsonStreamEvent.ArrayStarted("/actionOptions");
        var arrayCompleted = JsonStreamEvent.ArrayCompleted("/actionOptions");

        Assert.Equal("/content", chunk.Path);
        Assert.Equal("hi", Assert.IsType<JsonStreamStringChunkEvent>(chunk).Value);
        Assert.IsType<JsonStreamArrayStartedEvent>(arrayStarted);
        Assert.IsType<JsonStreamArrayCompletedEvent>(arrayCompleted);
    }

    private sealed class ContextSupportFakeFacade : IExpertFacade
    {
        public TAbstract Use<TAbstract>() where TAbstract : AbstractLongTextWritingExpert
            => (TAbstract)(AbstractLongTextWritingExpert)new RecordingLongTextWritingExpert();
    }

    private sealed class InMemoryBucketStub : IHistoryBucket
    {
        public string Description => "main";

        public void AddMessages(string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages)
        {
        }

        public IReadOnlyList<HistoryTurn> GetRawTurns() => [];

        public IReadOnlyList<HistoryProjectionEntry> GetCompressedView(CompressedViewOptions? options = null) => [];
    }
}
