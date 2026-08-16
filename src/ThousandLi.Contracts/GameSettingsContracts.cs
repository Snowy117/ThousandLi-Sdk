using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>标记 GameBackend 可供用户配置的 Game Settings 类型。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GameSettingsAttribute : Attribute;

/// <summary>标记 Game Settings 中可供用户配置的成员描述。</summary>
[AttributeUsage(AttributeTargets.Property)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class GameSettingsMemberAttribute(
    string title,
    string shortDescription,
    string longDescription) : Attribute
{
    /// <summary>成员标题。</summary>
    public string Title { get; } = title;

    /// <summary>成员短描述。</summary>
    public string ShortDescription { get; } = shortDescription;

    /// <summary>成员长描述。</summary>
    public string LongDescription { get; } = longDescription;
}

/// <summary>
/// Game Settings 的持久化存储端口（由运行时注入）。契约无身份参数——SDK 契约面不含用户/游戏包身份概念
/// （Player≠User）；平台在构造 store 实现时把身份绑定进实现内部（每 store 实例即一个身份作用域）。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IGameSettingsStore
{
    /// <summary>读取当前身份作用域下的设置快照；无存储时返回 null。</summary>
    Task<JsonElement?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>写入当前身份作用域下的设置快照。</summary>
    Task SetAsync(JsonElement settings, CancellationToken cancellationToken = default);

    /// <summary>删除当前身份作用域下的设置快照。</summary>
    Task<bool> DeleteAsync(CancellationToken cancellationToken = default);
}
