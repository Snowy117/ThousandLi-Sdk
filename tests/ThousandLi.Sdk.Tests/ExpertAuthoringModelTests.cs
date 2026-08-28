using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertAuthoringModelTests
{
    [Fact]
    public void MessagesValidateRoleAndContent()
    {
        Assert.Throws<ArgumentException>(() => new LocalBasicAiMessage(" ", "content"));
        Assert.Throws<ArgumentNullException>(() => new LocalBasicAiMessage(null!, "content"));
        Assert.Throws<ArgumentNullException>(() => new LocalBasicAiMessage("user", null!));
        Assert.Equal("  ", new LocalBasicAiMessage("user", "  ").Content);
        Assert.Equal("user", new LocalBasicAiMessage("user", "hi").Role);
    }

    [Fact]
    public void RequestsValidateModelMessagesModeAndCopyDefensively()
    {
        List<LocalBasicAiMessage> messages = [new("user", "hi")];
        Assert.Throws<ArgumentException>(() => new LocalBasicAiRequest(" ", messages));
        Assert.Throws<ArgumentNullException>(() => new LocalBasicAiRequest("m1", null!));
        Assert.Throws<ArgumentException>(() => new LocalBasicAiRequest("m1", []));
        Assert.Throws<ArgumentException>(
            () => new LocalBasicAiRequest("m1", [new LocalBasicAiMessage("user", "hi"), null!]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalBasicAiRequest("m1", messages, (LocalBasicAiResponseMode)99));

        var request = new LocalBasicAiRequest("m1", messages);
        messages.Add(new LocalBasicAiMessage("user", "mutated"));
        Assert.Single(request.Messages);
    }

    [Fact]
    public void CompletionResultsValidateText()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalBasicAiCompletionResult(null!));
        Assert.Equal(string.Empty, new LocalBasicAiCompletionResult(string.Empty).Text);
    }

    [Fact]
    public void HistoryTurnsValidateRoleContentAndCopyMetadata()
    {
        Assert.Throws<ArgumentException>(() => new ExpertHistoryTurn(" ", "content"));
        Assert.Throws<ArgumentNullException>(() => new ExpertHistoryTurn("player", null!));
        Assert.Equal(string.Empty, new ExpertHistoryTurn("player", string.Empty).Content);

        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var turn = new ExpertHistoryTurn("player", "hello", metadata);
        metadata["k"] = "mutated";
        Assert.Equal("v", turn.Metadata!["k"]);
        Assert.Null(new ExpertHistoryTurn("player", "hello").Metadata);
    }

    [Fact]
    public void CompletionResultMetadataIsCopiedAndOptional()
    {
        Assert.Null(new ExpertCompletionResult().Metadata);

        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var result = new ExpertCompletionResult(metadata);
        metadata["k"] = "mutated";
        Assert.Equal("v", result.Metadata!["k"]);
    }

    [Fact]
    public void PrimaryOutputsValidateTheirInputs()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TextPrimaryOutput(null!));
        Assert.Throws<ArgumentException>(
            () => new TextPrimaryOutput((_, _) => ValueTask.CompletedTask, " "));
        Assert.Throws<ArgumentException>(() => new JsonPrimaryOutput(" ", (_, _) => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new JsonPrimaryOutput("out", null!));
    }
}
