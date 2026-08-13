using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.TemplateName.Tests;

public sealed class GameBackendTests
{
    [Fact]
    public async Task InitialStateStartsAtTurnZero()
    {
        var backend = new GameBackend();
        var player = new BoundPlayerProfile(new PlayerId("test-player"), "Creator", "Test persona");

        var state = await backend.CreateInitialStateAsync(player, TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        Assert.Equal(0, state.GetProperty("turn").GetInt32());
    }
}
