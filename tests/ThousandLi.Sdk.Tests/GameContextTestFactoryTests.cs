using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class GameContextTestFactoryTests
{
    private sealed class SampleSettings
    {
        public string Mode { get; init; } = "normal";

        public int Level { get; init; }
    }

    [Fact]
    public void CreateAppliesDeterministicDefaults()
    {
        var context = ActionContextTestFactory.Create();

        Assert.Equal(new SessionId("test-session"), context.SessionId);
        Assert.Equal(new BranchId("test-branch"), context.BranchId);
        Assert.Equal(new ActionId("test-action"), context.ActionId);
        Assert.Equal(new ActionRunId("test-action-run"), context.ActionRunId);
        Assert.Equal(new PlayerId("test-player"), context.PlayerProfile.PlayerId);
        Assert.Equal("TestPlayer", context.PlayerProfile.PlayerName);
        Assert.Equal(JsonValueKind.Object, context.State.Snapshot.ValueKind);
        Assert.Empty(context.State.Snapshot.EnumerateObject());
        Assert.IsType<InMemoryHistoryBucketSet>(context.Buckets);
        Assert.NotNull(context.Logger);
    }

    [Fact]
    public async Task DefaultFrontendDiscardsEventsAndDefaultHistoryIsEmpty()
    {
        var context = ActionContextTestFactory.Create();

        await context.Frontend.WriteAsync(
            new FrontendEvent("tick", JsonSerializer.SerializeToElement(new { value = 1 })),
            TestSupport.CancellationToken);
        var history = await context.History.GetRecentPlayerActionsAsync(5, TestSupport.CancellationToken);

        Assert.Empty(history);
    }

    [Fact]
    public void DefaultExpertsFailWithClearMessages()
    {
        var context = ActionContextTestFactory.Create();

        var facadeError = Assert.Throws<InvalidOperationException>(
            () => context.Experts.Use<AbstractLongTextWritingExpert>());
        Assert.Contains("not configured for this test context", facadeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameSettingsFallsBackToDefaultInstanceWhenNothingIsInjected()
    {
        var context = ActionContextTestFactory.Create();

        var settings = await context.GetGameSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

        Assert.Equal("normal", settings.Mode);
        Assert.Equal(0, settings.Level);
    }

    [Fact]
    public async Task GetGameSettingsReadsFromInjectedStore()
    {
        var store = new InMemoryGameSettingsStore();
        await store.SetAsync(
            JsonSerializer.SerializeToElement(new SampleSettings { Mode = "hard", Level = 3 }),
            TestSupport.CancellationToken);
        var context = ActionContextTestFactory.Create(gameSettingsStore: store);

        var settings = await context.GetGameSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

        Assert.Equal("hard", settings.Mode);
        Assert.Equal(3, settings.Level);
    }

    [Fact]
    public async Task GetGameSettingsPrefersResolverDelegateOverStore()
    {
        var custom = new SampleSettings { Mode = "delegated" };
        var store = new InMemoryGameSettingsStore();
        await store.SetAsync(
                JsonSerializer.SerializeToElement(new SampleSettings { Mode = "stored" }),
                TestSupport.CancellationToken);
        var context = ActionContextTestFactory.Create(
            gameSettingsStore: store,
            getGameSettings: (_, _) => Task.FromResult<object>(custom));

        var settings = await context.GetGameSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

        Assert.Same(custom, settings);
    }

    [Fact]
    public void CreateAppliesProvidedParts()
    {
        var state = new GameState(JsonSerializer.SerializeToElement(new { seed = 42 }));
        var buckets = new InMemoryHistoryBucketSet();
        var frontend = new CollectingFrontendEventSink();
        var experts = new FakeExpertFacade();
        var sessionId = new SessionId("session-9");

        var context = ActionContextTestFactory.Create(
            state: state,
            buckets: buckets,
            frontend: frontend,
            experts: experts,
            sessionId: sessionId);

        Assert.Same(state, context.State);
        Assert.Same(buckets, context.Buckets);
        Assert.Same(frontend, context.Frontend);
        Assert.Same(experts, context.Experts);
        Assert.Equal(sessionId, context.SessionId);
    }

    [Fact]
    public void FrontendRequestContextCreateAppliesDefaultsAndProvidedParts()
    {
        var state = new ReadOnlyGameState(JsonSerializer.SerializeToElement(new { committed = true }));
        var buckets = new InMemoryHistoryBucketSet();
        var store = new InMemoryGameSettingsStore();
        var headActionId = new ActionId("head-1");

        var defaulted = FrontendRequestContextTestFactory.Create();
        Assert.Equal(new SessionId("test-session"), defaulted.SessionId);
        Assert.Equal(new BranchId("test-branch"), defaulted.BranchId);
        Assert.Null(defaulted.HeadActionId);
        Assert.Equal(new PlayerId("test-player"), defaulted.PlayerProfile.PlayerId);
        Assert.Equal(JsonValueKind.Object, defaulted.State.Snapshot.ValueKind);
        Assert.Empty(defaulted.State.Snapshot.EnumerateObject());
        Assert.IsType<InMemoryHistoryBucketSet>(defaulted.Buckets);
        Assert.Null(defaulted.GameSettingsStore);

        var customized = FrontendRequestContextTestFactory.Create(
            state: state,
            buckets: buckets,
            gameSettingsStore: store,
            headActionId: headActionId,
            sessionId: new SessionId("session-7"));
        Assert.Same(state, customized.State);
        Assert.Same(buckets, customized.Buckets);
        Assert.Same(store, customized.GameSettingsStore);
        Assert.Equal(headActionId, customized.HeadActionId);
        Assert.Equal(new SessionId("session-7"), customized.SessionId);
    }

    [Fact]
    public async Task CollectingFrontendEventSinkRecordsEventsInWriteOrder()
    {
        var sink = new CollectingFrontendEventSink();

        await sink.WriteAsync(
            new FrontendEvent("delta", JsonSerializer.SerializeToElement(new { step = 1 })),
            TestSupport.CancellationToken);
        await sink.WriteAsync(
            new FrontendEvent("delta", JsonSerializer.SerializeToElement(new { step = 2 })),
            TestSupport.CancellationToken);

        Assert.Equal([1, 2], sink.Events.Select(item => item.Payload.GetProperty("step").GetInt32()));
        Assert.Equal(["delta", "delta"], sink.Events.Select(item => item.EventType));
    }

    [Fact]
    public async Task EmptyActionHistoryAlwaysReturnsEmptyList()
    {
        var history = await EmptyActionHistory.Instance.GetRecentPlayerActionsAsync(10, TestSupport.CancellationToken);

        Assert.Empty(history);
    }

    [Fact]
    public void InMemoryHistoryBucketSetCreatesIndexesAndRejectsDuplicates()
    {
        var buckets = new InMemoryHistoryBucketSet();
        buckets.Create("main", "主叙事历史");

        Assert.Equal("主叙事历史", buckets["main"].Description);
        Assert.Throws<InvalidOperationException>(() => buckets.Create("main", "重复创建"));
        Assert.Throws<KeyNotFoundException>(() => _ = buckets["missing"]);
        Assert.Throws<ArgumentException>(() => buckets.Create(" ", "空白名"));
        Assert.Throws<ArgumentException>(() => buckets.Create("ok", ""));
    }

    [Fact]
    public void InMemoryHistoryBucketAppendsTurnsWithMonotonicOrdinals()
    {
        var buckets = new InMemoryHistoryBucketSet();
        buckets.Create("main", "主叙事历史");

        buckets["main"].AddMessages(null, null, ChatMessage.User("一"));
        buckets["main"].AddMessages("digest", null, ChatMessage.User("二"), ChatMessage.Assistant("答"));

        var turns = buckets["main"].GetRawTurns();
        Assert.Equal([0L, 1L], turns.Select(turn => turn.TurnOrdinal));
        Assert.Equal(["一", "二", "答"], turns.SelectMany(turn => turn.Messages).Select(item => item.Content));
        Assert.Equal("digest", turns[1].Digest);
        var projections = buckets["main"].GetCompressedView()
            .Select(Assert.IsType<HistoryProjectionRawTurn>)
            .ToArray();
        Assert.Equal(turns, projections.Select(projection => projection.Turn));
        Assert.Equal([0L, 1L], projections.Select(projection => projection.StartOrdinal));
        Assert.Throws<ArgumentException>(() => buckets["main"].AddMessages(null, null));
    }

    [Fact]
    public async Task InMemoryGameSettingsStoreRoundTripsClonedJson()
    {
        var store = new InMemoryGameSettingsStore();

        Assert.Null(await store.GetAsync(TestSupport.CancellationToken));
        await store.SetAsync(
            JsonSerializer.SerializeToElement(new { mode = "hard" }),
            TestSupport.CancellationToken);
        var stored = await store.GetAsync(TestSupport.CancellationToken);
        Assert.Equal("""{"mode":"hard"}""", stored!.Value.GetRawText());

        Assert.True(await store.DeleteAsync(TestSupport.CancellationToken));
        Assert.Null(await store.GetAsync(TestSupport.CancellationToken));
        Assert.False(await store.DeleteAsync(TestSupport.CancellationToken));
    }
}
