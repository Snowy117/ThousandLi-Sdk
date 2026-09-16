using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class HistoryBucketContractTests
{
    [Fact]
    public void IndexerOnUncreatedBucketThrowsKeyNotFound()
    {
        var set = new InMemoryHistoryBucketSet();

        var exception = Assert.Throws<KeyNotFoundException>(() => set["main"]);

        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public void CreateIsNonIdempotent()
    {
        var set = new InMemoryHistoryBucketSet();

        set.Create("main", "主叙事历史");

        Assert.Throws<InvalidOperationException>(() => set.Create("main", "主叙事历史"));
        Assert.Equal("主叙事历史", set["main"].Description);
    }

    [Fact]
    public void IdempotentProbeCreatePatternMatchesXuezhixiaC6()
    {
        var set = new InMemoryHistoryBucketSet();

        EnsureBucket(set, "main");
        EnsureBucket(set, "main");

        Assert.NotNull(set["main"]);
    }

    [Fact]
    public async Task AddMessagesAssignsMonotonicTurnOrdinalsFromZero()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "主叙事历史");

        set["main"].AddMessages(null, null, ChatMessage.User("first"));
        set["main"].AddMessages(null, null, ChatMessage.User("second"), ChatMessage.Assistant("reply"));
        set["main"].AddMessages(null, null, ChatMessage.Assistant("third"));

        var turns = await set["main"].GetRawTurnsAsync(CancellationToken.None);
        Assert.Equal([0L, 1L, 2L], turns.Select(turn => turn.TurnOrdinal));
        Assert.Equal(2, turns[1].Messages.Count);
        Assert.Equal("first", turns[0].Messages[0].Content);
        Assert.Equal(ChatMessageRole.User, turns[0].Messages[0].Role);
    }

    [Fact]
    public async Task TurnMetadataRoundTripsSeparateFromDigest()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "main");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["afterThinking"] = "t" };

        set["main"].AddMessages("digest-1", metadata, ChatMessage.User("hi"));

        var turn = Assert.Single(await set["main"].GetRawTurnsAsync(CancellationToken.None));
        Assert.Equal("digest-1", turn.Digest);
        Assert.Equal("t", turn.Metadata!["afterThinking"]);
        // 不可 downcast 后修改不可变账本。
        Assert.IsNotType<Dictionary<string, string>>(turn.Metadata);
    }

    [Fact]
    public async Task CompressedViewReturnsOneRawTurnPerTurn()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "main");
        set["main"].AddMessages(null, null, ChatMessage.User("a"));
        set["main"].AddMessages(null, null, ChatMessage.Assistant("b"));

        var view = await set["main"].GetCompressedViewAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, view.Count);
        Assert.All(view, entry => Assert.IsType<HistoryProjectionRawTurn>(entry));
    }

    [Fact]
    public async Task AsyncBucketReadsObserveTheCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var inner = new CancelAwaitingBucket();
        // 专家执行期间的真实路径：桶经 ReadOnlyHistoryBucket 包装后读取。
        var wrapped = new ReadOnlyHistoryBucket(inner);

        var readTask = wrapped.GetRawTurnsAsync(cancellationSource.Token);
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readTask);
        // 取消传播到被包装桶收到的是调用方令牌，而非包装层自造的令牌。
        Assert.True(inner.ObserveRawTurnsCancellation);
    }

    [Fact]
    public void AddMessagesWithNoMessagesThrows()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "main");

        Assert.Throws<ArgumentException>(() => set["main"].AddMessages(null, null));
    }

    [Fact]
    public void ChatMessageFactoriesProduceRoles()
    {
        Assert.Equal(ChatMessageRole.System, ChatMessage.System("s").Role);
        Assert.Equal(ChatMessageRole.User, ChatMessage.User("u").Role);
        Assert.Equal(ChatMessageRole.Assistant, ChatMessage.Assistant("a").Role);
        Assert.Throws<ArgumentException>(() => ChatMessage.User("  "));
    }

    /// <summary>
    /// 取消传播探针桶：异步读取挂起直到调用方令牌取消，随后观察并重抛
    /// <see cref="OperationCanceledException" />；用于断言取消沿异步读取面传播。
    /// </summary>
    private sealed class CancelAwaitingBucket : IHistoryBucket
    {
        public bool ObserveRawTurnsCancellation { get; private set; }

        public string Description => "cancel-probe";

        public void AddMessages(
            string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages) =>
            throw new NotSupportedException("The cancellation probe bucket is read-only.");

        public async ValueTask<IReadOnlyList<HistoryTurn>> GetRawTurnsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObserveRawTurnsCancellation = true;
                throw;
            }

            // Task.Delay(Timeout.Infinite) 永不正常完成；此返回仅为满足编译器路径完备性。
            return [];
        }

        public ValueTask<IReadOnlyList<HistoryProjectionEntry>> GetCompressedViewAsync(
            CompressedViewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryProjectionEntry>>([]);
        }
    }

    private static void EnsureBucket(InMemoryHistoryBucketSet set, string name)
    {
        try
        {
            _ = set[name];
            return;
        }
        catch (KeyNotFoundException)
        {
        }

        set.Create(name, "主叙事历史");
    }
}
