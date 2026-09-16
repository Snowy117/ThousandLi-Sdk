namespace ThousandLi.Contracts;

/// <summary>
/// 长文本写作专家类别锚点：携带类别自己的输入集（世界设定、玩家输入、可选人设/状态/schema），
/// 类别身份由 <see cref="ContractId"/> 与 <c>[ExpertContract]</c> 稳定 id 表达。
/// 具体专家仅需重写 <see cref="ExpertBase.StreamAsyncCore"/> 与 <see cref="ExpertBase.CompleteAsyncCore"/>。
/// </summary>
[ExpertContract(ContractId)]
public abstract class AbstractLongTextWritingExpert : ExpertBase<ILongTextWritingFeature, AbstractLongTextWritingExpert>
{
    /// <summary>类别契约标识常量（供路由、持久化与绑定引用）。</summary>
    public const string ContractId = "thousandli.expert/long-text-writing";

    /// <summary>世界设定文本。</summary>
    protected string? WorldSettings { get; private set; }

    /// <summary>玩家输入文本。</summary>
    protected string? PlayerInput { get; private set; }

    /// <summary>玩家角色设定（可选）。</summary>
    protected string? PlayerPersona { get; private set; }

    /// <summary>当前状态摘要 JSON 文本（可选）。</summary>
    protected string? CurrentState { get; private set; }

    /// <summary>状态 schema JSON 文本（可选，仅用于 VariableUpdate 第二程）。</summary>
    protected string? StateSchema { get; private set; }

    /// <summary>配置世界设定。</summary>
    public AbstractLongTextWritingExpert WithWorldSettings(string worldSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSettings);
        WorldSettings = worldSettings;
        return this;
    }

    /// <summary>配置玩家输入。</summary>
    public AbstractLongTextWritingExpert WithPlayerInput(string playerInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerInput);
        PlayerInput = playerInput;
        return this;
    }

    /// <summary>配置玩家角色设定（传 null 清除）。</summary>
    public AbstractLongTextWritingExpert WithPlayerPersona(string? playerPersona)
    {
        PlayerPersona = playerPersona;
        return this;
    }

    /// <summary>配置当前状态摘要 JSON 文本（传 null 清除）。</summary>
    public AbstractLongTextWritingExpert WithCurrentState(string? currentState)
    {
        CurrentState = currentState;
        return this;
    }

    /// <summary>配置状态 schema JSON 文本（供可选的 VariableUpdate 第二程使用；传 null 清除）。</summary>
    public AbstractLongTextWritingExpert WithStateSchema(string? stateSchema)
    {
        StateSchema = stateSchema;
        return this;
    }

    /// <summary>
    /// 类别输入校验：具体专家在执行前调用以确认必填输入已配置。
    /// </summary>
    protected void ValidateCategoryInputs()
    {
        if (WorldSettings is null)
        {
            throw new InvalidOperationException(
                "The category requires world settings before execution; call WithWorldSettings(...).");
        }

        if (PlayerInput is null)
        {
            throw new InvalidOperationException(
                "The category requires player input before execution; call WithPlayerInput(...).");
        }
    }
}
