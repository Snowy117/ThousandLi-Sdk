using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class FakeExpertRunnerTests
{
    [Fact]
    public async Task ScenarioReplaysDeterministicallyAndRecordsClonedInput()
    {
        var runner = TestSupport.FakeExpert();
        var firstSink = new RecordingSemanticSink();
        var secondSink = new RecordingSemanticSink();
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{\"value\":1}"));

        var first = await runner.ExecuteAsync(request, firstSink, TestSupport.CancellationToken);
        var second = await runner.ExecuteAsync(request, secondSink, TestSupport.CancellationToken);

        Assert.Equal("complete", first.Output.GetProperty("text").GetString());
        Assert.Equal(first.Output.GetRawText(), second.Output.GetRawText());
        Assert.Equal(firstSink.Events.Select(item => item.Payload.GetRawText()),
            secondSink.Events.Select(item => item.Payload.GetRawText()));
        Assert.Equal(["fake-0001", "fake-0002"], runner.Invocations.Select(item => item.InvocationId));
        Assert.All(runner.Invocations,
            invocation => Assert.Equal(1, invocation.Request.Input.GetProperty("value").GetInt32()));
    }

    [Fact]
    public async Task MissingScenarioIsRejectedBeforeRecordingInvocation()
    {
        var runner = TestSupport.FakeExpert();
        var request = new ExpertInvocationRequest(TestSupport.Contract, "missing", TestSupport.Json("{}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("not registered", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ChannelKeyDoesNotParticipateInScenarioLookupAndIsPreservedInInvocationLog()
    {
        var runner = TestSupport.FakeExpert();
        var request = new ExpertInvocationRequest(
            TestSupport.Contract, "default", TestSupport.Json("{\"value\":1}"), "channel-7");

        var result = await runner.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken);

        Assert.Equal("complete", result.Output.GetProperty("text").GetString());
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("default", invocation.Request.ScenarioId);
        Assert.Equal("channel-7", invocation.Request.ChannelKey);
    }

    [Theory]
    [InlineData(2, 0, "tests-story-v1")]
    [InlineData(1, 0, "different")]
    public async Task IncompatibleContractIsRejected(int major, int minor, string fingerprint)
    {
        var runner = TestSupport.FakeExpert();
        var required = new ExpertContractDescriptor("tests/story", new ContractVersion(major, minor), fingerprint);
        var request = new ExpertInvocationRequest(required, "default", TestSupport.Json("{}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("contract mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void UndefinedJsonIsRejectedAtContractBoundary()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertInvocationRequest(TestSupport.Contract, "default", default));
        Assert.Throws<ArgumentException>(() =>
            new PlayerActionEnvelope(TestSupport.PlayerId, default));
    }
}
