using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// A facade whose <c>Use</c> fails must surface through the runtime seam as a failed action
/// (<c>Started → Aborted</c>) with committed state unchanged — the generic contract behind every
/// unconfigured expert mode.
/// </summary>
public sealed class UnconfiguredExpertFacadeRuntimeSeamTests
{
    [Fact]
    public async Task ThrowingFacadeSurfacesAsAFailedActionWithUnchangedState()
    {
        var backend = new DelegateBackend(handleAction: (_, context, _) =>
        {
            _ = context.Experts.Use<AbstractLongTextWritingExpert>();
            return ValueTask.CompletedTask;
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend, experts: ThrowingExpertFacade.Instance);
        var events = new List<ActionRuntimeEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(
                               TestSupport.Action("""{"choice":"advance"}"""), TestSupport.CancellationToken))
                events.Add(runtimeEvent);
        });

        Assert.Contains(typeof(AbstractLongTextWritingExpert).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            [ActionRuntimeEventKind.Started, ActionRuntimeEventKind.Aborted],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        var aborted = events[^1];
        Assert.Equal(ActionTerminalStatus.Failed, aborted.TerminalStatus);
        Assert.NotNull(aborted.ErrorMessage);
        Assert.Contains("not configured", aborted.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());
    }
}
