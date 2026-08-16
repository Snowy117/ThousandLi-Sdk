using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// DevHost 本地进程内的无身份 game settings 存储。DevHost 组合是单游戏单玩家，
/// 身份在构造时绑定进组合（一个 store 实例即一个作用域），因此实现 <see cref="IGameSettingsStore" />
/// 契约的无身份端口即可。设置只在进程内存中存活，不落盘。
/// </summary>
public sealed class InMemoryGameSettingsStore : IGameSettingsStore
{
    private JsonElement? _settings;

    /// <inheritdoc />
    public Task<JsonElement?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settings?.Clone());
    }

    /// <inheritdoc />
    public Task SetAsync(JsonElement settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = settings.Clone();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = _settings is not null;
        _settings = null;
        return Task.FromResult(removed);
    }
}
