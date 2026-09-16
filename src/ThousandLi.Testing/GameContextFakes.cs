using System.Text.Json;
using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// 丢弃所有前端事件的空实现，供测试上下文默认注入。需要断言前端输出时改用
/// <see cref="CollectingFrontendEventSink" />。
/// </summary>
public sealed class NoOpFrontendEventSink : IFrontendEventSink
{
    /// <summary>共享实例。</summary>
    public static NoOpFrontendEventSink Instance { get; } = new();

    private NoOpFrontendEventSink()
    {
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(FrontendEvent frontendEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frontendEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 记录全部前端事件的收集器：按写入顺序暴露 <see cref="Events" /> 快照，
/// 供 Game 测试断言 action 处理过程中发出的前端事件序列。
/// </summary>
public sealed class CollectingFrontendEventSink : IFrontendEventSink
{
    private readonly Lock _gate = new();
    private readonly List<FrontendEvent> _events = [];

    /// <summary>已写入的前端事件快照（按写入顺序；每次访问返回拷贝）。</summary>
    public IReadOnlyList<FrontendEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(FrontendEvent frontendEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frontendEvent);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _events.Add(frontendEvent);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>始终返回空列表的玩家行动历史，供测试上下文默认注入。</summary>
public sealed class EmptyActionHistory : IActionHistory
{
    /// <summary>共享实例。</summary>
    public static EmptyActionHistory Instance { get; } = new();

    private EmptyActionHistory()
    {
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PlayerActionEnvelope>> GetRecentPlayerActionsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<PlayerActionEnvelope>>([]);
    }
}

/// <summary>
/// 测试用内存历史桶集合：直接落账、无 action-run 缓冲语义（写入立即可见）。
/// 与 DevHost 的 <c>ThousandLi.DevHost.InMemoryHistoryBucketSet</c>（带 run 级缓冲/提交/回滚）是
/// 不同语义的实现；Game 单元测试通常只需要本类型。每个 bucket 维护从 0 起的单调 TurnOrdinal。
/// </summary>
public sealed class InMemoryHistoryBucketSet : IHistoryBucketSet
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IHistoryBucket this[string name]
    {
        get
        {
            lock (_gate)
            {
                return _buckets.TryGetValue(name, out var bucket)
                    ? bucket
                    : throw new KeyNotFoundException($"History bucket '{name}' does not exist.");
            }
        }
    }

    /// <inheritdoc />
    public void Create(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        lock (_gate)
        {
            if (_buckets.ContainsKey(name))
                throw new InvalidOperationException($"History bucket '{name}' already exists.");
            _buckets[name] = new Bucket(description);
        }
    }

    private sealed class Bucket(string description) : IHistoryBucket
    {
        private readonly List<HistoryTurn> _turns = [];
        private long _nextOrdinal;

        public string Description { get; } = description;

        public void AddMessages(
            string? digest,
            IReadOnlyDictionary<string, string>? metadata,
            params ChatMessage[] messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            if (messages.Length == 0)
                throw new ArgumentException("History turn must contain at least one message.", nameof(messages));
            _turns.Add(new HistoryTurn(messages, digest, _nextOrdinal++, metadata));
        }

        public ValueTask<IReadOnlyList<HistoryTurn>> GetRawTurnsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryTurn>>([.. _turns]);
        }

        public ValueTask<IReadOnlyList<HistoryProjectionEntry>> GetCompressedViewAsync(
            CompressedViewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryProjectionEntry>>(
                [.. _turns.Select<HistoryTurn, HistoryProjectionEntry>(turn => new HistoryProjectionRawTurn(turn))]);
        }
    }
}

/// <summary>
/// 测试用无身份 game settings 内存存储：get/set/delete 直接操作克隆后的 JSON，
/// 不落盘。与 DevHost 的 <c>ThousandLi.DevHost.InMemoryGameSettingsStore</c> 同形；
/// 身份闭包绑定语义见 SDK 契约（Player≠User，身份由组合根绑定）。
/// </summary>
[PublicAPI]
public sealed class InMemoryGameSettingsStore : IGameSettingsStore
{
    private JsonElement? _settings;

    /// <inheritdoc />
    public Task<JsonElement?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settings?.Clone());
    }

    /// <inheritdoc />
    public Task SetAsync(JsonElement settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = settings.Clone();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = _settings is not null;
        _settings = null;
        return Task.FromResult(removed);
    }
}
