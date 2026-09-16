using ThousandLi.Contracts;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Facade shape tests: every <c>Use</c> call returns a fresh, already-bound proxy expert bound to a
/// shared remote execution context (mirroring the local facade contract).
/// </summary>
public sealed class RemoteExpertFacadeTests
{
    private static RemoteExpertFacade CreateFacade(FakeRemoteHttpHandler handler) => new(
        new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
        new RemoteInvocationRunnerOptions());

    [Fact]
    public void UseReturnsAFreshBoundProxyPerCall()
    {
        var facade = CreateFacade(new FakeRemoteHttpHandler());

        var first = facade.Use<AbstractLongTextWritingExpert>();
        var second = facade.Use<AbstractLongTextWritingExpert>();

        Assert.NotSame(first, second);
        Assert.IsType<RemoteLongTextWritingExpert>(first);
        Assert.IsType<RemoteLongTextWritingExpert>(second);
    }

    [Fact]
    public async Task UseReturnsAProxiedInstanceReadyToExecute()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(
            "[{\"contractId\":\"thousandli.expert/long-text-writing\",\"name\":\"LongTextWriting\"," +
            "\"description\":\"\"}]");
        handler.EnqueueJson(
            "{\"expertInvocationId\":\"inv-1\",\"status\":\"running\",\"replayed\":false," +
            "\"contractId\":\"thousandli.expert/long-text-writing\",\"expertPackageId\":\"pkg\"," +
            "\"lastEventOrdinal\":-1,\"createdAt\":\"2026-08-30T10:00:00Z\"}",
            System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            "id: 0\ndata: {\"type\":\"event\",\"eventType\":\"chunk\",\"payload\":{\"text\":\"好\"}}\n\n" +
            "data: {\"type\":\"completed\",\"output\":{\"primary\":\"好\"}}\n\n");
        var facade = new RemoteExpertFacade(
            new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
            new RemoteInvocationRunnerOptions(
                new Dictionary<string, string> { ["thousandli.expert/long-text-writing"] = "pkg" }));

        var chunks = new List<string>();
        var expert = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) =>
        {
            chunks.Add(evt.Delta);
            return ValueTask.CompletedTask;
        }));

        var result = await expert.StreamAsync(TestSupport.CancellationToken);

        Assert.Equal(["好"], chunks);
        Assert.Null(result.Metadata);
        Assert.Null(result.Reasoning);
    }

    [Fact]
    public void BoundProxyInstancesRejectRebinding()
    {
        var facade = CreateFacade(new FakeRemoteHttpHandler());

        var expert = facade.Use<AbstractLongTextWritingExpert>();

        Assert.Throws<InvalidOperationException>(() =>
            expert.Bind(new RemoteExpertExecutionContext()));
    }

    [Fact]
    public void TheRemoteContextFailsFastOnBasicAiAccess()
    {
        var context = new RemoteExpertExecutionContext();

        Assert.Throws<NotSupportedException>(() => _ = context.BasicAi);
        Assert.NotNull(context.PlayerProfile);
        Assert.NotNull(context.Logger);
    }
}
