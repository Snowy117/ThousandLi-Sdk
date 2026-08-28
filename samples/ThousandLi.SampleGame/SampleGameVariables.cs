using ThousandLi.GameHelper;

namespace ThousandLi.SampleGame;

/// <summary>
/// SampleGame 的 GameHelper 托管 SessionState 根：演示持久化成员与 AI 可见成员的划分，
/// 以及专家链 <c>WithVariableUpdate</c> 第二遍补丁的 delta/replace 用法。
/// 根类型必须是非密封类且成员为公共 virtual 可读写属性，
/// 由 GameHelper 生成写回 <c>/_gameHelper/sessionVariables</c> 的跟踪代理。
/// </summary>
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global — SessionState 契约要求 virtual 可写属性，由 Castle 跟踪代理写回
[SessionStateRoot]
public class SampleGameVariables
{
    /// <summary>仅持久化、AI 不可见：只由 GameBackend 读写，变量更新补丁无法指向它。</summary>
    [SessionStateMember]
    public virtual int ReflectCount { get; set; }

    /// <summary>AI 可见、可通过 <c>delta</c> 更新的数值（带边界约束）。</summary>
    [AiStateMember(0, "主角的勇气值（0-100），影响叙事走向。", UpdateRule = "单次变化不超过 10。", Min = "0", Max = "100")]
    public virtual int Courage { get; set; } = 10;

    /// <summary>AI 可见、可通过 <c>replace</c> 更新的数值。</summary>
    [AiStateMember(1, "主角对引路人的信任程度（0-100）。")]
    public virtual int Trust { get; set; } = 30;
}
// ReSharper restore AutoPropertyCanBeMadeGetOnly.Global
