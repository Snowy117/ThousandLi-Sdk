using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.GameHelper.Tests;

[GameSettings]
public sealed class TestGameSettings
{
    // ReSharper disable once UnusedMember.Global — 由 JSON 序列化反射读取
    [GameSettingsMember("时间标签", "短描述", "长描述")]
    public bool EnableTimeTags { get; init; } = true;

    [GameSettingsMember("行动选项", "短描述", "长描述")]
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global — JSON 反序列化与合并需要公开 setter
    // ReSharper disable once UnusedMember.Global — 由 JSON 序列化反射读取
    public bool EnableActionOptions { get; set; } = true;
}

public sealed class GameSettingsHelperExtensionsTests
{
    [Fact]
    public async Task HandleSettingsRequestReturnsDefaultWhenUnstored()
    {
        var store = new InMemoryGameSettingsStore();
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")), store);

        var result = await context.HandleSettingsRequestAsync<TestGameSettings>(
            new FrontendRequestEnvelope(new PlayerId("p"), Support.Json("{\"type\":\"getGameSettings\"}")),
            Support.CancellationToken);

        Assert.True(result.Payload.GetProperty("enableTimeTags").GetBoolean());
    }

    [Fact]
    public async Task HandleSettingsRequestReturnsStoredSnapshotWhenPresent()
    {
        var store = new InMemoryGameSettingsStore();
        await store.SetAsync(
            Support.Json("{\"enableTimeTags\":false,\"enableActionOptions\":true}"),
            Support.CancellationToken);
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")), store);

        var result = await context.HandleSettingsRequestAsync<TestGameSettings>(
            new FrontendRequestEnvelope(new PlayerId("p"), Support.Json("{\"type\":\"getGameSettings\"}")),
            Support.CancellationToken);

        Assert.False(result.Payload.GetProperty("enableTimeTags").GetBoolean());
    }

    [Fact]
    public async Task HandleSettingsRequestUpdatesAndPersistsMerge()
    {
        var store = new InMemoryGameSettingsStore();
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")), store);

        var result = await context.HandleSettingsRequestAsync<TestGameSettings>(
            new FrontendRequestEnvelope(
                new PlayerId("p"),
                Support.Json("{\"type\":\"updateGameSettings\",\"settings\":{\"enableTimeTags\":false}}")),
            Support.CancellationToken);

        Assert.True(result.Payload.GetProperty("ok").GetBoolean());
        var stored = await store.GetAsync(Support.CancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.Value.GetProperty("enableTimeTags").GetBoolean());
        // 未提供的字段保留默认值。
        Assert.True(stored.Value.GetProperty("enableActionOptions").GetBoolean());
    }

    [Fact]
    public async Task HandleSettingsRequestRequiresStore()
    {
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.HandleSettingsRequestAsync<TestGameSettings>(
                new FrontendRequestEnvelope(new PlayerId("p"), Support.Json("{\"type\":\"getGameSettings\"}")),
                Support.CancellationToken));

        Assert.Contains("GameSettingsStore", exception.Message);
    }

    [Fact]
    public async Task HandleSettingsRequestRejectsUnknownType()
    {
        var store = new InMemoryGameSettingsStore();
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")), store);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await context.HandleSettingsRequestAsync<TestGameSettings>(
                new FrontendRequestEnvelope(new PlayerId("p"), Support.Json("{\"type\":\"resetGameSettings\"}")),
                Support.CancellationToken));
    }
}
