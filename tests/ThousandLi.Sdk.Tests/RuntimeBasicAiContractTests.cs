using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class RuntimeBasicAiContractTests
{
    private static AiJsonSchema TargetSchema => AiJsonSchema.Object(AiJsonSchema.Required("narrative", AiJsonSchema.String()));

    [Fact]
    public void MessageFactoriesAssignRoles()
    {
        Assert.Equal(BasicAiMessageRole.System, BasicAiMessage.System("s").Role);
        Assert.Equal(BasicAiMessageRole.User, BasicAiMessage.User("u").Role);
        Assert.Equal(BasicAiMessageRole.Assistant, BasicAiMessage.Assistant("a").Role);
        Assert.Equal("u", BasicAiMessage.User("u").Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MessageRejectsBlankContent(string? content)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BasicAiMessage(BasicAiMessageRole.User, content!));
    }

    [Fact]
    public void ToolDescriptorRejectsBlankNamesAndNullSchemas()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new BasicAiToolDescriptor("", "describes a tool", AiJsonSchema.Object()));
        Assert.ThrowsAny<ArgumentException>(() =>
            new BasicAiToolDescriptor("name", " ", AiJsonSchema.Object()));
        Assert.Throws<ArgumentNullException>(() =>
            new BasicAiToolDescriptor("name", "describes a tool", null!));
    }

    [Fact]
    public void ToolDescriptorStoresAllFields()
    {
        var schema = AiJsonSchema.Object();

        var descriptor = new BasicAiToolDescriptor("roll_dice", "Rolls a dice.", schema);

        Assert.Equal("roll_dice", descriptor.Name);
        Assert.Equal("Rolls a dice.", descriptor.Description);
        Assert.Same(schema, descriptor.ParametersSchema);
    }

    [Fact]
    public void ModelDescriptorRejectsBlankModelIds()
    {
        Assert.ThrowsAny<ArgumentException>(() => new BasicAiModelDescriptor(" "));
    }

    [Fact]
    public void RequestRejectsBlankModelIds()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new BasicAiRequest("", [BasicAiMessage.User("hi")], TargetSchema));
    }

    [Fact]
    public void RequestRejectsEmptyAndNullMessageEntries()
    {
        Assert.Throws<ArgumentException>(() => new BasicAiRequest("m1", [], TargetSchema));
        Assert.Throws<ArgumentException>(() =>
            new BasicAiRequest("m1", [BasicAiMessage.User("hi"), null!], TargetSchema));
    }

    [Fact]
    public void RequestRejectsNullTargetSchema()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BasicAiRequest("m1", [BasicAiMessage.User("hi")], null!));
    }

    [Fact]
    public void RequestRejectsNullToolEntries()
    {
        Assert.Throws<ArgumentException>(() => new BasicAiRequest(
            "m1",
            [BasicAiMessage.User("hi")],
            TargetSchema,
            tools: [null!]));
    }

    [Fact]
    public void RequestDefensivelyCopiesMessagesAndTools()
    {
        var messages = new List<BasicAiMessage> { BasicAiMessage.User("hi") };
        var tools = new List<BasicAiToolDescriptor>
        {
            new("tool", "describes a tool", AiJsonSchema.Object())
        };
        var request = new BasicAiRequest("m1", messages, TargetSchema, tools);
        messages.Add(BasicAiMessage.User("mutated"));
        tools.Add(new BasicAiToolDescriptor("mutated", "mutated", AiJsonSchema.Object()));

        Assert.Single(request.Messages);
        Assert.Single(request.Tools);
        Assert.Equal("hi", request.Messages[0].Content);
        Assert.Equal("tool", request.Tools[0].Name);
    }

    [Fact]
    public void RequestCarriesSamplingAndEmptyToolsDefault()
    {
        var request = new BasicAiRequest("m1", [BasicAiMessage.User("hi")], TargetSchema);
        Assert.Empty(request.Tools);
        Assert.Null(request.Sampling);

        var sampling = new BasicAiSamplingParameters { Temperature = 0.7f, TopP = 0.9f };
        var sampled = new BasicAiRequest("m1", [BasicAiMessage.User("hi")], TargetSchema, sampling: sampling);
        Assert.NotNull(sampled.Sampling);
        Assert.Equal(0.7f, sampled.Sampling?.Temperature);
        Assert.Equal(0.9f, sampled.Sampling?.TopP);
    }

    [Fact]
    public void CompletionResultRejectsUndefinedJson()
    {
        var undefined = default(JsonElement);
        Assert.Throws<ArgumentException>(() => new BasicAiCompletionResult(undefined));
    }

    [Fact]
    public void CompletionResultClonesItsJson()
    {
        var document = JsonDocument.Parse("""{"answer":42}""");
        var result = new BasicAiCompletionResult(document.RootElement);
        document.Dispose();

        Assert.Equal(42, result.Json.GetProperty("answer").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CompletionResultRejectsBlankReasoning(string reasoning)
    {
        Assert.Throws<ArgumentException>(() => new BasicAiCompletionResult(TestSupport.Json("{}"), reasoning));
    }

    [Fact]
    public void CompletionResultAcceptsNullReasoning()
    {
        var result = new BasicAiCompletionResult(TestSupport.Json("""{"answer":42}"""));
        Assert.Null(result.Reasoning);
    }

    [Fact]
    public void ReasoningStreamEventRejectsNullAndEmptyDeltas()
    {
        Assert.Throws<ArgumentNullException>(() => new BasicAiReasoningStreamEvent(null!));
        Assert.Throws<ArgumentException>(() => new BasicAiReasoningStreamEvent(""));
    }

    [Fact]
    public void ReasoningStreamEventAcceptsWhitespaceDeltas()
    {
        var streamEvent = new BasicAiReasoningStreamEvent(" ");
        Assert.Equal(" ", streamEvent.Delta);
    }

    [Fact]
    public void JsonStreamEventWrapperCarriesTheWrappedEvent()
    {
        var wrapped = JsonStreamEvent.StringChunk("/narrative", "hello");
        var streamEvent = new BasicAiJsonStreamEvent(wrapped);
        Assert.Same(wrapped, streamEvent.Event);
    }
}
