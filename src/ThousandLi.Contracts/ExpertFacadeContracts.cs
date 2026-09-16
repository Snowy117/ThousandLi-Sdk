using System.Collections.ObjectModel;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>专家输出事件基类（纯数据载体，跨专家类别复用）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract record ExpertFeatureEvent;

/// <summary>供应商推理文本的专家增量事件（跨专家类别的通用能力，与主输出/Feature callback 模式一致）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ReasoningDeltaEvent(string Delta) : ExpertFeatureEvent;

/// <summary>一次专家执行调用的结构化结果。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertCompletionResult
{
    /// <summary>创建专家执行结果；不暴露完整模型 JSON，只暴露 Expert 整理出的 metadata 与可选推理文本。</summary>
    public ExpertCompletionResult(
        IReadOnlyDictionary<string, string>? metadata = null,
        string? reasoning = null)
    {
        Metadata = CopyMetadata(metadata);
        Reasoning = reasoning;
    }

    /// <summary>Expert 整理出的回合级 metadata；不包含完整 raw model JSON。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; }

    /// <summary>可选供应商推理文本（完整内容）。</summary>
    public string? Reasoning { get; }

    private static ReadOnlyDictionary<string, string>? CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Expert metadata keys cannot be null or blank.", nameof(metadata));

            copy[pair.Key] = pair.Value;
        }

        return copy.AsReadOnly();
    }
}

/// <summary>专家 Feature 声明接口——零方法纯标记接口（纯数据载体 + 语义 callback 字段）。</summary>
/// <remarks>
/// Feature 只承载 Game 的意图 + 参数 + 语义化 callback。如何把 Feature 转化为 schema/prompt 与 LLM 对接，
/// 是具体专家实现（平台侧）的专责。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertFeature;

/// <summary>长文本写作专家类别的 Feature 接口（编译期归属标记）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface ILongTextWritingFeature : IExpertFeature;

/// <summary>专家主输出通道声明。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertPrimaryOutput
{
    /// <summary>主输出 root property 名称。</summary>
    string PropertyName { get; }

    /// <summary>主输出 schema（Game 的自定义输出通道）。专家把它纳入自己构建的 schema。</summary>
    AiJsonSchema Schema { get; }
}

/// <summary>文本主输出声明。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record TextPrimaryOutput : IExpertPrimaryOutput
{
    /// <summary>创建文本主输出声明。</summary>
    public TextPrimaryOutput(
        Func<TextDeltaEvent, CancellationToken, ValueTask> onDelta,
        string propertyName = "narrative",
        Func<TextCompletedEvent, CancellationToken, ValueTask>? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        PrimaryOutputPropertyName.RejectJsonPointerUnsafe(propertyName);
        OnDelta = onDelta;
        PropertyName = propertyName;
        OnCompleted = onCompleted;
    }

    /// <summary>文本增量 callback。</summary>
    public Func<TextDeltaEvent, CancellationToken, ValueTask> OnDelta { get; }

    /// <inheritdoc />
    public string PropertyName { get; }

    /// <summary>文本完成 callback。</summary>
    public Func<TextCompletedEvent, CancellationToken, ValueTask>? OnCompleted { get; }

    /// <inheritdoc />
    public AiJsonSchema Schema => AiJsonSchema.String();
}

/// <summary>自定义 JSON 主输出声明。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonPrimaryOutput : IExpertPrimaryOutput
{
    /// <summary>创建 JSON 主输出声明。</summary>
    public JsonPrimaryOutput(
        string propertyName,
        AiJsonSchema schema,
        Func<PrimaryJsonStreamEvent, CancellationToken, ValueTask> onJsonEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        PrimaryOutputPropertyName.RejectJsonPointerUnsafe(propertyName);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(onJsonEvent);
        PropertyName = propertyName;
        Schema = schema;
        OnJsonEvent = onJsonEvent;
    }

    /// <inheritdoc />
    public string PropertyName { get; }

    /// <inheritdoc />
    public AiJsonSchema Schema { get; }

    /// <summary>JSON 流事件 callback。</summary>
    public Func<PrimaryJsonStreamEvent, CancellationToken, ValueTask> OnJsonEvent { get; }
}

/// <summary>文本增量事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record TextDeltaEvent(string Delta) : ExpertFeatureEvent;

/// <summary>文本完成事件。</summary>
// ReSharper disable once NotAccessedPositionalProperty.Global — 事件载荷字段，由 Game 端 onCompleted callback 按需消费
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record TextCompletedEvent(string Text) : ExpertFeatureEvent;

/// <summary>主输出 JSON 流事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PrimaryJsonStreamEvent(JsonStreamEvent Event) : ExpertFeatureEvent;

/// <summary>
/// 主输出属性名校验 helper。属性名必须 JSON-Pointer 安全（不含 <c>/</c> 或 <c>~</c>），
/// 因为 JSON 流解析会按 JSON Pointer 规则转义路径段，用原始属性名拼接的比较路径将永远无法匹配
/// 转义后的流事件路径，导致主输出 callback/事件被静默跳过。
/// </summary>
internal static class PrimaryOutputPropertyName
{
    public static void RejectJsonPointerUnsafe(string propertyName)
    {
        if (propertyName.Contains('/', StringComparison.Ordinal) ||
            propertyName.Contains('~', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Primary output property name '{propertyName}' must not contain '/' or '~' " +
                "because JSON Pointer escaping would break stream path matching.",
                nameof(propertyName));
        }
    }
}

/// <summary>时间标签增量事件。<see cref="IsStart" /> 区分开始/结束标签，由专家 sink 路由到正确 callback。</summary>
// ReSharper disable once NotAccessedPositionalProperty.Global — 事件载荷字段，由 Game 端 callback 按需消费
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record TimeTagDeltaEvent(string Delta, bool IsStart) : ExpertFeatureEvent;

/// <summary>
///     时间标签 Feature：纯数据载体，承载开始/结束标签的语义 callback。
///     专家 sink 负责把模型流式增量映射到 <see cref="OnStartTag"/>/<see cref="OnEndTag"/>。
/// </summary>
/// <remarks>
///     专家 sink 约定逐 chunk 转发（不等 completed）：前端逐 delta 展示时间标签，
///     故每个 <c>JsonStreamStringChunkEvent</c> 都立即触发 callback。callback 可为 null（仅声明意图、不转发）。
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class TimeTagsFeature(
    Func<TimeTagDeltaEvent, CancellationToken, ValueTask>? onStartTag,
    Func<TimeTagDeltaEvent, CancellationToken, ValueTask>? onEndTag) : ILongTextWritingFeature
{
    /// <summary>开始时间标签增量 callback；可为 null。</summary>
    public Func<TimeTagDeltaEvent, CancellationToken, ValueTask>? OnStartTag { get; } = onStartTag;

    /// <summary>结束时间标签增量 callback；可为 null。</summary>
    public Func<TimeTagDeltaEvent, CancellationToken, ValueTask>? OnEndTag { get; } = onEndTag;
}

/// <summary>行动选项完成事件。</summary>
// ReSharper disable once NotAccessedPositionalProperty.Global — 事件载荷字段，由 Game 端 onOptionCompleted callback 按需消费
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ActionOptionCompletedEvent(int Index, string Text) : ExpertFeatureEvent;

/// <summary>行动选项 Feature。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ActionOptionsFeature : ILongTextWritingFeature
{
    /// <summary>创建行动选项 Feature。</summary>
    /// <param name="maxCount">期望生成的最大选项数量。</param>
    /// <param name="onOptionCompleted">逐个选项完成 callback。</param>
    /// <param name="onArrayStarted">可选：模型开始生成 actionOptions 数组时触发（如发前端 started 事件）。</param>
    /// <param name="onArrayCompleted">可选：模型生成完 actionOptions 数组时触发（如发前端 completed 事件）。</param>
    public ActionOptionsFeature(
        int maxCount,
        Func<ActionOptionCompletedEvent, CancellationToken, ValueTask> onOptionCompleted,
        Func<CancellationToken, ValueTask>? onArrayStarted = null,
        Func<CancellationToken, ValueTask>? onArrayCompleted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        MaxCount = maxCount;
        OnOptionCompleted = onOptionCompleted ?? throw new ArgumentNullException(nameof(onOptionCompleted));
        OnArrayStarted = onArrayStarted;
        OnArrayCompleted = onArrayCompleted;
    }

    /// <summary>期望生成的最大选项数量。</summary>
    public int MaxCount { get; }

    /// <summary>行动选项完成 callback。</summary>
    public Func<ActionOptionCompletedEvent, CancellationToken, ValueTask> OnOptionCompleted { get; }

    /// <summary>可选：actionOptions 数组开始 callback。</summary>
    public Func<CancellationToken, ValueTask>? OnArrayStarted { get; }

    /// <summary>可选：actionOptions 数组完成 callback。</summary>
    public Func<CancellationToken, ValueTask>? OnArrayCompleted { get; }
}

/// <summary>
/// 专家 facade 契约：按抽象专家类别（<see cref="ExpertBase"/> 子类锚点）解析并返回请求作用域的
/// 专家实例。泛型约束类别无关——新增专家类别不需要改动本接口或任何实现。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertFacade
{
    /// <summary>
    /// 按抽象专家类别解析一个请求作用域的专家调用实例。返回实例已绑定执行上下文，
    /// 由调用方继续 fluent 配置后执行。
    /// </summary>
    /// <typeparam name="TAbstract">抽象专家类别锚点（如 <see cref="AbstractLongTextWritingExpert" />）。</typeparam>
    TAbstract Use<TAbstract>() where TAbstract : ExpertBase;
}
