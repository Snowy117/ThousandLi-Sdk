namespace ThousandLi.Contracts;

/// <summary>
/// 所有专家类别锚点的公共基类：契约标识、流畅配置状态、恰好一次 <see cref="Bind"/>、单次执行生命周期。
/// 具体类别通过 <see cref="ExpertBase{TFeature, TSelf}"/> 继承以获得强类型的 <c>WithFeatures</c> 与返回锚点类型的流畅 API。
/// 平台/运行时通过 <see cref="IExpertFacade.Use{TAbstract}"/> 的泛型实参精确命中绑定并创建实例。
/// </summary>
public abstract class ExpertBase : IExpertExecutionParticipant
{
    private IExpertExecutionContext? _runtimeContext;
    private int _executed;

    /// <summary>
    /// 为该专家实例注入运行上下文。由外观/工厂恰好调用一次；重复调用抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Bind(IExpertExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_runtimeContext is not null)
        {
            throw new InvalidOperationException("Expert instance is already bound.");
        }

        _runtimeContext = context;
    }

    /// <summary>
    /// 当前绑定的运行上下文。未绑定即访问将抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    protected IExpertExecutionContext RuntimeContext => _runtimeContext
        ?? throw new InvalidOperationException("Expert is not bound to a runtime context.");

    IRuntimeBasicAi IExpertExecutionParticipant.BasicAi => RuntimeContext.BasicAi;

    /// <summary>将作为回合元数据捕获的根属性名集合。默认空集；具体专家可重写声明。</summary>
    protected virtual IReadOnlySet<string> MetadataFieldNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    IReadOnlySet<string> IExpertExecutionParticipant.MetadataFieldNames => MetadataFieldNames;

    /// <summary>当前配置的推理增量处理器（可能为 null）。由 <c>WithReasoningHandler</c> 设置。</summary>
    protected Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? ReasoningHandler { get; set; }

    Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? IExpertExecutionParticipant.ReasoningHandler => ReasoningHandler;

    /// <summary>已配置的历史桶（只读视图，已按 Game 传入顺序包裹）。</summary>
    protected IReadOnlyList<IHistoryBucket> ConfiguredHistoryBuckets { get; private set; } = [];

    /// <summary>将历史桶引用交给专家；专家在构建提示词时自行读取。</summary>
    protected void SetHistoryBuckets(IReadOnlyList<IHistoryBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ConfiguredHistoryBuckets =
            [.. buckets.Select(static bucket => bucket as ReadOnlyHistoryBucket ?? new ReadOnlyHistoryBucket(bucket))];
    }

    /// <summary>
    /// 流式执行入口。校验绑定与单次执行后委托给 <see cref="StreamAsyncCore"/>。
    /// </summary>
    public Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotBoundOrExecuted();
        return StreamAsyncCore(cancellationToken);
    }

    /// <summary>
    /// 非流式执行入口。校验绑定与单次执行后委托给 <see cref="CompleteAsyncCore"/>。
    /// </summary>
    public Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotBoundOrExecuted();
        return CompleteAsyncCore(cancellationToken);
    }

    /// <summary>流式执行核心。具体专家在此编排 BasicAi 调用、schema 构建与事件回调映射。</summary>
    protected abstract Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken);

    /// <summary>非流式执行核心。具体专家在此编排 BasicAi 调用、schema 构建与事件回调映射。</summary>
    protected abstract Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken);

    private void ThrowIfNotBoundOrExecuted()
    {
        if (_runtimeContext is null)
        {
            throw new InvalidOperationException(
                "The expert instance has not been bound; call Bind(IExpertExecutionContext) before execution.");
        }

        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                "Expert instances are single-execution; this instance has already been executed.");
        }
    }
}

/// <summary>
/// 类别锚点的 CRTP 基类：通过 <typeparamref name="TSelf"/> 让流畅配置方法返回锚点类型本身，
/// 通过 <typeparamref name="TFeature"/> 把 <c>WithFeatures</c> 约束到该类别的 Feature 接口。
/// </summary>
/// <typeparam name="TFeature">该类别接受的 Feature 标记接口。</typeparam>
/// <typeparam name="TSelf">继承此基类的类别锚点类型本身。</typeparam>
public abstract class ExpertBase<TFeature, TSelf> : ExpertBase
    where TFeature : IExpertFeature
    where TSelf : ExpertBase<TFeature, TSelf>
{
    /// <summary>已配置的 Feature 列表（按传入顺序）。</summary>
    protected IReadOnlyList<TFeature> ConfiguredFeatures { get; private set; } = [];

    /// <summary>已配置的主输出（默认文本输出）。</summary>
    protected IExpertPrimaryOutput ConfiguredPrimaryOutput { get; private set; } = new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask);

    /// <summary>追加推理增量处理器（流式与聚合路径都会收到）。</summary>
    public TSelf WithReasoningHandler(Func<ReasoningDeltaEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ReasoningHandler = handler;
        return (TSelf)this;
    }

    /// <summary>配置本次调用的主输出（文本或自定义 JSON）。</summary>
    public TSelf WithPrimaryOutput(IExpertPrimaryOutput primaryOutput)
    {
        ArgumentNullException.ThrowIfNull(primaryOutput);
        ConfiguredPrimaryOutput = primaryOutput;
        return (TSelf)this;
    }

    /// <summary>附加类别 Feature（可选输出与回调载体）。多次调用按传入顺序累加。</summary>
    public TSelf WithFeatures(params TFeature[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Any(static feature => (object?)feature is null))
        {
            throw new ArgumentException("Features cannot contain null entries.", nameof(features));
        }

        ConfiguredFeatures = [.. ConfiguredFeatures, .. features];
        return (TSelf)this;
    }

    /// <summary>将历史桶引用交给专家；专家在构建提示词时自行读取。</summary>
    public TSelf WithHistoryBuckets(params IHistoryBucket[] buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Any(static bucket => (object?)bucket is null))
        {
            throw new ArgumentException("Buckets cannot contain null entries.", nameof(buckets));
        }

        SetHistoryBuckets(buckets);
        return (TSelf)this;
    }
}
