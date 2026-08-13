using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalGameRuntimeTests
{
    [Fact]
    public async Task SuccessfulActionStreamsInOrderAndCommitsState()
    {
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            await context.Frontend.WriteAsync(
                new FrontendEvent("narrative", TestSupport.Json("{\"text\":\"hello\"}")),
                cancellationToken);
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend);

        var events = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));

        Assert.Equal(
            [ActionRuntimeEventKind.Started, ActionRuntimeEventKind.FrontendEvent, ActionRuntimeEventKind.Committed],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        Assert.Equal(1, runtime.CommittedState.GetProperty("turn").GetInt32());
        var action = Assert.Single(runtime.CommittedActions);
        Assert.Equal(0, action.PreviousState.GetProperty("turn").GetInt32());
        Assert.Equal(1, action.CommittedState.GetProperty("turn").GetInt32());
        Assert.Single(action.FrontendEvents);
    }

    [Fact]
    public async Task BackendFailureStreamsAbortedAndRollsBackState()
    {
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            await context.Frontend.WriteAsync(new FrontendEvent("optimistic", TestSupport.Json("{}")), cancellationToken);
            throw new InvalidOperationException("backend failed");
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend);
        var events = new List<ActionRuntimeEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(
                               TestSupport.Action(), TestSupport.CancellationToken))
                events.Add(runtimeEvent);
        });

        Assert.Equal("backend failed", exception.Message);
        Assert.Equal(
            [ActionRuntimeEventKind.Started, ActionRuntimeEventKind.FrontendEvent, ActionRuntimeEventKind.Aborted],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        Assert.Equal(ActionTerminalStatus.Failed, events[^1].TerminalStatus);
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());
        Assert.Empty(runtime.CommittedActions);
    }

    [Fact]
    public async Task CommitSaveFailureStreamsAbortedAndKeepsPreviousCommittedState()
    {
        var store = new FailingSaveStore(new InMemoryLocalSessionStore(), failingSaveNumber: 2);
        var backend = new DelegateBackend((_, context, _) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            return ValueTask.CompletedTask;
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend, store);
        var events = new List<ActionRuntimeEvent>();

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(
                               TestSupport.Action(), TestSupport.CancellationToken))
                events.Add(runtimeEvent);
        });

        Assert.Equal("simulated save failure", exception.Message);
        Assert.Equal([ActionRuntimeEventKind.Started, ActionRuntimeEventKind.Aborted],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());
        Assert.Empty(runtime.CommittedActions);
    }

    [Fact]
    public async Task CancellationAfterStartedStreamsAbortedAndRollsBackState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend);
        using var cancellation = new CancellationTokenSource();
        var actionCancellationToken = cancellation.Token;
        var events = new List<ActionRuntimeEvent>();

        var action = Task.Run(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(TestSupport.Action(), actionCancellationToken))
                events.Add(runtimeEvent);
        }, TestSupport.CancellationToken);
        await entered.Task.WaitAsync(TestSupport.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await action.WaitAsync(TestSupport.CancellationToken));
        Assert.Equal([ActionRuntimeEventKind.Started, ActionRuntimeEventKind.Aborted],
            events.Select(runtimeEvent => runtimeEvent.Kind));
        Assert.Equal(ActionTerminalStatus.Aborted, events[^1].TerminalStatus);
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());
        Assert.Empty(runtime.CommittedActions);
    }

    [Fact]
    public async Task FrontendEventIsObservableBeforeActionCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wroteEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            await context.Frontend.WriteAsync(new FrontendEvent("optimistic", TestSupport.Json("{}")), cancellationToken);
            wroteEvent.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend);
        var collected = new List<ActionRuntimeEvent>();
        var collection = Task.Run(async () =>
        {
            await foreach (var runtimeEvent in runtime.HandleActionAsync(
                               TestSupport.Action(), TestSupport.CancellationToken))
            {
                collected.Add(runtimeEvent);
                if (runtimeEvent.Kind is ActionRuntimeEventKind.FrontendEvent) observedEvent.SetResult();
            }
        }, TestSupport.CancellationToken);

        await wroteEvent.Task.WaitAsync(TestSupport.CancellationToken);
        await observedEvent.Task.WaitAsync(TestSupport.CancellationToken);
        Assert.Equal([ActionRuntimeEventKind.Started, ActionRuntimeEventKind.FrontendEvent],
            collected.Select(runtimeEvent => runtimeEvent.Kind));
        Assert.Equal(0, runtime.CommittedState.GetProperty("turn").GetInt32());

        release.SetResult();
        await collection.WaitAsync(TestSupport.CancellationToken);
        Assert.Equal(ActionRuntimeEventKind.Committed, collected[^1].Kind);
    }

    [Fact]
    public async Task FrontendRequestSeesCommittedSnapshotDuringInflightAction()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateMutated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.State.Replace(new JsonPointer("/turn"), TestSupport.Json("1"));
            stateMutated.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var runtime = await TestSupport.CreateRuntimeAsync(backend);
        var actionTask = TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));

        await stateMutated.Task.WaitAsync(TestSupport.CancellationToken);
        var response = await runtime.HandleFrontendRequestAsync(
            new FrontendRequestEnvelope(TestSupport.PlayerId, TestSupport.Json("{}")),
            TestSupport.CancellationToken);

        Assert.Equal(0, response.Payload.GetProperty("turn").GetInt32());
        release.SetResult();
        await actionTask.WaitAsync(TestSupport.CancellationToken);
    }

    [Fact]
    public async Task SuccessfulActionsReceiveUniqueSequences()
    {
        var runtime = await TestSupport.CreateRuntimeAsync(new DelegateBackend());

        var first = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));
        var second = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));

        Assert.Equal("action-00000001", first[0].ActionId.Value);
        Assert.Equal("run-00000001", first[0].ActionRunId.Value);
        Assert.Equal("action-00000002", second[0].ActionId.Value);
        Assert.Equal("run-00000002", second[0].ActionRunId.Value);
    }
}
