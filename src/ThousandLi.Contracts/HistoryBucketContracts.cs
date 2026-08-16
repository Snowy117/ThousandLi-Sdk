using System.Collections.ObjectModel;
using System.Diagnostics;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>历史消息桶中存储的极简对话消息角色。</summary>
/// <remarks>
///     本枚举独立于 BasicAi 输入消息角色：bucket 的 <see cref="ChatMessage" /> 是历史存储载体，
///     与 BasicAi 裸调用输入分离，避免语义耦合（分裂接口，各自诚实）。未来 tool 角色加到此枚举。
/// </remarks>
public enum ChatMessageRole
{
    /// <summary>系统消息。</summary>
    System,

    /// <summary>用户消息。</summary>
    User,

    /// <summary>助手消息。</summary>
    Assistant
}

/// <summary>历史消息桶中存储的极简对话消息（角色 + 内容）。</summary>
/// <remarks>
///     极简形态（Role + Content）；未来可能加失败状态等，本次不预判。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ChatMessage
{
    /// <summary>创建历史消息并校验内容非空。</summary>
    /// <param name="role">消息角色。</param>
    /// <param name="content">消息文本内容。</param>
    public ChatMessage(ChatMessageRole role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Role = role;
        Content = content;
    }

    /// <summary>消息角色。</summary>
    public ChatMessageRole Role { get; }

    /// <summary>消息文本内容。</summary>
    public string Content { get; }

    /// <summary>创建系统消息。</summary>
    public static ChatMessage System(string content)
    {
        return new ChatMessage(ChatMessageRole.System, content);
    }

    /// <summary>创建用户消息。</summary>
    public static ChatMessage User(string content)
    {
        return new ChatMessage(ChatMessageRole.User, content);
    }

    /// <summary>创建助手消息。</summary>
    public static ChatMessage Assistant(string content)
    {
        return new ChatMessage(ChatMessageRole.Assistant, content);
    }
}

/// <summary>历史消息桶中的一个回合单元（不可变账本的原子单位）。</summary>
/// <remarks>
///     一个回合可含多条 <see cref="ChatMessage" />（如玩家一句 + AI 一句）。回合单元是压缩的原子单位——
///     平台压缩时不会把一个回合拆开。<see cref="TurnOrdinal" /> 是 per-bucket 从 0 起的单调递增序号，
///     作为排序锚点。不加时间戳（测试需确定性时间；序号已足排序）。
///     <see cref="Metadata" /> 承载回合级结构化元数据（如专家产出的 <c>afterThinking</c>/<c>afterFormat</c>），
///     与 <see cref="Digest" />（小摘要）语义分离。
/// </remarks>
public sealed record HistoryTurn
{
    /// <summary>创建历史回合单元并校验消息列表非空。</summary>
    /// <param name="messages">该回合的所有消息（有序，至少一条）。</param>
    /// <param name="digest">可选小摘要；本次接口预留，可为 null。</param>
    /// <param name="turnOrdinal">per-bucket 从 0 起的单调递增序号。</param>
    /// <param name="metadata">可选回合级结构化元数据（承载专家产出的 <c>afterThinking</c> 等），可为 null。</param>
    public HistoryTurn(
        IReadOnlyList<ChatMessage> messages,
        string? digest,
        long turnOrdinal,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            throw new ArgumentException("History turn must contain at least one message.", nameof(messages));

        var copied = new List<ChatMessage>(messages.Count);
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var message in messages)
        {
            copied.Add(message ?? throw new ArgumentException(
                "History turn messages cannot contain null.", nameof(messages)));
        }

        // 暴露 ReadOnlyCollection 而非底层 List，防止调用方 downcast 后修改不可变账本。
        Messages = copied.AsReadOnly();
        Digest = digest;
        TurnOrdinal = turnOrdinal;
        Metadata = CopyMetadata(metadata);
    }

    /// <summary>该回合的所有消息（有序，压缩的原子单位）。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>可选小摘要（Game 在 <c>AddMessages</c> 时传入；本次接口预留，可为 null）。</summary>
    public string? Digest { get; }

    /// <summary>per-bucket 从 0 起的单调递增序号，作为排序锚点。</summary>
    public long TurnOrdinal { get; }

    /// <summary>
    ///     可选回合级结构化元数据（承载专家产出的 <c>afterThinking</c>/<c>afterFormat</c> 等），
    ///     与 <see cref="Digest" />（小摘要）语义分离。可为 null。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; }

    private static ReadOnlyDictionary<string, string>? CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("History turn metadata keys cannot be null/empty.", nameof(metadata));
            // 字典值类型为非空 string（NRT 保证），直接赋值；空串是合法值，防御性保留。
            copy[pair.Key] = pair.Value;
        }

        // 返回不可变包装，防 downcast 到 Dictionary 后修改不可变账本（与 Messages 的 AsReadOnly() 一致）。
        return copy.AsReadOnly();
    }
}

/// <summary>压缩视图的预留策略参数（本次空占位）。</summary>
/// <remarks>
///     预留未来策略参数：最近 N 原文 / 小摘要窗口 / 大总结阈值。本次压缩视图只返回原文，
///     不读取此 record 的任何字段。<see cref="DebuggerDisplayAttribute" /> 用于满足空类型需有成员的检查。
/// </remarks>
[DebuggerDisplay("CompressedViewOptions")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CompressedViewOptions;

/// <summary>
///     压缩视图的一项投影条目（DU 层级，对齐项目既有流事件模式）。
/// </summary>
/// <remarks>
///     压缩视图按策略把旧单元压成大总结、较旧单元用小摘要、最近单元保留原文，返回混合投影序列。
///     本次压缩视图只产出 <see cref="HistoryProjectionRawTurn" />（全原文，不压缩）。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract record HistoryProjectionEntry
{
    /// <summary>供 DU 子类共享的受保护构造函数（使基类具备实例实现）。</summary>
    protected HistoryProjectionEntry()
    {
    }

    /// <summary>该项覆盖的起始回合序号（含）。</summary>
    public abstract long StartOrdinal { get; }

    /// <summary>该项覆盖的结束回合序号（含）。</summary>
    public abstract long EndOrdinal { get; }
}

/// <summary>压缩视图中的原文回合投影条目（本次压缩视图唯一产出的 case）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record HistoryProjectionRawTurn : HistoryProjectionEntry
{
    /// <summary>创建原文回合投影条目并校验 turn 非空。</summary>
    /// <param name="turn">被投影的原文回合（不可为 null）。</param>
    public HistoryProjectionRawTurn(HistoryTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        Turn = turn;
    }

    /// <summary>被投影的原文回合。</summary>
    public HistoryTurn Turn { get; }

    /// <inheritdoc />
    public override long StartOrdinal => Turn.TurnOrdinal;

    /// <inheritdoc />
    public override long EndOrdinal => Turn.TurnOrdinal;
}

/// <summary>
///     压缩视图中的小摘要投影条目（占位，本次不产生）。
/// </summary>
/// <remarks>
///     压缩视图按策略，对「较旧但未大总结」区间的 turn，<b>如果该 turn 有 digest</b>，
///     产出 <see cref="HistoryProjectionDigestSummary" /> entry 替代 RawTurn；如果没有 digest，降级为 RawTurn。
///     即本 case 不「创造」digest，只是「选择性暴露」turn 里已有的 digest。本次压缩视图不产生此 case。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record HistoryProjectionDigestSummary : HistoryProjectionEntry
{
    /// <summary>创建小摘要投影条目并校验摘要非空。</summary>
    /// <param name="turnOrdinal">该回合的序号。</param>
    /// <param name="digest">小摘要文本（不可为 null/空白）。</param>
    public HistoryProjectionDigestSummary(long turnOrdinal, string digest)
    {
        // DigestSummary 出现即意味着 turn 带有有效 digest；传 null/空白是调用方误用，尽早拦截。
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        TurnOrdinal = turnOrdinal;
        Digest = digest;
    }

    /// <summary>该回合的序号（通过基类 <see cref="StartOrdinal"/>/<see cref="EndOrdinal"/> 暴露）。</summary>
    private long TurnOrdinal { get; }

    /// <summary>小摘要文本。</summary>
    public string Digest { get; }

    /// <inheritdoc />
    public override long StartOrdinal => TurnOrdinal;

    /// <inheritdoc />
    public override long EndOrdinal => TurnOrdinal;
}

/// <summary>
///     压缩视图中的大总结投影条目（占位，本次不产生）。
/// </summary>
/// <remarks>
///     专用摘要专家包触发的大总结，覆盖一个序号区间。本次压缩视图不产生此 case。
///     位置参数名 <c>StartOrdinal</c>/<c>EndOrdinal</c> 会与基类 abstract 属性冲突，故采用非位置式 record。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record HistoryProjectionGrandSummary : HistoryProjectionEntry
{
    /// <summary>创建大总结投影条目并校验摘要非空。</summary>
    /// <param name="startOrdinal">覆盖的起始回合序号（含）。</param>
    /// <param name="endOrdinal">覆盖的结束回合序号（含）。</param>
    /// <param name="summary">大总结文本。</param>
    public HistoryProjectionGrandSummary(long startOrdinal, long endOrdinal, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        // 序号从 0 起，负值无意义；尽早拦截调用方误用。
        if (startOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startOrdinal),
                startOrdinal,
                "Ordinals start from 0; negative ordinals are not valid.");
        }
        // GrandSummary 覆盖一个序号区间，起始不应超过结束。尽早拦截调用方误用。
        if (startOrdinal > endOrdinal)
        {
            throw new ArgumentException(
                "Start ordinal must not exceed end ordinal.", nameof(startOrdinal));
        }
        StartOrdinal = startOrdinal;
        EndOrdinal = endOrdinal;
        Summary = summary;
    }

    /// <inheritdoc />
    public override long StartOrdinal { get; }

    /// <inheritdoc />
    public override long EndOrdinal { get; }

    /// <summary>大总结文本。</summary>
    public string Summary { get; }
}

/// <summary>单个历史消息桶：存全量原文不可变账本 + 提供压缩视图服务。</summary>
/// <remarks>
///     bucket 接口是纯线性的（<see cref="AddMessages" /> / <see cref="GetRawTurns" /> /
///     <see cref="GetCompressedView" />），不暴露任何分支语义给 Game。
///     分支/reroll 是平台内部透明职责，由完整持久化隐含支持。
/// </remarks>
// ReSharper disable UnusedMemberInSuper.Global
public interface IHistoryBucket
{
    /// <summary>该 bucket 的 LLM-friendly 自然语言描述（如「主叙事历史」「支线A」）。</summary>
    string Description { get; }

    /// <summary>
    ///     显式入历史：以一个回合单元追加到账本。<paramref name="digest" /> 可选（小摘要，接口预留），
    ///     <paramref name="metadata" /> 可选（回合级结构化元数据，承载专家产出的 <c>afterThinking</c> 等）。
    /// </summary>
    /// <param name="digest">可选小摘要；本次可不填。</param>
    /// <param name="metadata">可选回合级结构化元数据，与 digest 语义分离。</param>
    /// <param name="messages">该回合的所有消息（params 必须在末尾）。</param>
    void AddMessages(string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages);

    /// <summary>原始视图：返回全部回合单元的原始内容（不压缩），供 Game（如玩家查看久远历史）使用。</summary>
    IReadOnlyList<HistoryTurn> GetRawTurns();

    /// <summary>
    ///     压缩视图：按策略返回混合投影序列。本次只实现「原文层」——返回每个原始回合对应的
    ///     <see cref="HistoryProjectionRawTurn" />（全原文，不压缩）。
    /// </summary>
    /// <param name="options">预留策略参数；本次不读取。</param>
    IReadOnlyList<HistoryProjectionEntry> GetCompressedView([UsedImplicitly] CompressedViewOptions? options = null);
}
// ReSharper restore UnusedMemberInSuper.Global

/// <summary>一个 session 内的历史消息桶集合：管理多 bucket 的显式创建与按名访问。</summary>
/// <remarks>
///     所有 bucket 须显式 <see cref="Create" />（无默认 bucket、无 GetOrCreate）。
///     <b>多用户竞态提示</b>：Game 须自行保证 bucket 名称唯一（如生成 UUID）。
/// </remarks>
// ReSharper disable UnusedMemberInSuper.Global
public interface IHistoryBucketSet
{
    /// <summary>
    ///     按名访问已创建的 bucket。bucket 须 <see cref="Create" /> 后才能访问；
    ///     未 Create 直接索引器访问抛 <see cref="KeyNotFoundException" />（防误用）。
    /// </summary>
    /// <param name="name">bucket 名称。</param>
    IHistoryBucket this[string name] { get; }

    /// <summary>
    ///     显式创建 bucket 并附带 LLM-friendly 自然语言描述。<b>不幂等</b>：已存在则抛 <see cref="InvalidOperationException" />。
    /// </summary>
    /// <param name="name">bucket 名称。</param>
    /// <param name="description">LLM-friendly 自然语言描述（如「主叙事历史」），多 bucket 时供专家在 prompt 中区分各 bucket 语义。</param>
    void Create(string name, string description);
}
// ReSharper restore UnusedMemberInSuper.Global
