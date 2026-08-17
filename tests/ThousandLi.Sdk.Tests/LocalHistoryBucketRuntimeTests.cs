using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalHistoryBucketRuntimeTests
{
    private static BoundPlayerProfile Player { get; } =
        new(new PlayerId("player-1"), "Creator", "Curious explorer");

    [Fact]
    public async Task BufferedBucketWritesAreVisibleAndCommitAfterSuccessfulRun()
    {
        var buckets = new InMemoryHistoryBucketSet();
        var backend = new DelegateBackend((_, context, _) =>
        {
            var bucket = EnsureBucket(context, "main");
            bucket.AddMessages(null, null, ChatMessage.User("hello"));
            // 读己写：同一 run 内立即可见。
            Assert.Single(bucket.GetRawTurns());
            return ValueTask.CompletedTask;
        });
        var runtime = await CreateRuntimeAsync(backend, buckets);

        await TestSupport.CollectAsync(runtime.HandleActionAsync(TestSupport.Action(), TestSupport.CancellationToken));

        // run 提交后，unbound 读回到提交基线。
        var turn = Assert.Single(buckets["main"].GetRawTurns());
        Assert.Equal("hello", turn.Messages[0].Content);
    }

    [Fact]
    public async Task AbortedRunDiscardsBufferedWritesAndRunCreatedBuckets()
    {
        var buckets = new InMemoryHistoryBucketSet();
        var backend = new DelegateBackend((_, context, _) =>
        {
            var bucket = EnsureBucket(context, "main");
            bucket.AddMessages(null, null, ChatMessage.User("buffered"));
            throw new InvalidOperationException("backend failed");
        });
        var runtime = await CreateRuntimeAsync(backend, buckets);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runtime.HandleActionAsync(
                               TestSupport.Action(), TestSupport.CancellationToken))
            {
            }
        });

        // run 创建的 bucket 随缓冲一起丢弃。
        Assert.Throws<KeyNotFoundException>(() => buckets["main"]);
    }

    [Fact]
    public async Task AbortedRunRestoresOrdinalForCommittedBucket()
    {
        var buckets = new InMemoryHistoryBucketSet();
        var backend = new DelegateBackend((_, context, _) =>
        {
            var bucket = EnsureBucket(context, "main");
            bucket.AddMessages(null, null, ChatMessage.User("payload"));
            return ValueTask.CompletedTask;
        });
        var runtime = await CreateRuntimeAsync(backend, buckets);
        await TestSupport.CollectAsync(runtime.HandleActionAsync(TestSupport.Action(), TestSupport.CancellationToken));

        var failing = new DelegateBackend((_, context, _) =>
        {
            context.Buckets["main"].AddMessages(null, null, ChatMessage.User("lost-on-abort"));
            throw new InvalidOperationException("backend failed");
        });
        runtime = await CreateRuntimeAsync(failing, buckets, sessionId: new SessionId("session-2"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runtime.HandleActionAsync(
                               TestSupport.Action(), TestSupport.CancellationToken))
            {
            }
        });

        // 失败 run 的序号被回滚：重试的 run 不留下序号空洞。
        runtime = await CreateRuntimeAsync(backend, buckets, sessionId: new SessionId("session-3"));
        await TestSupport.CollectAsync(runtime.HandleActionAsync(TestSupport.Action(), TestSupport.CancellationToken));
        Assert.Equal([0L, 1L], buckets["main"].GetRawTurns().Select(turn => turn.TurnOrdinal));
    }

    [Fact]
    public async Task FrontendRequestSeesOnlyCommittedBucketsDuringInflightAction()
    {
        var buckets = new InMemoryHistoryBucketSet();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend(async (_, context, cancellationToken) =>
        {
            context.Buckets.Create("main", "main");
            context.Buckets["main"].AddMessages(null, null, ChatMessage.User("inflight"));
            buffered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        }, handleFrontendRequest: (_, context, _) =>
        {
            try
            {
                _ = context.Buckets["main"].GetRawTurns();
                return ValueTask.FromResult(new FrontendRequestResult(TestSupport.Json("{\"bucketVisible\":true}")));
            }
            catch (KeyNotFoundException)
            {
                return ValueTask.FromResult(new FrontendRequestResult(TestSupport.Json("{\"bucketVisible\":false}")));
            }
        });
        var runtime = await CreateRuntimeAsync(backend, buckets);
        var actionTask = TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));

        await buffered.Task.WaitAsync(TestSupport.CancellationToken);
        var result = await runtime.HandleFrontendRequestAsync(
            new FrontendRequestEnvelope(TestSupport.PlayerId, TestSupport.Json("{}")),
            TestSupport.CancellationToken);

        // 前端请求的 bucket 快照不含 in-flight 缓冲（run 创建的 bucket 也看不到）。
        Assert.False(result.Payload.GetProperty("bucketVisible").GetBoolean());
        release.SetResult();
        await actionTask.WaitAsync(TestSupport.CancellationToken);
    }

    private static ValueTask<LocalGameRuntime> CreateRuntimeAsync(
        IGameBackend backend,
        InMemoryHistoryBucketSet buckets,
        SessionId? sessionId = null) =>
        LocalGameRuntime.CreateAsync(
            "tests_game@1.0.0",
            backend,
            Player,
            new ThrowingExpertExecutor(),
            new InMemoryLocalSessionStore(),
            sessionId ?? new SessionId($"session-{Guid.NewGuid():N}"),
            buckets: buckets,
            cancellationToken: TestSupport.CancellationToken);

    private static IHistoryBucket EnsureBucket(ActionContext context, string name)
    {
        try
        {
            return context.Buckets[name];
        }
        catch (KeyNotFoundException)
        {
            context.Buckets.Create(name, "主叙事历史");
            return context.Buckets[name];
        }
    }
}
