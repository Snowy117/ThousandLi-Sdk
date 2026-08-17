using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 长文本写作专家类别的 game-facing builder 契约。
/// Game 通过 <c>context.Experts.Use&lt;AbstractLongTextWritingExpert&gt;()</c> 获取平台注入的具体实例，
/// 用 fluent API 填充类别输入与通用配置（主输出 / Feature / 历史桶 / reasoning handler），
/// 再调用 <see cref="StreamAsync" /> / <see cref="CompleteAsync" /> 执行。
/// 具体执行流程（prompt/schema 构建、LLM 调用、事件到 callback 的映射）由平台侧的具体专家实现拥有。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class AbstractLongTextWritingExpert
{
    /// <summary>世界设定。</summary>
    protected string? WorldSettings { get; private set; }

    /// <summary>玩家输入。</summary>
    protected string? PlayerInput { get; private set; }

    /// <summary>玩家人设（可选）。</summary>
    protected string? PlayerPersona { get; private set; }

    /// <summary>当前状态摘要（可选）。</summary>
    protected string? CurrentState { get; private set; }

    /// <summary>当前状态的 AI-facing schema（可选）。</summary>
    protected string? StateSchema { get; private set; }

    /// <summary>当前已配置的主输出；未配置时返回 null。</summary>
    protected IExpertPrimaryOutput? ConfiguredPrimaryOutput { get; private set; }

    /// <summary>当前已启用的 Feature 列表。</summary>
    protected IReadOnlyList<ILongTextWritingFeature> ConfiguredFeatures { get; private set; } = [];

    /// <summary>当前已配置的历史消息桶（供具体专家读取）。</summary>
    protected IReadOnlyList<IHistoryBucket> ConfiguredHistoryBuckets { get; private set; } = [];

    /// <summary>已注册的 reasoning handler；为 null 时不转发推理增量。</summary>
    protected Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? ReasoningHandler { get; private set; }

    /// <summary>设置世界设定。</summary>
    public AbstractLongTextWritingExpert WithWorldSettings(string worldSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSettings);
        WorldSettings = worldSettings;
        return this;
    }

    /// <summary>设置玩家输入。</summary>
    public AbstractLongTextWritingExpert WithPlayerInput(string playerInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerInput);
        PlayerInput = playerInput;
        return this;
    }

    /// <summary>设置玩家人设。</summary>
    public AbstractLongTextWritingExpert WithPlayerPersona(string? playerPersona)
    {
        PlayerPersona = playerPersona;
        return this;
    }

    /// <summary>设置当前状态摘要。</summary>
    public AbstractLongTextWritingExpert WithCurrentState(string? currentState)
    {
        CurrentState = currentState;
        return this;
    }

    /// <summary>设置当前状态的 AI-facing schema。</summary>
    public AbstractLongTextWritingExpert WithStateSchema(string? stateSchema)
    {
        StateSchema = stateSchema;
        return this;
    }

    /// <summary>设置主输出通道。</summary>
    public AbstractLongTextWritingExpert WithPrimaryOutput(IExpertPrimaryOutput primaryOutput)
    {
        ConfiguredPrimaryOutput = primaryOutput ?? throw new ArgumentNullException(nameof(primaryOutput));
        return this;
    }

    /// <summary>启用本次请求的 Feature（只接受长文本写作类别的 Feature）。</summary>
    public AbstractLongTextWritingExpert WithFeatures(params ILongTextWritingFeature[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        ConfiguredFeatures = [.. ConfiguredFeatures, .. features];
        return this;
    }

    /// <summary>
    /// 传入历史消息桶引用。专家自己调 <see cref="IHistoryBucket.GetCompressedView" /> /
    /// <see cref="IHistoryBucket.GetRawTurns" /> / <see cref="IHistoryBucket.Description" /> 按需投影提取信息。
    /// 每个 bucket 在存储时被包装为 <see cref="ReadOnlyHistoryBucket" />——专家执行期间是历史桶的只读消费者，
    /// <see cref="IHistoryBucket.AddMessages" /> 抛 <see cref="NotSupportedException" />（防专家写账本）。
    /// </summary>
    public AbstractLongTextWritingExpert WithHistoryBuckets(params IHistoryBucket[] buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ConfiguredHistoryBuckets = [.. buckets.Select(bucket =>
            new ReadOnlyHistoryBucket(bucket
                ?? throw new ArgumentException("History bucket elements cannot be null.", nameof(buckets))))];
        return this;
    }

    /// <summary>注册推理增量 handler，使推理传递成为通用能力（与主输出/Feature callback 模式一致）。</summary>
    public AbstractLongTextWritingExpert WithReasoningHandler(Func<ReasoningDeltaEvent, CancellationToken, ValueTask> handler)
    {
        ReasoningHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// 流式执行一次专家请求。具体专家拥有完整执行流程（构建 request+sink、调用 LLM、映射事件到 callback）。
    /// </summary>
    public abstract Task<ExpertCompletionResult> StreamAsync([UsedImplicitly] CancellationToken cancellationToken = default);

    /// <summary>
    /// 非流式完成一次专家请求。具体专家拥有完整执行流程。
    /// </summary>
    public abstract Task<ExpertCompletionResult> CompleteAsync([UsedImplicitly] CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验世界设定/玩家输入非空，缺失时抛 <see cref="InvalidOperationException"/>。
    /// 由具体专家在构建 request+sink 时（<c>StreamAsync</c>/<c>CompleteAsync</c> override 内）调用。
    /// </summary>
    protected void ValidateCategoryInputs()
    {
        if (string.IsNullOrWhiteSpace(WorldSettings))
            throw new InvalidOperationException("Long text writing expert requires world settings.");
        if (string.IsNullOrWhiteSpace(PlayerInput))
            throw new InvalidOperationException("Long text writing expert requires player input.");
    }
}
