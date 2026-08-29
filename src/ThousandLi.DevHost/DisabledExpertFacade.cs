using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// DevHost remote 模式下的游戏面禁用 facade：游戏代码调用 <c>Use&lt;T&gt;</c> 立即抛
/// <see cref="InvalidOperationException" /> 并指路 Playground，绝不静默兜底（片1 的 remote
/// 游戏 facade 于 0.4.0-preview.2 交付）。
/// </summary>
public sealed class DisabledExpertFacade : IExpertFacade
{
    /// <summary>共享实例。</summary>
    public static DisabledExpertFacade Instance { get; } = new();

    /// <inheritdoc />
    public TAbstract Use<TAbstract>() where TAbstract : AbstractLongTextWritingExpert
    {
        throw new InvalidOperationException(
            $"The game-facing expert facade for '{typeof(TAbstract).FullName}' is not available in this DevHost " +
            "mode: the remote game-facing facade arrives in 0.4.0-preview.2. Use the Playground " +
            "(/playground) to debug remote experts in the meantime.");
    }
}
