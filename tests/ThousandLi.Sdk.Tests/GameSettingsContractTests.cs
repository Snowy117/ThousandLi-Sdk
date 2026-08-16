using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

[GameSettings]
public sealed class TestGameSettings
{
    [GameSettingsMember("时间标签", "短描述", "长描述")]
    public bool EnableTimeTags { get; init; } = true;

    [GameSettingsMember("行动选项", "短描述", "长描述")]
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global — JSON 反序列化与合并需要公开 setter
    public bool EnableActionOptions { get; set; } = true;
}

public sealed class GameSettingsContractTests
{
    [Fact]
    public async Task GetGameSettingsReturnsInjectedResolution()
    {
        var state = new GameState(TestSupport.Json("{}"));
        var context = ContextSupport.BuildActionContext(
            state,
            getGameSettings: (_, _) => Task.FromResult<object>(new TestGameSettings { EnableTimeTags = false }));

        var settings = await context.GetGameSettingsAsync<TestGameSettings>(TestSupport.CancellationToken);

        Assert.False(settings.EnableTimeTags);
    }

    [Fact]
    public async Task GetGameSettingsFallsBackToDefaultInstance()
    {
        var state = new GameState(TestSupport.Json("{}"));
        var context = ContextSupport.BuildActionContext(state);

        var settings = await context.GetGameSettingsAsync<TestGameSettings>(TestSupport.CancellationToken);

        Assert.True(settings.EnableTimeTags);
        Assert.True(settings.EnableActionOptions);
    }

    [Fact]
    public async Task HandleSettingsRequestReturnsDefaultWhenUnstored()
    {
        var store = new InMemoryGameSettingsStore();
        var context = ContextSupport.BuildFrontendRequestContext(
            new ReadOnlyGameState(TestSupport.Json("{}")),
            new InMemoryHistoryBucketSet(),
            store);

        var result = await context.HandleSettingsRequestAsync<TestGameSettings>(
            new FrontendRequestEnvelope(new PlayerId("p"), TestSupport.Json("{\"type\":\"getGameSettings\"}")),
            context.UserId,
            context.GamePackageId,
            TestSupport.CancellationToken);

        Assert.True(result.Payload.GetProperty("enableTimeTags").GetBoolean());
    }

    [Fact]
    public async Task HandleSettingsRequestUpdatesAndPersistsMerge()
    {
        var store = new InMemoryGameSettingsStore();
        var context = ContextSupport.BuildFrontendRequestContext(
            new ReadOnlyGameState(TestSupport.Json("{}")),
            new InMemoryHistoryBucketSet(),
            store);

        var result = await context.HandleSettingsRequestAsync<TestGameSettings>(
            new FrontendRequestEnvelope(
                new PlayerId("p"),
                TestSupport.Json("{\"type\":\"updateGameSettings\",\"settings\":{\"enableTimeTags\":false}}")),
            context.UserId,
            context.GamePackageId,
            TestSupport.CancellationToken);

        Assert.True(result.Payload.GetProperty("ok").GetBoolean());
        var stored = await store.GetAsync(context.UserId, context.GamePackageId, TestSupport.CancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.Value.GetProperty("enableTimeTags").GetBoolean());
        // 未提供的字段保留默认值。
        Assert.True(stored.Value.GetProperty("enableActionOptions").GetBoolean());
    }

    [Fact]
    public async Task HandleSettingsRequestRequiresStore()
    {
        var context = ContextSupport.BuildFrontendRequestContext(
            new ReadOnlyGameState(TestSupport.Json("{}")),
            new InMemoryHistoryBucketSet(),
            store: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.HandleSettingsRequestAsync<TestGameSettings>(
                new FrontendRequestEnvelope(new PlayerId("p"), TestSupport.Json("{\"type\":\"getGameSettings\"}")),
                context.UserId,
                context.GamePackageId,
                TestSupport.CancellationToken));

        Assert.Contains("GameSettingsStore", exception.Message);
    }

    private sealed class InMemoryGameSettingsStore : IGameSettingsStore
    {
        private readonly Dictionary<string, JsonElement> _settings = new(StringComparer.Ordinal);

        public Task<JsonElement?> GetAsync(UserId userId, string gamePackageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<JsonElement?>(
                _settings.TryGetValue(Key(userId, gamePackageId), out var value) ? value.Clone() : null);
        }

        public Task SetAsync(UserId userId, string gamePackageId, JsonElement settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _settings[Key(userId, gamePackageId)] = settings.Clone();
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(UserId userId, string gamePackageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings.Remove(Key(userId, gamePackageId)));
        }

        private static string Key(UserId userId, string gamePackageId) => $"{userId.Value}\n{gamePackageId}";
    }
}
