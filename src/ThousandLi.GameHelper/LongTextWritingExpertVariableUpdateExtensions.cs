using ThousandLi.Contracts;

namespace ThousandLi.GameHelper;

/// <summary>长文本写作专家的 GameHelper 托管变量更新配置。</summary>
public static class LongTextWritingExpertVariableUpdateExtensions
{
    /// <summary>
    /// 注入 AI 可见当前状态与 schema，并在专家提出 patch 后回写托管 SessionState 根。
    /// 应用完成后使该上下文的 typed-root 缓存失效，后续 <c>GetSessionState</c> 会重新水合补丁后的状态。
    /// </summary>
    public static AbstractLongTextWritingExpert WithVariableUpdate<TVariables>(
        this AbstractLongTextWritingExpert expert,
        ActionContext context,
        Func<VariableUpdateOperation, bool>? validateOperation = null)
        where TVariables : class
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(context);

        _ = context.GetSessionState<TVariables>();
        var contract = SessionStateContract.Create(typeof(TVariables));
        var currentState = SessionStateExtensions.ReadAiFacingManagedRoot(context.State, contract);

        return expert
            .WithCurrentState(currentState.GetRawText())
            .WithStateSchema(contract.StateSchema)
            .WithFeatures(new VariableUpdateFeature((proposal, _) =>
            {
                SessionStateExtensions.ApplyVariableUpdateToManagedRoot(
                    context.State,
                    contract,
                    proposal.Operations,
                    context.Logger,
                    validateOperation);
                SessionStateExtensions.InvalidateCachedSessionState(context, typeof(TVariables));
                return ValueTask.CompletedTask;
            }));
    }
}
