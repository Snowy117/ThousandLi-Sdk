using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 平台侧语义事件发射器的最小 sink 契约：把一次专家执行中的语义事件（如 <c>chunk</c>、
/// <c>timetag</c>）发往 wire 通道。平台端 codec 解码出的 Feature/主输出 callback 均绑定到此 sink；
/// SDK 代理侧不使用它——游戏已持有真实 callback，由 codec 的 <c>DispatchAsync</c> 直调。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IWireEventSink
{
    /// <summary>发送一条语义事件（eventType 开放字符串集合，payload 为 codec 编码的 JSON）。</summary>
    /// <param name="eventType">语义事件类型（如 <c>chunk</c>）。</param>
    /// <param name="payload">codec 编码的事件 payload。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SendAsync(string eventType, JsonElement payload, CancellationToken cancellationToken = default);
}

/// <summary>wire 参数校验错误（stable code + 供 problem+json 使用的描述与可选 JSON Pointer）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record WireValidationError
{
    public WireValidationError(string code, string message, string? pointer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        Pointer = pointer;
    }

    /// <summary>稳定错误码（如 <c>features[1]/unknownKind</c>）。</summary>
    public string Code { get; }

    /// <summary>人读错误描述。</summary>
    public string Message { get; }

    /// <summary>可选 JSON Pointer（如 <c>/features/1</c>）。</summary>
    public string? Pointer { get; }
}

/// <summary>
/// 完成帧 primary 值的累加器：平台端 codec 绑定的 callback 在发 wire 事件的同时喂给本累加器，
/// <c>EncodeCompletion</c> 据此聚合完成帧（text 模式=全文；json 模式=由事件序列重建的最终值）。
/// 文本模式与 JSON 模式互斥；同一实例只接受一种喂食方式。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class WirePrimaryOutputAccumulator
{
    private readonly StringBuilder _text = new();
    private readonly List<JsonStreamEvent> _jsonEvents = [];
    private bool _textMode;
    private bool _jsonMode;

    /// <summary>累计一个文本增量（text 主输出模式）。</summary>
    public void AppendTextDelta(string delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delta);
        EnsureMode(textMode: true);
        _text.Append(delta);
    }

    /// <summary>累计一个 JSON 流事件（json 主输出模式；事件路径相对主输出属性）。</summary>
    public void AppendJsonEvent(JsonStreamEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        EnsureMode(textMode: false);
        _jsonEvents.Add(evt);
    }

    /// <summary>聚合文本主输出的全文（text 模式；未收到增量时为空字符串）。</summary>
    public string BuildText() => !_textMode
        ? throw new InvalidOperationException("Wire completion accumulator is not in text mode.")
        : _text.ToString();

    /// <summary>
    /// 尝试重建 JSON 主输出属性的最终值（json 模式）。未收到任何流事件时返回 false；
    /// 重建复用 <see cref="JsonStreamExtensions"/> 的流重组逻辑，值根可为任意 JSON 形状。
    /// </summary>
    public bool TryBuildJsonValue(out JsonElement value)
    {
        if (!_jsonMode)
            throw new InvalidOperationException("Wire completion accumulator is not in JSON mode.");
        if (_jsonEvents.Count == 0)
        {
            value = default;
            return false;
        }

        value = _jsonEvents.BuildJsonValue();
        return true;
    }

    private void EnsureMode(bool textMode)
    {
        if (textMode)
        {
            if (_jsonMode)
                throw new InvalidOperationException(
                    "Wire completion accumulator already received JSON stream events.");
            _textMode = true;
        }
        else
        {
            if (_textMode)
                throw new InvalidOperationException(
                    "Wire completion accumulator already received text deltas.");
            _jsonMode = true;
        }
    }
}

/// <summary>
/// SDK 代理侧 <c>EncodeInvocationAsync</c> 的产物：wire 参数包（POST input）与桶注册表
/// （bucketId → 本地活动桶引用，供代理响应平台的 dataRequest 反向请求）。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LongTextWritingWireInvocation
{
    public LongTextWritingWireInvocation(JsonElement input, IReadOnlyDictionary<string, IHistoryBucket> bucketRegistry)
    {
        JsonContractGuard.ThrowIfUndefined(input, nameof(input));
        ArgumentNullException.ThrowIfNull(bucketRegistry);
        Input = input.Clone();
        BucketRegistry = new Dictionary<string, IHistoryBucket>(bucketRegistry, StringComparer.Ordinal);
    }

    /// <summary>wire 参数包（形状由类别 codec 定义）。</summary>
    public JsonElement Input { get; }

    /// <summary>ref 桶的 bucketId → 活动 <see cref="IHistoryBucket" /> 映射；inline 桶自足，不入注册表。</summary>
    public IReadOnlyDictionary<string, IHistoryBucket> BucketRegistry { get; }
}

/// <summary>
/// 平台侧 <c>DecodeInvocation</c> 的产物：从 wire 参数包忠实还原的类别输入全集。
/// Feature 与主输出的 callback 绑定到平台注入的 <see cref="IWireEventSink"/>（语义事件发射器），
/// 完成帧聚合所需的 <see cref="Accumulator"/> 一并提供给 <c>EncodeCompletion</c>。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LongTextWritingWireInvocationConfig
{
    /// <summary>创建解码配置（各字段由类别 codec 填充）。</summary>
    public LongTextWritingWireInvocationConfig(
        string worldSettings,
        string playerInput,
        string? playerPersona,
        string? currentState,
        string? stateSchema,
        IExpertPrimaryOutput primaryOutput,
        IReadOnlyList<ILongTextWritingFeature> features,
        IReadOnlyList<IHistoryBucket> historyBuckets,
        WirePrimaryOutputAccumulator accumulator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerInput);
        ArgumentNullException.ThrowIfNull(primaryOutput);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(historyBuckets);
        ArgumentNullException.ThrowIfNull(accumulator);
        WorldSettings = worldSettings;
        PlayerInput = playerInput;
        PlayerPersona = playerPersona;
        CurrentState = currentState;
        StateSchema = stateSchema;
        PrimaryOutput = primaryOutput;
        Features = features;
        HistoryBuckets = historyBuckets;
        Accumulator = accumulator;
    }

    /// <summary>世界设定。</summary>
    public string WorldSettings { get; }

    /// <summary>玩家输入。</summary>
    public string PlayerInput { get; }

    /// <summary>玩家人设（可选）。</summary>
    public string? PlayerPersona { get; }

    /// <summary>当前状态摘要（可选）。</summary>
    public string? CurrentState { get; }

    /// <summary>当前状态的 AI-facing schema（可选）。</summary>
    public string? StateSchema { get; }

    /// <summary>主输出声明（callback 绑定到平台语义事件发射器）。</summary>
    public IExpertPrimaryOutput PrimaryOutput { get; }

    /// <summary>已启用的 Feature 列表（callback 绑定到平台语义事件发射器）。</summary>
    public IReadOnlyList<ILongTextWritingFeature> Features { get; }

    /// <summary>历史桶（ref → 平台注入的解析结果；inline → 重建的只读桶）。</summary>
    public IReadOnlyList<IHistoryBucket> HistoryBuckets { get; }

    /// <summary>完成帧 primary 值的累加器（由平台侧 callback 喂食）。</summary>
    public WirePrimaryOutputAccumulator Accumulator { get; }
}

/// <summary>
/// 长文本写作类别 Feature 的 wire codec 契约（非泛型视图，供注册表多态调用）。
/// 一条 codec 完整描述一种 Feature 的 wire 形状：判别 kind、参数编解码、
/// 语义事件到 callback 槽位的路由，以及用于 <c>BuildInputSchema</c> 派生的参数 schema 节点。
/// 平台侧（对象→wire）与 SDK 侧（wire→对象）共用同一 codec 的两个方向，物理上不可能漂移。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface ILongTextWritingFeatureWireCodec
{
    /// <summary>wire 判别字符串（如 <c>timeTags</c>）；在所属注册表内唯一。</summary>
    string Kind { get; }

    /// <summary>该 codec 处理的 Feature CLR 类型。</summary>
    Type FeatureType { get; }

    /// <summary>该 codec 产生的语义事件类型集合（如 <c>["timetag"]</c>）；在所属注册表内全局唯一。</summary>
    IReadOnlyList<string> EventTypes { get; }

    /// <summary>该 Feature wire 元素（含 kind 判别）的 JSON schema 节点；每次调用返回新实例。</summary>
    JsonObject ParameterSchema { get; }

    /// <summary>把 Feature 参数（callback 除外）写为 wire 元素的附加属性（kind 由注册表写入）。</summary>
    /// <param name="feature">SDK 侧的游戏 Feature 实例。</param>
    /// <param name="writer">目标 writer（已位于元素对象内部）。</param>
    void WriteParameters(ILongTextWritingFeature feature, Utf8JsonWriter writer);

    /// <summary>从 wire 元素还原 Feature 对象（callback 绑定到平台语义事件发射器）。</summary>
    /// <param name="wireFeature">wire Feature 元素（含 kind）。</param>
    /// <param name="sink">平台语义事件发射器。</param>
    ILongTextWritingFeature ReadParameters(JsonElement wireFeature, IWireEventSink sink);

    /// <summary>SDK 侧事件路由：把 wire 语义事件 payload 分发到游戏 Feature 的 callback 槽位。</summary>
    /// <param name="feature">持有游戏真实 callback 的 Feature 实例。</param>
    /// <param name="eventType">语义事件类型（属于 <see cref="EventTypes" />）。</param>
    /// <param name="payload">事件 payload。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask DispatchAsync(
        ILongTextWritingFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ILongTextWritingFeatureWireCodec" /> 的泛型基类：为具体 Feature 类型提供类型安全的
/// 编解码与分发样板。新增 Feature kind 时继承本类并通过
/// <c>LongTextWritingWireCodec.WithFeatureCodec</c> 注册——加法扩展，不触碰协议层。
/// </summary>
/// <typeparam name="TFeature">codec 处理的 Feature 类型。</typeparam>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class LongTextWritingFeatureWireCodec<TFeature> : ILongTextWritingFeatureWireCodec
    where TFeature : class, ILongTextWritingFeature
{
    /// <inheritdoc />
    public abstract string Kind { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> EventTypes { get; }

    /// <inheritdoc />
    public abstract JsonObject ParameterSchema { get; }

    /// <inheritdoc />
    public Type FeatureType => typeof(TFeature);

    /// <summary>把 Feature 参数写为 wire 元素的附加属性（kind 由注册表写入）。</summary>
    /// <param name="feature">游戏 Feature 实例。</param>
    /// <param name="writer">目标 writer。</param>
    protected abstract void Write(TFeature feature, Utf8JsonWriter writer);

    /// <summary>从 wire 元素还原 Feature 对象（callback 绑定到平台语义事件发射器）。</summary>
    /// <param name="wireFeature">wire Feature 元素（含 kind）。</param>
    /// <param name="sink">平台语义事件发射器。</param>
    protected abstract TFeature Read(JsonElement wireFeature, IWireEventSink sink);

    /// <summary>把 wire 语义事件 payload 分发到游戏 Feature 的 callback 槽位。</summary>
    /// <param name="feature">持有游戏真实 callback 的 Feature 实例。</param>
    /// <param name="eventType">语义事件类型。</param>
    /// <param name="payload">事件 payload。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public abstract ValueTask DispatchAsync(
        TFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken);

    void ILongTextWritingFeatureWireCodec.WriteParameters(ILongTextWritingFeature feature, Utf8JsonWriter writer) =>
        Write(Cast(feature), writer);

    ILongTextWritingFeature ILongTextWritingFeatureWireCodec.ReadParameters(
        JsonElement wireFeature, IWireEventSink sink) => Read(wireFeature, sink);

    ValueTask ILongTextWritingFeatureWireCodec.DispatchAsync(
        ILongTextWritingFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken) =>
        DispatchAsync(Cast(feature), eventType, payload, cancellationToken);

    private static TFeature Cast(ILongTextWritingFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature as TFeature ?? throw new InvalidOperationException(
            $"Wire codec for feature '{feature.GetType().FullName}' was handed an incompatible feature instance.");
    }
}

/// <summary>
/// 长文本写作类别主输出（text|json）的 wire codec 契约（非泛型视图）。
/// 与 Feature codec 同构：参数编解码 + 事件路由 + 参数 schema 派生；
/// 额外接收 <see cref="WirePrimaryOutputAccumulator" /> 以聚合完成帧 primary 值。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface ILongTextWritingPrimaryOutputWireCodec
{
    /// <summary>wire 判别字符串（<c>text</c> 或 <c>json</c>）；在所属注册表内唯一。</summary>
    string Kind { get; }

    /// <summary>该 codec 处理的主输出 CLR 类型。</summary>
    Type OutputType { get; }

    /// <summary>该 codec 产生的语义事件类型集合（text → <c>["chunk"]</c>；json → <c>["jsonStream"]</c>）。</summary>
    IReadOnlyList<string> EventTypes { get; }

    /// <summary>该主输出 wire 元素（含 kind 判别）的 JSON schema 节点；每次调用返回新实例。</summary>
    JsonObject ParameterSchema { get; }

    /// <summary>把主输出参数（callback 除外）写为 wire 元素的附加属性（kind 由注册表写入）。</summary>
    /// <param name="output">SDK 侧的游戏主输出声明。</param>
    /// <param name="writer">目标 writer（已位于元素对象内部）。</param>
    void WriteParameters(IExpertPrimaryOutput output, Utf8JsonWriter writer);

    /// <summary>从 wire 元素还原主输出声明（callback 绑定到平台语义事件发射器并喂给累加器）。</summary>
    /// <param name="wireOutput">wire 主输出元素（含 kind）。</param>
    /// <param name="sink">平台语义事件发射器。</param>
    /// <param name="accumulator">完成帧 primary 值的累加器。</param>
    IExpertPrimaryOutput ReadParameters(
        JsonElement wireOutput, IWireEventSink sink, WirePrimaryOutputAccumulator accumulator);

    /// <summary>SDK 侧事件路由：把 wire 语义事件 payload 分发到游戏主输出 callback。</summary>
    /// <param name="output">持有游戏真实 callback 的主输出声明。</param>
    /// <param name="eventType">语义事件类型（属于 <see cref="EventTypes" />）。</param>
    /// <param name="payload">事件 payload。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask DispatchAsync(
        IExpertPrimaryOutput output, string eventType, JsonElement payload, CancellationToken cancellationToken);

    /// <summary>
    /// 平台侧完成帧聚合：把累加器内容写为完成帧 <c>primary</c> 值（text=全文；json=事件序列重建值，
    /// 无事件时写 null）。由门面按注册表分派，扩展主输出 kind 时完成帧自动适配。
    /// </summary>
    /// <param name="writer">完成帧 writer（已位于 primary 值位置）。</param>
    /// <param name="accumulator">完成帧 primary 值的累加器。</param>
    void WriteCompletionPrimary(Utf8JsonWriter writer, WirePrimaryOutputAccumulator accumulator);
}

/// <summary>
/// <see cref="ILongTextWritingPrimaryOutputWireCodec" /> 的泛型基类：为具体主输出类型提供类型安全的
/// 编解码与分发样板。
/// </summary>
/// <typeparam name="TOutput">codec 处理的主输出类型。</typeparam>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class LongTextWritingPrimaryOutputWireCodec<TOutput> : ILongTextWritingPrimaryOutputWireCodec
    where TOutput : class, IExpertPrimaryOutput
{
    /// <inheritdoc />
    public abstract string Kind { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> EventTypes { get; }

    /// <inheritdoc />
    public abstract JsonObject ParameterSchema { get; }

    /// <inheritdoc />
    public Type OutputType => typeof(TOutput);

    /// <summary>把主输出参数写为 wire 元素的附加属性（kind 由注册表写入）。</summary>
    /// <param name="output">游戏主输出声明。</param>
    /// <param name="writer">目标 writer。</param>
    protected abstract void Write(TOutput output, Utf8JsonWriter writer);

    /// <summary>从 wire 元素还原主输出声明（callback 绑定 sink 并喂累加器）。</summary>
    /// <param name="wireOutput">wire 主输出元素（含 kind）。</param>
    /// <param name="sink">平台语义事件发射器。</param>
    /// <param name="accumulator">完成帧 primary 值的累加器。</param>
    protected abstract TOutput Read(JsonElement wireOutput, IWireEventSink sink, WirePrimaryOutputAccumulator accumulator);

    /// <summary>把 wire 语义事件 payload 分发到游戏主输出 callback。</summary>
    /// <param name="output">持有游戏真实 callback 的主输出声明。</param>
    /// <param name="eventType">语义事件类型。</param>
    /// <param name="payload">事件 payload。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public abstract ValueTask DispatchAsync(
        TOutput output, string eventType, JsonElement payload, CancellationToken cancellationToken);

    /// <summary>
    /// 把累加器内容写为完成帧 <c>primary</c> 值（平台侧完成帧聚合；由门面按注册表分派，
    /// 扩展主输出 kind 时完成帧自动适配）。
    /// </summary>
    /// <param name="writer">完成帧 writer（已位于 primary 值位置）。</param>
    /// <param name="accumulator">完成帧 primary 值的累加器。</param>
    protected abstract void WriteCompletionPrimary(
        Utf8JsonWriter writer, WirePrimaryOutputAccumulator accumulator);

    void ILongTextWritingPrimaryOutputWireCodec.WriteParameters(IExpertPrimaryOutput output, Utf8JsonWriter writer) =>
        Write(Cast(output), writer);

    IExpertPrimaryOutput ILongTextWritingPrimaryOutputWireCodec.ReadParameters(
        JsonElement wireOutput, IWireEventSink sink, WirePrimaryOutputAccumulator accumulator) =>
        Read(wireOutput, sink, accumulator);

    ValueTask ILongTextWritingPrimaryOutputWireCodec.DispatchAsync(
        IExpertPrimaryOutput output, string eventType, JsonElement payload, CancellationToken cancellationToken) =>
        DispatchAsync(Cast(output), eventType, payload, cancellationToken);

    void ILongTextWritingPrimaryOutputWireCodec.WriteCompletionPrimary(
        Utf8JsonWriter writer, WirePrimaryOutputAccumulator accumulator) =>
        WriteCompletionPrimary(writer, accumulator);

    private static TOutput Cast(IExpertPrimaryOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output as TOutput ?? throw new InvalidOperationException(
            $"Wire codec for primary output '{output.GetType().FullName}' was handed an incompatible primary output instance.");
    }
}
