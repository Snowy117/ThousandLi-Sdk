using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Non-generic expert authoring base. Holds the bound <see cref="IExpertRuntimeContext"/> and the
/// binding lifecycle; experts never receive game-backend state through this type.
/// </summary>
public abstract class ExpertBase
{
    private IExpertRuntimeContext? _runtimeContext;

    protected IExpertRuntimeContext RuntimeContext =>
        _runtimeContext ?? throw new InvalidOperationException(
            "The expert instance has not been bound to a runtime context. Expert instances must be created through a factory or executor that binds an IExpertRuntimeContext before execution.");

    internal void Bind(IExpertRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_runtimeContext is not null)
            throw new InvalidOperationException(
                "This expert instance is already bound to a runtime context; expert instances must not be rebound.");
        _runtimeContext = context;
    }
}

/// <summary>
/// CRTP generic authoring base for concrete experts. Holds the fluent request configuration
/// (<see cref="WithPrimaryOutput"/>, <see cref="WithFeatures"/>, <see cref="WithHistoryBuckets"/>)
/// and the abstract execution contract. The base performs no model invocation itself; execution is
/// owned by the concrete expert override of <see cref="StreamAsyncCore"/>/<see cref="CompleteAsyncCore"/>.
/// Instances and sinks are per-invocation: each instance executes at most once and configuration is
/// frozen once execution starts.
/// </summary>
/// <typeparam name="TFeature">The feature category interface of the expert.</typeparam>
/// <typeparam name="TSelf">The concrete expert type (CRTP).</typeparam>
public abstract class ExpertBase<TFeature, TSelf> : ExpertBase
    where TFeature : IExpertFeature
    where TSelf : ExpertBase<TFeature, TSelf>
{
    private readonly List<TFeature> _features = [];
    private readonly List<IHistoryBucket> _historyBuckets = [];
    private int _executed;

    public TSelf WithPrimaryOutput(IExpertPrimaryOutput primaryOutput)
    {
        ArgumentNullException.ThrowIfNull(primaryOutput);
        EnsureInvocationPending();
        PrimaryOutput = primaryOutput;
        return (TSelf)this;
    }

    public TSelf WithFeatures(params TFeature[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Any(feature => (object?)feature is null))
            throw new ArgumentException("Features cannot contain null entries.", nameof(features));
        EnsureInvocationPending();
        _features.AddRange(features);
        return (TSelf)this;
    }

    /// <summary>
    /// 传入历史消息桶引用。每个 bucket 存储时被包装为 <see cref="ReadOnlyHistoryBucket"/>——
    /// 专家执行期间是历史桶的只读消费者，写账本抛 <see cref="NotSupportedException"/>。
    /// </summary>
    public TSelf WithHistoryBuckets(params IHistoryBucket[] buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Any(bucket => (object?)bucket is null))
            throw new ArgumentException("History buckets cannot contain null entries.", nameof(buckets));
        EnsureInvocationPending();
        _historyBuckets.AddRange(buckets.Select(bucket => new ReadOnlyHistoryBucket(bucket)));
        return (TSelf)this;
    }

    protected IExpertPrimaryOutput? PrimaryOutput { get; private set; }

    protected IReadOnlyList<TFeature> ConfiguredFeatures => _features;

    protected IReadOnlyList<IHistoryBucket> HistoryBuckets => _historyBuckets;

    public Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnce(static (self, token) => self.StreamAsyncCore(token), cancellationToken);

    public Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnce(static (self, token) => self.CompleteAsyncCore(token), cancellationToken);

    protected abstract Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken);

    protected abstract Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken);

    private Task<ExpertCompletionResult> ExecuteOnce(
        Func<TSelf, CancellationToken, Task<ExpertCompletionResult>> core,
        CancellationToken cancellationToken)
    {
        _ = RuntimeContext;
        if (Interlocked.Exchange(ref _executed, 1) != 0)
            throw new InvalidOperationException(
                "This expert invocation instance has already been executed. Expert instances and sinks are per-invocation; create a fresh instance for every invocation.");
        return core((TSelf)this, cancellationToken);
    }

    private void EnsureInvocationPending()
    {
        if (Volatile.Read(ref _executed) != 0)
            throw new InvalidOperationException("Configuration cannot change after the expert invocation has executed.");
    }
}
