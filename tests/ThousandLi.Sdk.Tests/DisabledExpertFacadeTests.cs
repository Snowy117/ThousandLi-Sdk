using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class DisabledExpertFacadeTests
{
    [Fact]
    public void UseFailsFastWithTheRemoteSliceGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DisabledExpertFacade.Instance.Use<AbstractLongTextWritingExpert>());

        Assert.Contains(typeof(AbstractLongTextWritingExpert).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.4.0-preview.2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Playground", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteGameFacadeFailsFastThroughTheRuntimeSeamAsAFailedAction()
    {
        var backend = new DelegateBackend(handleAction: (_, context, _) =>
        {
            _ = context.Experts.Use<AbstractLongTextWritingExpert>();
            return ValueTask.CompletedTask;
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend, experts: DisabledExpertFacade.Instance);
        var events = new List<ActionRuntimeEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(
                               TestSupport.Action("""{"choice":"advance"}"""), TestSupport.CancellationToken))
                events.Add(runtimeEvent);
        });

        Assert.Contains("0.4.0-preview.2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Playground", exception.Message, StringComparison.Ordinal);
        Assert.Equal([ActionRuntimeEventKind.Started, ActionRuntimeEventKind.Aborted],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        var aborted = events[^1];
        Assert.Equal(ActionTerminalStatus.Failed, aborted.TerminalStatus);
        Assert.Contains("0.4.0-preview.2", aborted.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Playground", aborted.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());
    }
}
