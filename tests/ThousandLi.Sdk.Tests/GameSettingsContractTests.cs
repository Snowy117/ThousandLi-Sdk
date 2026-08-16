using ThousandLi.Contracts;

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
}
