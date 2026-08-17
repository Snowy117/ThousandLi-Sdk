using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// DevHost 未配置脚本化类型化专家时的禁用 facade：任何 <c>Use&lt;T&gt;</c> 都抛
/// <see cref="InvalidOperationException" />，避免静默返回 null 或空实现。
/// </summary>
public sealed class DisabledExpertFacade : IExpertFacade
{
    /// <summary>共享实例。</summary>
    public static DisabledExpertFacade Instance { get; } = new();

    /// <inheritdoc />
    public TAbstract Use<TAbstract>() where TAbstract : AbstractLongTextWritingExpert
    {
        throw new InvalidOperationException(
            $"Typed expert facade '{typeof(TAbstract).FullName}' is not configured in the local DevHost yet.");
    }
}
