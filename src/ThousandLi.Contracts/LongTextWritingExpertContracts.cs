using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 长文本写作专家类别的唯一锚类型（类型即契约）。
/// Game 通过 <c>context.Experts.Use&lt;AbstractLongTextWritingExpert&gt;()</c> 获取平台注入的具体实例，
/// 用 fluent API 填充类别输入与通用配置（主输出 / Feature / 历史桶 / reasoning handler），
/// 再调用 <see cref="StreamAsync" /> / <see cref="CompleteAsync" /> 执行。
/// 具体执行流程（prompt/schema 构建、LLM 调用、事件到 callback 的映射）由具体专家在
/// <see cref="StreamAsyncCore" /> / <see cref="CompleteAsyncCore" /> override 中拥有。
/// 实例由工厂/facade 创建后未经绑定（unbound）；执行前由 facade/组合根调用
/// <see cref="Bind" /> 恰好绑定一次。实例与 sink 均为单次调用对象：每个实例至多执行一次。
/// 契约身份（<c>thousandli.expert/long-text-writing</c>）直接挂在本类型上；类别 fluent 类型
/// 本身是编译期 IDL，<see cref="Definition" /> 只是第一版校验文档（允许随 preview 演进）。
/// </summary>
[ExpertContract(LongTextWritingContract.Id, 1, 0, LongTextWritingContract.Fingerprint)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class AbstractLongTextWritingExpert : IExpertContract, IExpertExecutionParticipant
{
    private static readonly IReadOnlySet<string> SEmptyMetadataFields = new HashSet<string>(StringComparer.Ordinal);

    private IExpertExecutionContext? _executionContext;
    private int _executed;

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

    /// <summary>类别的契约描述符（id、版本、指纹）。</summary>
    public static ExpertContractDescriptor Descriptor => new(
        LongTextWritingContract.Id, new ContractVersion(1, 0), LongTextWritingContract.Fingerprint);

    /// <summary>类别契约的第一版校验文档；类型面（fluent 签名）才是权威契约。</summary>
    public static ExpertContractDefinition Definition => LongTextWritingContract.CreateDefinition();

    /// <summary>The bound execution context; throws when the instance has not been bound yet.</summary>
    protected IExpertExecutionContext RuntimeContext =>
        _executionContext ?? throw new InvalidOperationException(
            "The expert instance has not been bound to an execution context. Expert instances must be created through a factory or facade that binds an IExpertExecutionContext before execution.");

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
        if (features.Any(feature => (object?)feature is null))
            throw new ArgumentException("Features cannot contain null entries.", nameof(features));
        ConfiguredFeatures = [.. ConfiguredFeatures, .. features];
        return this;
    }

    /// <summary>
    /// 传入历史消息桶引用。专家自己调 <see cref="IHistoryBucket.GetCompressedViewAsync" /> /
    /// <see cref="IHistoryBucket.GetRawTurnsAsync" /> / <see cref="IHistoryBucket.Description" /> 按需投影提取信息。
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
    /// Binds the execution context to this instance, exactly once, before invocation. Callers are
    /// the facades and composition roots that hand experts to game code: the production Host facade,
    /// local composition roots, and test doubles (for example the <c>ThousandLi.Testing</c> fake
    /// facade pattern where the registered factory creates, binds, and returns the expert).
    /// </summary>
    public void Bind(IExpertExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_executionContext is not null)
            throw new InvalidOperationException(
                "This expert instance is already bound to an execution context; expert instances must not be rebound.");
        _executionContext = context;
    }

    /// <summary>
    /// Root property names captured as turn metadata; concrete experts override to declare their
    /// intrinsic metadata fields (for example <c>afterThinking</c>/<c>afterFormat</c>).
    /// </summary>
    protected virtual IReadOnlySet<string> MetadataFieldNames => SEmptyMetadataFields;

    /// <summary>
    /// 流式执行入口；每个实例至多执行一次。验证绑定与单次执行生命周期后委托给具体专家的
    /// <see cref="StreamAsyncCore" /> override。
    /// </summary>
    public Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnce(static (self, token) => self.StreamAsyncCore(token), cancellationToken);

    /// <summary>
    /// 非流式执行入口；每个实例至多执行一次。验证绑定与单次执行生命周期后委托给具体专家的
    /// <see cref="CompleteAsyncCore" /> override。
    /// </summary>
    public Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnce(static (self, token) => self.CompleteAsyncCore(token), cancellationToken);

    /// <summary>
    /// 流式执行契约，由具体专家拥有：构建 request+sink，然后调用
    /// <c>ExpertExecution</c>（或自行编排多次 BasicAi 调用）。
    /// </summary>
    protected abstract Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// 非流式执行契约，由具体专家拥有：构建 request+sink，然后调用
    /// <c>ExpertExecution</c>（或自行编排多次 BasicAi 调用）。
    /// </summary>
    protected abstract Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// 校验世界设定/玩家输入非空，缺失时抛 <see cref="InvalidOperationException"/>。
    /// 由具体专家在构建 request+sink 时（<c>StreamAsyncCore</c>/<c>CompleteAsyncCore</c> override 内）调用。
    /// </summary>
    protected void ValidateCategoryInputs()
    {
        if (string.IsNullOrWhiteSpace(WorldSettings))
            throw new InvalidOperationException("Long text writing expert requires world settings.");
        if (string.IsNullOrWhiteSpace(PlayerInput))
            throw new InvalidOperationException("Long text writing expert requires player input.");
    }

    private Task<ExpertCompletionResult> ExecuteOnce(
        Func<AbstractLongTextWritingExpert, CancellationToken, Task<ExpertCompletionResult>> core,
        CancellationToken cancellationToken)
    {
        _ = RuntimeContext;
        if (Interlocked.Exchange(ref _executed, 1) != 0)
            throw new InvalidOperationException(
                "This expert invocation instance has already been executed. Expert instances and sinks are per-invocation; create a fresh instance for every invocation.");
        return core(this, cancellationToken);
    }

    IRuntimeBasicAi IExpertExecutionParticipant.BasicAi => RuntimeContext.BasicAi;

    IReadOnlySet<string> IExpertExecutionParticipant.MetadataFieldNames => MetadataFieldNames;

    Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? IExpertExecutionParticipant.ReasoningHandler =>
        ReasoningHandler;
}

/// <summary>
/// The contract identity constants and first-version definition of the long-text-writing expert
/// category. The definition is a codec projection (D6 single source of truth): the input schema
/// and the semantic event whitelist are derived from <see cref="LongTextWritingWireCodec.Default"/>,
/// so codec changes are automatically covered by fingerprint drift detection. The fingerprint pins
/// the current <see cref="AbstractLongTextWritingExpert.Definition"/>; recompute it whenever the
/// definition evolves during the preview line.
/// </summary>
internal static class LongTextWritingContract
{
    public const string Id = "thousandli.expert/long-text-writing";
    public const string Fingerprint = "8046a8ea2ca4ffbd55776315b126d870e51e335438aed82a9cd44333f8f1df76";

    public static ExpertContractDefinition CreateDefinition()
    {
        var codec = LongTextWritingWireCodec.Default;
        return new ExpertContractDefinition(
            inputSchema: codec.BuildInputSchema(),
            semanticEventTypes: codec.EventTypes,
            outputSchema: ParseSchema("""
                {
                  "type": "object",
                  "description": "Completion frame aggregating every expert-to-game channel: the primary output value, feature results, turn metadata, and provider reasoning. The exact wire shape is finalized with the remote invocation payload."
                }
                """));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
