using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class FakeExpertExecutorTests
{
    [Fact]
    public async Task ScenarioReplaysDeterministicallyAndRecordsClonedInput()
    {
        var executor = TestSupport.FakeExpert();
        var firstSink = new RecordingSemanticSink();
        var secondSink = new RecordingSemanticSink();
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{\"value\":1}"));

        var first = await executor.ExecuteAsync(request, firstSink, TestSupport.CancellationToken);
        var second = await executor.ExecuteAsync(request, secondSink, TestSupport.CancellationToken);

        Assert.Equal("complete", first.Output.GetProperty("text").GetString());
        Assert.Equal(first.Output.GetRawText(), second.Output.GetRawText());
        Assert.Equal(firstSink.Events.Select(item => item.Payload.GetRawText()),
            secondSink.Events.Select(item => item.Payload.GetRawText()));
        Assert.Equal(["fake-0001", "fake-0002"], executor.Invocations.Select(item => item.InvocationId));
        Assert.All(executor.Invocations,
            invocation => Assert.Equal(1, invocation.Request.Input.GetProperty("value").GetInt32()));
    }

    [Fact]
    public async Task MissingScenarioIsRejectedBeforeRecordingInvocation()
    {
        var executor = TestSupport.FakeExpert();
        var request = new ExpertInvocationRequest(TestSupport.Contract, "missing", TestSupport.Json("{}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("not registered", exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Theory]
    [InlineData(2, 0, "tests-narrator-v1")]
    [InlineData(1, 0, "different")]
    public async Task IncompatibleContractIsRejected(int major, int minor, string fingerprint)
    {
        var executor = TestSupport.FakeExpert();
        var required = new ExpertContractDescriptor("tests/narrator", new ContractVersion(major, minor), fingerprint);
        var request = new ExpertInvocationRequest(required, "default", TestSupport.Json("{}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("contract mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
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
