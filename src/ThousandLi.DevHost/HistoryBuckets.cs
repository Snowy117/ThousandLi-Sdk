using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// DevHost action-run 生命周期桶接口：供 <see cref="LocalGameRuntime" /> 持有的
/// <see cref="IHistoryBucketSet" /> 注入每次 action run 的缓冲/提交/回滚行为。
/// 公共 <see cref="IHistoryBucketSet" /> 契约（<c>Create</c>/索引器）不透出这些生命周期成员。
/// </summary>
internal interface IHistoryBucketSetHost
{
    /// <summary>开始收集一次 action run 的缓冲写入（先清空上一次缓冲）。</summary>
    void BindActionRun();

    /// <summary>把缓冲写入合并进提交基线，成为后续运行/前端请求可见的状态。</summary>
    void CommitActionRun();

    /// <summary>丢弃缓冲写入，提交基线不变（run 失败/取消时调用）。</summary>
    void AbortActionRun();

    /// <summary>返回只包含当前提交基线的快照集合（前端请求读取已提交 bucket 状态用）。</summary>
    IHistoryBucketSet CreateCommittedSnapshot();
}

/// <summary>
/// 没有生命周期接缝时的防御性 fallback：所有生命周期钩子为 no-op，
/// 快照直接返回集合自身（保持旧行为）。<see cref="LocalGameRuntime" /> 用它包装
/// 调用方注入的自定义 <see cref="IHistoryBucketSet" />。
/// </summary>
internal sealed class NoopHistoryBucketSetHost(IHistoryBucketSet inner) : IHistoryBucketSetHost
{
    public void BindActionRun()
    {
    }

    public void CommitActionRun()
    {
    }

    public void AbortActionRun()
    {
    }

    public IHistoryBucketSet CreateCommittedSnapshot() => inner;
}

/// <summary>
/// DevHost 本地进程内的线性历史桶实现（单会话、无分支/reroll 语义——那由平台 SessionRuntime 负责）。
/// 每个 bucket 维护从 0 起的单调 TurnOrdinal 计数器，按 <see cref="IHistoryBucketSet" /> 契约
/// 显式创建（非幂等）、按名访问（未创建抛 <see cref="KeyNotFoundException" />）。
/// 通过 <see cref="IHistoryBucketSetHost" /> 支持 action-run 级写入缓冲：
/// run 内 <see cref="IHistoryBucket.AddMessages" />/建桶先入缓冲（读己写可见），
/// <see cref="IHistoryBucketSetHost.CommitActionRun" /> 后对后续 run/前端请求可见，
/// <see cref="IHistoryBucketSetHost.AbortActionRun" /> 后缓冲丢弃、提交基线不变。
/// </summary>
public sealed class InMemoryHistoryBucketSet : IHistoryBucketSet, IHistoryBucketSetHost
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BucketState> _committed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BucketState> _buffer = new(StringComparer.Ordinal);
    private bool _bound;

    /// <inheritdoc />
    public IHistoryBucket this[string name] => Lookup(name) is { } state
        ? state.View
        : throw new KeyNotFoundException($"History bucket '{name}' does not exist.");

    /// <inheritdoc />
    public void Create(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        lock (_gate)
        {
            if (Lookup(name) is not null)
                throw new InvalidOperationException($"History bucket '{name}' already exists.");
            var state = new BucketState(description);
            state.View = new BucketView(this, state);
            (_bound ? _buffer : _committed)[name] = state;
        }
    }

    void IHistoryBucketSetHost.BindActionRun()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _bound = true;
        }
    }

    void IHistoryBucketSetHost.CommitActionRun()
    {
        lock (_gate)
        {
            foreach (var (name, state) in _buffer)
            {
                state.CommittedTurns.AddRange(state.BufferedTurns);
                state.BufferedTurns.Clear();
                state.CommittedNextTurnOrdinal = state.NextTurnOrdinal;
                _committed[name] = state;
            }
            // 已存在的 bucket 可能在本 run 内直接缓冲（AddMessages 落在 committed 实例上）。
            foreach (var state in _committed.Values)
            {
                state.CommittedTurns.AddRange(state.BufferedTurns);
                state.BufferedTurns.Clear();
                state.CommittedNextTurnOrdinal = state.NextTurnOrdinal;
            }
            _buffer.Clear();
            _bound = false;
        }
    }

    void IHistoryBucketSetHost.AbortActionRun()
    {
        lock (_gate)
        {
            foreach (var state in _committed.Values)
            {
                state.BufferedTurns.Clear();
                state.NextTurnOrdinal = state.CommittedNextTurnOrdinal;
            }
            _buffer.Clear();
            _bound = false;
        }
    }

    IHistoryBucketSet IHistoryBucketSetHost.CreateCommittedSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new InMemoryHistoryBucketSet();
            foreach (var (name, state) in _committed)
            {
                var copy = new BucketState(state.Description)
                {
                    NextTurnOrdinal = state.NextTurnOrdinal,
                    CommittedNextTurnOrdinal = state.CommittedNextTurnOrdinal
                };
                copy.CommittedTurns.AddRange(state.CommittedTurns);
                copy.View = new BucketView(snapshot, copy);
                snapshot._committed[name] = copy;
            }
            return snapshot;
        }
    }

    private BucketState? Lookup(string name)
    {
        lock (_gate)
        {
            return _committed.GetValueOrDefault(name) ?? _buffer.GetValueOrDefault(name);
        }
    }

    private void AddMessages(BucketState state, string? digest, IReadOnlyDictionary<string, string>? metadata,
        ChatMessage[] messages)
    {
        lock (_gate)
        {
            state.BufferedTurns.Add(
                new HistoryTurn(messages, digest, state.NextTurnOrdinal++, metadata));
        }
    }

    private IReadOnlyList<HistoryTurn> GetRawTurns(BucketState state)
    {
        lock (_gate)
        {
            return [.. state.CommittedTurns, .. state.BufferedTurns];
        }
    }

    private sealed class BucketState(string description)
    {
        public string Description { get; } = description;
        public List<HistoryTurn> CommittedTurns { get; } = [];
        public List<HistoryTurn> BufferedTurns { get; } = [];
        public long NextTurnOrdinal { get; set; }
        public long CommittedNextTurnOrdinal { get; set; }
        public IHistoryBucket View { get; set; } = null!;
    }

    private sealed class BucketView(
        InMemoryHistoryBucketSet owner,
        BucketState state) : IHistoryBucket
    {
        /// <inheritdoc />
        public string Description => state.Description;

        /// <inheritdoc />
        public void AddMessages(
            string? digest,
            IReadOnlyDictionary<string, string>? metadata,
            params ChatMessage[] messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            if (messages.Length == 0)
                throw new ArgumentException("History turn must contain at least one message.", nameof(messages));
            owner.AddMessages(state, digest, metadata, messages);
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<HistoryTurn>> GetRawTurnsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.GetRawTurns(state));
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<HistoryProjectionEntry>> GetCompressedViewAsync(
            CompressedViewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryProjectionEntry>>(
                [.. owner.GetRawTurns(state)
                    .Select<HistoryTurn, HistoryProjectionEntry>(turn => new HistoryProjectionRawTurn(turn))]);
        }
    }
}
