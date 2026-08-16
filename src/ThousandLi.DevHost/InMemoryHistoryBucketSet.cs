using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// DevHost 本地进程内的线性历史桶实现（单会话、无分支/reroll 语义——那由平台 SessionRuntime 负责）。
/// 每个 bucket 维护从 0 起的单调 TurnOrdinal 计数器，按 <see cref="IHistoryBucketSet" /> 契约
/// 显式创建（非幂等）、按名访问（未创建抛 <see cref="KeyNotFoundException" />）。
/// </summary>
public sealed class InMemoryHistoryBucketSet : IHistoryBucketSet
{
    private readonly Dictionary<string, InMemoryHistoryBucket> _buckets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IHistoryBucket this[string name] =>
        _buckets.TryGetValue(name, out var bucket)
            ? bucket
            : throw new KeyNotFoundException($"History bucket '{name}' does not exist.");

    /// <inheritdoc />
    public void Create(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!_buckets.TryAdd(name, new InMemoryHistoryBucket(description)))
            throw new InvalidOperationException($"History bucket '{name}' already exists.");
    }

    private sealed class InMemoryHistoryBucket(string description) : IHistoryBucket
    {
        private readonly List<HistoryTurn> _turns = [];
        private long _nextTurnOrdinal;

        /// <inheritdoc />
        public string Description { get; } = description;

        /// <inheritdoc />
        public void AddMessages(
            string? digest,
            IReadOnlyDictionary<string, string>? metadata,
            params ChatMessage[] messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            if (messages.Length == 0)
                throw new ArgumentException("History turn must contain at least one message.", nameof(messages));
            _turns.Add(new HistoryTurn(messages, digest, _nextTurnOrdinal++, metadata));
        }

        /// <inheritdoc />
        public IReadOnlyList<HistoryTurn> GetRawTurns() => _turns;

        /// <inheritdoc />
        public IReadOnlyList<HistoryProjectionEntry> GetCompressedView(CompressedViewOptions? options = null)
            => [.. _turns.Select<HistoryTurn, HistoryProjectionEntry>(turn => new HistoryProjectionRawTurn(turn))];
    }
}

/// <summary>
/// DevHost 尚未接入类型化专家执行（第二期）时的禁用 facade：任何 <c>Use&lt;T&gt;</c> 都抛
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
