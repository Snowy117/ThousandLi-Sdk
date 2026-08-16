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
    public void AddMessagesAssignsMonotonicTurnOrdinalsFromZero()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "主叙事历史");

        set["main"].AddMessages(null, null, ChatMessage.User("first"));
        set["main"].AddMessages(null, null, ChatMessage.User("second"), ChatMessage.Assistant("reply"));
        set["main"].AddMessages(null, null, ChatMessage.Assistant("third"));

        var turns = set["main"].GetRawTurns();
        Assert.Equal([0L, 1L, 2L], turns.Select(turn => turn.TurnOrdinal));
        Assert.Equal(2, turns[1].Messages.Count);
        Assert.Equal("first", turns[0].Messages[0].Content);
        Assert.Equal(ChatMessageRole.User, turns[0].Messages[0].Role);
    }

    [Fact]
    public void TurnMetadataRoundTripsSeparateFromDigest()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "main");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["afterThinking"] = "t" };

        set["main"].AddMessages("digest-1", metadata, ChatMessage.User("hi"));

        var turn = Assert.Single(set["main"].GetRawTurns());
        Assert.Equal("digest-1", turn.Digest);
        Assert.Equal("t", turn.Metadata!["afterThinking"]);
        // 不可 downcast 后修改不可变账本。
        Assert.IsNotType<Dictionary<string, string>>(turn.Metadata);
    }

    [Fact]
    public void CompressedViewReturnsOneRawTurnPerTurn()
    {
        var set = new InMemoryHistoryBucketSet();
        set.Create("main", "main");
        set["main"].AddMessages(null, null, ChatMessage.User("a"));
        set["main"].AddMessages(null, null, ChatMessage.Assistant("b"));

        var view = set["main"].GetCompressedView();

        Assert.Equal(2, view.Count);
        Assert.All(view, entry => Assert.IsType<HistoryProjectionRawTurn>(entry));
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
