using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertInvocationRequestTests
{
    [Fact]
    public void LegacyConstructorPreservesScenarioKeySemantics()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{\"value\":1}"));

        Assert.Equal("default", request.ScenarioId);
        Assert.Null(request.ChannelKey);
        Assert.Equal(1, request.Input.GetProperty("value").GetInt32());
    }

    [Fact]
    public void LegacyConstructorStillRejectsBlankScenarioKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertInvocationRequest(TestSupport.Contract, " ", TestSupport.Json("{}")));
    }

    [Fact]
    public void ChannelConstructorAllowsAbsentScenarioKey()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "playground-42");

        Assert.Null(request.ScenarioId);
        Assert.Equal("playground-42", request.ChannelKey);
    }

    [Fact]
    public void ChannelConstructorCarriesScenarioAndChannelKeyTogether()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{}"), "channel-7");

        Assert.Equal("default", request.ScenarioId);
        Assert.Equal("channel-7", request.ChannelKey);
    }

    [Fact]
    public void ChannelConstructorAllowsAbsentChannelKey()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{}"), null);

        Assert.Equal("default", request.ScenarioId);
        Assert.Null(request.ChannelKey);
    }

    [Fact]
    public void ChannelConstructorRejectsBlankScenarioKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertInvocationRequest(TestSupport.Contract, " ", TestSupport.Json("{}"), "channel-7"));
    }

    [Fact]
    public void ChannelConstructorRejectsBlankChannelKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), " "));
    }

    [Fact]
    public void ChannelConstructorRejectsNullContract()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExpertInvocationRequest(null!, "default", TestSupport.Json("{}"), "channel-7"));
    }

    [Fact]
    public void LegacyConstructorRejectsNullContract()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExpertInvocationRequest(null!, "default", TestSupport.Json("{}")));
    }

    [Fact]
    public void ChannelConstructorAllowsBareInvocationWithoutScenarioOrChannelKey()
    {
        // The request is a carrier shape shared by Fake/Local/Remote channels; whether a key is
        // required is executor policy (Fake executors reject a missing scenario key themselves).
        var request = new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), null);

        Assert.Null(request.ScenarioId);
        Assert.Null(request.ChannelKey);
    }

    [Fact]
    public void ChannelConstructorRejectsUndefinedInput()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertInvocationRequest(TestSupport.Contract, null, default, "channel-7"));
    }

    [Fact]
    public void BothConstructorsCloneInputIndependentlyOfTheSourceDocument()
    {
        var document = JsonDocument.Parse("{\"value\":7}");
        var legacy = new ExpertInvocationRequest(TestSupport.Contract, "default", document.RootElement);
        var channel = new ExpertInvocationRequest(
            TestSupport.Contract, "default", document.RootElement, "channel-7");

        document.Dispose();

        Assert.Equal(7, legacy.Input.GetProperty("value").GetInt32());
        Assert.Equal(7, channel.Input.GetProperty("value").GetInt32());
    }

    [Fact]
    public void RecordRoundTripsLegacyShape()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{\"value\":3}"));

        var record = new ExpertInvocationRecord(request, "inv-0001");

        Assert.Equal("default", record.Request.ScenarioId);
        Assert.Null(record.Request.ChannelKey);
        Assert.Equal(3, record.Request.Input.GetProperty("value").GetInt32());
        Assert.Equal(request, record.Request);
    }

    [Fact]
    public void RecordRoundTripsChannelShape()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{\"value\":4}"), "channel-9");

        var record = new ExpertInvocationRecord(request, "inv-0002");

        Assert.Null(record.Request.ScenarioId);
        Assert.Equal("channel-9", record.Request.ChannelKey);
        Assert.Equal(4, record.Request.Input.GetProperty("value").GetInt32());
        Assert.Equal(request, record.Request);
    }

    [Fact]
    public void RecordRejectsNullRequestAndBlankInvocationId()
    {
        var request = new ExpertInvocationRequest(TestSupport.Contract, "default", TestSupport.Json("{}"));

        Assert.Throws<ArgumentNullException>(() => new ExpertInvocationRecord(null!, "inv-0001"));
        Assert.Throws<ArgumentException>(() => new ExpertInvocationRecord(request, " "));
    }

    [Fact]
    public async Task FakeExecutorRejectsChannelOnlyRequestWithClearDiagnostic()
    {
        var executor = TestSupport.FakeExpert();
        var request = new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("scenario key", exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestSupport.Contract.Id, exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task ScriptedFakeExecutorRejectsChannelOnlyRequestWithClearDiagnostic()
    {
        var executor = ScriptedFakeExpertExecutor.Load(null);
        var request = new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("scenario key", exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestSupport.Contract.Id, exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public void RuntimeContractVersionIsAdditiveMinor()
    {
        Assert.Equal(new ContractVersion(1, 1), SdkContracts.Runtime);
        Assert.Equal(new ContractVersion(1, 0), SdkContracts.Frontend);
    }
}
