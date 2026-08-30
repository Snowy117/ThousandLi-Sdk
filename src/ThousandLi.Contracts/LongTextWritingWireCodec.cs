using System.Text.Json;
using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// <see cref="TimeTagsFeature" /> 的 wire codec（kind <c>timeTags</c>）：
/// 参数无；语义事件 <c>timetag</c> payload <c>{delta, isStart}</c>，
/// 按 <c>isStart</c> 路由到 <see cref="TimeTagsFeature.OnStartTag" /> / <see cref="TimeTagsFeature.OnEndTag" />。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class TimeTagsFeatureWireCodec : LongTextWritingFeatureWireCodec<TimeTagsFeature>
{
    private const string EventType = "timetag";

    /// <inheritdoc />
    public override string Kind => "timeTags";

    /// <inheritdoc />
    public override IReadOnlyList<string> EventTypes => [EventType];

    /// <inheritdoc />
    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
        },
        ["required"] = new JsonArray("kind"),
    };

    /// <inheritdoc />
    protected override void Write(TimeTagsFeature feature, Utf8JsonWriter writer)
    {
        // timeTags 无请求级参数（callback 不上 wire）。
        _ = feature;
        _ = writer;
    }

    /// <inheritdoc />
    protected override TimeTagsFeature Read(JsonElement wireFeature, IWireEventSink sink)
    {
        _ = wireFeature;
        return new TimeTagsFeature(
            onStartTag: (evt, cancellationToken) =>
                sink.SendAsync(EventType, JsonSerializer.SerializeToElement(new { delta = evt.Delta, isStart = evt.IsStart }), cancellationToken),
            onEndTag: (evt, cancellationToken) =>
                sink.SendAsync(EventType, JsonSerializer.SerializeToElement(new { delta = evt.Delta, isStart = evt.IsStart }), cancellationToken));
    }

    /// <inheritdoc />
    public override ValueTask DispatchAsync(
        TimeTagsFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken)
    {
        if (eventType != EventType)
            throw new NotSupportedException($"Time tags wire codec received unexpected event type '{eventType}'.");

        var delta = payload.GetProperty("delta").GetString()!;
        var isStart = payload.GetProperty("isStart").GetBoolean();
        var evt = new TimeTagDeltaEvent(delta, isStart);
        // 游戏声明 null callback 时按本地语义静默丢弃（仅声明意图、不转发）。
        return isStart
            ? feature.OnStartTag?.Invoke(evt, cancellationToken) ?? ValueTask.CompletedTask
            : feature.OnEndTag?.Invoke(evt, cancellationToken) ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="ActionOptionsFeature" /> 的 wire codec（kind <c>actionOptions</c>）：
/// 参数 <c>maxCount</c>；语义事件 <c>actionOption</c> payload
/// <c>{index, value}</c>（选项完成）或 <c>{arrayEvent: "started"|"completed"}</c>（数组边界）。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ActionOptionsFeatureWireCodec : LongTextWritingFeatureWireCodec<ActionOptionsFeature>
{
    private const string EventType = "actionOption";

    /// <inheritdoc />
    public override string Kind => "actionOptions";

    /// <inheritdoc />
    public override IReadOnlyList<string> EventTypes => [EventType];

    /// <inheritdoc />
    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
            ["maxCount"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum number of action options the model should generate.",
            },
        },
        ["required"] = new JsonArray("kind", "maxCount"),
    };

    /// <inheritdoc />
    protected override void Write(ActionOptionsFeature feature, Utf8JsonWriter writer) =>
        writer.WriteNumber("maxCount", feature.MaxCount);

    /// <inheritdoc />
    protected override ActionOptionsFeature Read(JsonElement wireFeature, IWireEventSink sink)
    {
        var maxCount = wireFeature.GetProperty("maxCount").GetInt32();
        return new ActionOptionsFeature(
            maxCount,
            onOptionCompleted: (evt, cancellationToken) =>
                sink.SendAsync(EventType, JsonSerializer.SerializeToElement(new { index = evt.Index, value = evt.Text }), cancellationToken),
            onArrayStarted: cancellationToken =>
                sink.SendAsync(EventType, JsonSerializer.SerializeToElement(new { arrayEvent = "started" }), cancellationToken),
            onArrayCompleted: cancellationToken =>
                sink.SendAsync(EventType, JsonSerializer.SerializeToElement(new { arrayEvent = "completed" }), cancellationToken));
    }

    /// <inheritdoc />
    public override async ValueTask DispatchAsync(
        ActionOptionsFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken)
    {
        if (eventType != EventType)
            throw new NotSupportedException($"Action options wire codec received unexpected event type '{eventType}'.");

        if (payload.TryGetProperty("arrayEvent", out var arrayEventElement) &&
            arrayEventElement.ValueKind == JsonValueKind.String)
        {
            switch (arrayEventElement.GetString())
            {
                case "started":
                    if (feature.OnArrayStarted is not null)
                        await feature.OnArrayStarted(cancellationToken).ConfigureAwait(false);
                    return;
                case "completed":
                    if (feature.OnArrayCompleted is not null)
                        await feature.OnArrayCompleted(cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    throw new NotSupportedException(
                        $"Unknown actionOption arrayEvent value '{arrayEventElement.GetString()}'.");
            }
        }

        var index = payload.GetProperty("index").GetInt32();
        var value = payload.GetProperty("value").GetString()!;
        await feature.OnOptionCompleted(new ActionOptionCompletedEvent(index, value), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="VariableUpdateFeature" /> 的 wire codec（kind <c>variableUpdate</c>）：
/// 参数无（callback 不上 wire；当前状态与 schema 已在参数包顶层 <c>currentState</c>/<c>stateSchema</c>）；
/// 语义事件 <c>variableUpdate</c> payload <c>{operations:[…]}</c>（复用
/// <see cref="VariableUpdateOperationJsonConverter"/> 的命令编码），SDK 侧解码为
/// <see cref="VariableUpdatePatchProposal" /> 后路由到 <see cref="VariableUpdateFeature.OnPatchProposed" />。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class VariableUpdateFeatureWireCodec : LongTextWritingFeatureWireCodec<VariableUpdateFeature>
{
    private const string EventType = "variableUpdate";

    /// <inheritdoc />
    public override string Kind => "variableUpdate";

    /// <inheritdoc />
    public override IReadOnlyList<string> EventTypes => [EventType];

    /// <inheritdoc />
    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
        },
        ["required"] = new JsonArray("kind"),
    };

    /// <inheritdoc />
    protected override void Write(VariableUpdateFeature feature, Utf8JsonWriter writer)
    {
        _ = feature;
        _ = writer;
    }

    /// <inheritdoc />
    protected override VariableUpdateFeature Read(JsonElement wireFeature, IWireEventSink sink) =>
        new(onPatchProposed: (proposal, cancellationToken) =>
            sink.SendAsync(EventType, SerializeProposal(proposal), cancellationToken));

    /// <inheritdoc />
    public override ValueTask DispatchAsync(
        VariableUpdateFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken)
    {
        if (eventType != EventType)
            throw new NotSupportedException($"Variable update wire codec received unexpected event type '{eventType}'.");

        var proposal = VariableUpdatePatchProposal.FromJson(payload.GetProperty("operations"));
        return feature.OnPatchProposed(proposal, cancellationToken);
    }

    private static JsonElement SerializeProposal(VariableUpdatePatchProposal proposal) =>
        JsonSerializer.SerializeToElement(new { operations = proposal.Operations });
}

/// <summary>
/// <see cref="TextPrimaryOutput" /> 的 wire codec（kind <c>text</c>）：
/// 参数 <c>propertyName</c>（缺省 <c>narrative</c>）；语义事件 <c>chunk</c> payload <c>{text}</c>。
/// 平台端 delta callback 同时喂给 <see cref="WirePrimaryOutputAccumulator" /> 供完成帧聚合全文；
/// 完成回调（OnCompleted）不上 wire——由 SDK 侧完成帧解码触发。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class TextPrimaryOutputWireCodec : LongTextWritingPrimaryOutputWireCodec<TextPrimaryOutput>
{
    private const string EventType = "chunk";

    /// <inheritdoc />
    public override string Kind => "text";

    /// <inheritdoc />
    public override IReadOnlyList<string> EventTypes => [EventType];

    /// <inheritdoc />
    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
            ["propertyName"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Root property name of the text primary output; defaults to 'narrative'.",
            },
        },
        ["required"] = new JsonArray("kind"),
    };

    /// <inheritdoc />
    protected override void Write(TextPrimaryOutput output, Utf8JsonWriter writer)
    {
        writer.WriteString("propertyName", output.PropertyName);
    }

    /// <inheritdoc />
    protected override TextPrimaryOutput Read(
        JsonElement wireOutput, IWireEventSink sink, WirePrimaryOutputAccumulator accumulator)
    {
        var propertyName = wireOutput.TryGetProperty("propertyName", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!
            : "narrative";
        return new TextPrimaryOutput(
            onDelta: (evt, cancellationToken) =>
            {
                accumulator.AppendTextDelta(evt.Delta);
                return sink.SendAsync(
                    EventType, JsonSerializer.SerializeToElement(new { text = evt.Delta }), cancellationToken);
            },
            propertyName);
    }

    /// <inheritdoc />
    public override ValueTask DispatchAsync(
        TextPrimaryOutput output, string eventType, JsonElement payload, CancellationToken cancellationToken) =>
        eventType == EventType
            ? output.OnDelta(new TextDeltaEvent(payload.GetProperty("text").GetString()!), cancellationToken)
            : throw new NotSupportedException($"Text primary output wire codec received unexpected event type '{eventType}'.");

    /// <inheritdoc />
    protected override void WriteCompletionPrimary(
        Utf8JsonWriter writer, WirePrimaryOutputAccumulator accumulator) =>
        writer.WriteStringValue(accumulator.BuildText());
}

/// <summary>
/// <see cref="JsonPrimaryOutput" /> 的 wire codec（kind <c>json</c>）：
/// 参数 <c>propertyName</c> + <c>schema</c>（统一 AST JSON）；语义事件 <c>jsonStream</c>
/// payload <c>{kind, path, …}</c>（事件路径相对主输出属性，与本地
/// <see cref="PrimaryJsonStreamEvent" /> 交付语义一致）。平台端 callback 同时喂给
/// <see cref="WirePrimaryOutputAccumulator" />，完成帧 primary 由事件序列重建（evidence/调试用；
/// 游戏端语义交付走流事件，与本地一致）。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class JsonPrimaryOutputWireCodec : LongTextWritingPrimaryOutputWireCodec<JsonPrimaryOutput>
{
    private const string EventType = "jsonStream";

    /// <inheritdoc />
    public override string Kind => "json";

    /// <inheritdoc />
    public override IReadOnlyList<string> EventTypes => [EventType];

    /// <inheritdoc />
    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
            ["propertyName"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Root property name of the JSON primary output.",
            },
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Unified AST schema (AiJsonSchema.ToJson()) of the primary output value.",
            },
        },
        ["required"] = new JsonArray("kind", "propertyName", "schema"),
    };

    /// <inheritdoc />
    protected override void Write(JsonPrimaryOutput output, Utf8JsonWriter writer)
    {
        writer.WriteString("propertyName", output.PropertyName);
        writer.WritePropertyName("schema");
        output.Schema.ToJson().WriteTo(writer);
    }

    /// <inheritdoc />
    protected override JsonPrimaryOutput Read(
        JsonElement wireOutput, IWireEventSink sink, WirePrimaryOutputAccumulator accumulator)
    {
        var propertyName = wireOutput.GetProperty("propertyName").GetString()!;
        var schema = AiJsonSchema.FromJson(wireOutput.GetProperty("schema"));
        return new JsonPrimaryOutput(
            propertyName,
            schema,
            onJsonEvent: (evt, cancellationToken) =>
            {
                accumulator.AppendJsonEvent(evt.Event);
                return sink.SendAsync(EventType, EncodeJsonStreamEvent(evt.Event), cancellationToken);
            });
    }

    /// <inheritdoc />
    public override ValueTask DispatchAsync(
        JsonPrimaryOutput output, string eventType, JsonElement payload, CancellationToken cancellationToken) =>
        eventType == EventType
            ? output.OnJsonEvent(new PrimaryJsonStreamEvent(DecodeJsonStreamPayload(payload)), cancellationToken)
            : throw new NotSupportedException($"JSON primary output wire codec received unexpected event type '{eventType}'.");

    /// <inheritdoc />
    protected override void WriteCompletionPrimary(
        Utf8JsonWriter writer, WirePrimaryOutputAccumulator accumulator)
    {
        if (accumulator.TryBuildJsonValue(out var value))
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }

    /// <summary>把主输出范围内的 JSON 流事件编码为 <c>jsonStream</c> wire payload（数字以 rawValue 字符串保精度）。</summary>
    internal static JsonElement EncodeJsonStreamEvent(JsonStreamEvent evt)
    {
        object value = evt switch
        {
            JsonStreamObjectStartedEvent => new { kind = "objectStarted", path = evt.Path },
            JsonStreamObjectCompletedEvent => new { kind = "objectCompleted", path = evt.Path },
            JsonStreamArrayStartedEvent => new { kind = "arrayStarted", path = evt.Path },
            JsonStreamArrayCompletedEvent => new { kind = "arrayCompleted", path = evt.Path },
            JsonStreamPropertyNameEvent property => new { kind = "propertyName", path = evt.Path, name = property.Name },
            JsonStreamStringStartedEvent => new { kind = "stringStarted", path = evt.Path },
            JsonStreamStringChunkEvent chunk => new { kind = "stringChunk", path = evt.Path, value = chunk.Value },
            JsonStreamStringCompletedEvent => new { kind = "stringCompleted", path = evt.Path },
            JsonStreamNumberValueEvent number => new { kind = "numberValue", path = evt.Path, rawValue = number.RawValue },
            JsonStreamBooleanValueEvent boolean => new { kind = "booleanValue", path = evt.Path, value = boolean.Value },
            JsonStreamNullValueEvent => new { kind = "nullValue", path = evt.Path },
            _ => throw new NotSupportedException($"JSON stream event type '{evt.GetType().Name}' has no wire encoding."),
        };
        return JsonSerializer.SerializeToElement(value);
    }

    /// <summary>把 <c>jsonStream</c> wire payload 还原为主输出范围内的 JSON 流事件。</summary>
    internal static JsonStreamEvent DecodeJsonStreamPayload(JsonElement payload)
    {
        var kind = payload.GetProperty("kind").GetString()!;
        var path = payload.GetProperty("path").GetString()!;
        return kind switch
        {
            "objectStarted" => JsonStreamEvent.ObjectStarted(path),
            "objectCompleted" => JsonStreamEvent.ObjectCompleted(path),
            "arrayStarted" => JsonStreamEvent.ArrayStarted(path),
            "arrayCompleted" => JsonStreamEvent.ArrayCompleted(path),
            "propertyName" => JsonStreamEvent.PropertyName(path, payload.GetProperty("name").GetString()!),
            "stringStarted" => JsonStreamEvent.StringStarted(path),
            "stringChunk" => JsonStreamEvent.StringChunk(path, payload.GetProperty("value").GetString()!),
            "stringCompleted" => JsonStreamEvent.StringCompleted(path),
            "numberValue" => JsonStreamEvent.NumberValue(path, payload.GetProperty("rawValue").GetString()!),
            "booleanValue" => JsonStreamEvent.BooleanValue(path, payload.GetProperty("value").GetBoolean()),
            "nullValue" => JsonStreamEvent.NullValue(path),
            _ => throw new NotSupportedException($"Unknown jsonStream wire kind '{kind}'."),
        };
    }
}

/// <summary>
/// wire inline 历史桶（解码端重建的只读投影）：只读语义与
/// <see cref="ReadOnlyHistoryBucket" /> 一致——<see cref="IHistoryBucket.AddMessages" /> 抛
/// <see cref="NotSupportedException" />；压缩视图只产出原文回合投影。
/// </summary>
internal sealed class WireInlineHistoryBucket(string description, IReadOnlyList<HistoryTurn> turns) : IHistoryBucket
{
    public string Description { get; } = description;

    public void AddMessages(
        string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages)
    {
        _ = digest;
        _ = metadata;
        _ = messages;
        throw new NotSupportedException("Expert cannot mutate history buckets during execution.");
    }

    public IReadOnlyList<HistoryTurn> GetRawTurns() => turns;

    public IReadOnlyList<HistoryProjectionEntry> GetCompressedView(CompressedViewOptions? options = null)
    {
        _ = options;
        return [.. turns.Select(turn => new HistoryProjectionRawTurn(turn))];
    }
}

/// <summary>
/// 长文本写作类别的 wire codec 门面（L2 实例化）：聚合 Feature/主输出 codec 注册表，
/// 提供参数包编解码、fluent 重放、宽容校验、完成帧聚合与契约 Definition 派生的单一入口。
/// 平台 adapter（胶水）与 SDK 代理共用同一门面的两个方向——物理上不可能漂移。
/// 注册表不可变；新增 Feature/主输出 kind 通过 <see cref="WithFeatureCodec" /> /
/// <see cref="WithPrimaryOutputCodec" /> 以 copy-with 方式扩展，协议层零改动。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LongTextWritingWireCodec
{
    private static readonly TextPrimaryOutputWireCodec TextCodec = new();
    private static readonly JsonPrimaryOutputWireCodec JsonCodec = new();

    private readonly List<ILongTextWritingFeatureWireCodec> _featureCodecs;
    private readonly List<ILongTextWritingPrimaryOutputWireCodec> _primaryCodecs;
    private readonly Dictionary<string, ILongTextWritingFeatureWireCodec> _featuresByKind;
    private readonly Dictionary<Type, ILongTextWritingFeatureWireCodec> _featuresByType;
    private readonly Dictionary<string, ILongTextWritingPrimaryOutputWireCodec> _primaryByKind;
    private readonly Dictionary<Type, ILongTextWritingPrimaryOutputWireCodec> _primaryByType;
    private readonly Dictionary<string, ILongTextWritingFeatureWireCodec> _featureEventRoutes;
    private readonly Dictionary<string, ILongTextWritingPrimaryOutputWireCodec> _primaryEventRoutes;
    private readonly string[] _eventTypes;

    private LongTextWritingWireCodec(
        IReadOnlyList<ILongTextWritingFeatureWireCodec> featureCodecs,
        IReadOnlyList<ILongTextWritingPrimaryOutputWireCodec> primaryCodecs)
    {
        _featureCodecs = [.. featureCodecs.OrderBy(static codec => codec.Kind, StringComparer.Ordinal)];
        _primaryCodecs = [.. primaryCodecs.OrderBy(static codec => codec.Kind, StringComparer.Ordinal)];
        _featuresByKind = [];
        _featuresByType = [];
        _primaryByKind = [];
        _primaryByType = [];
        _featureEventRoutes = [];
        _primaryEventRoutes = [];

        foreach (var codec in _featureCodecs)
        {
            if (!_featuresByKind.TryAdd(codec.Kind, codec))
                throw new ArgumentException($"Duplicate wire feature kind '{codec.Kind}'.", nameof(featureCodecs));
            if (!_featuresByType.TryAdd(codec.FeatureType, codec))
                throw new ArgumentException(
                    $"Wire feature kind '{codec.Kind}' reuses feature type '{codec.FeatureType.FullName}'.",
                    nameof(featureCodecs));
            foreach (var eventType in codec.EventTypes)
            {
                if (!_featureEventRoutes.TryAdd(eventType, codec) || _primaryEventRoutes.ContainsKey(eventType))
                    throw new ArgumentException(
                        $"Semantic event type '{eventType}' is claimed by more than one wire codec.",
                        nameof(featureCodecs));
            }
        }

        foreach (var codec in _primaryCodecs)
        {
            if (!_primaryByKind.TryAdd(codec.Kind, codec))
                throw new ArgumentException($"Duplicate primary output kind '{codec.Kind}'.", nameof(primaryCodecs));
            if (!_primaryByType.TryAdd(codec.OutputType, codec))
                throw new ArgumentException(
                    $"Wire primary output kind '{codec.Kind}' reuses output type '{codec.OutputType.FullName}'.",
                    nameof(primaryCodecs));
            foreach (var eventType in codec.EventTypes)
            {
                if (!_primaryEventRoutes.TryAdd(eventType, codec) || _featureEventRoutes.ContainsKey(eventType))
                    throw new ArgumentException(
                        $"Semantic event type '{eventType}' is claimed by more than one wire codec.",
                        nameof(primaryCodecs));
            }
        }

        _eventTypes = [.. _featureEventRoutes.Keys.Concat(_primaryEventRoutes.Keys).Order(StringComparer.Ordinal)];
    }

    /// <summary>内置注册表（timeTags / actionOptions / variableUpdate + text / json）的门面实例。</summary>
    public static LongTextWritingWireCodec Default { get; } =
        new(
        [
            new TimeTagsFeatureWireCodec(),
            new ActionOptionsFeatureWireCodec(),
            new VariableUpdateFeatureWireCodec(),
        ],
        [TextCodec, JsonCodec]);

    /// <summary>注册的全部语义事件类型（确定性排序）——契约 <c>semanticEventTypes</c> 的单一真源。</summary>
    public IReadOnlyList<string> EventTypes => _eventTypes;

    /// <summary>以 copy-with 方式注册一条 Feature codec（新增 Feature kind 的唯一扩展点）。</summary>
    /// <param name="codec">新 Feature codec。</param>
    /// <returns>包含原有注册与新 codec 的不可变门面实例。</returns>
    public LongTextWritingWireCodec WithFeatureCodec(ILongTextWritingFeatureWireCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new LongTextWritingWireCodec([.. _featureCodecs, codec], [.. _primaryCodecs]);
    }

    /// <summary>以 copy-with 方式注册一条主输出 codec（text|json 之外的扩展点）。</summary>
    /// <param name="codec">新主输出 codec。</param>
    /// <returns>包含原有注册与新 codec 的不可变门面实例。</returns>
    public LongTextWritingWireCodec WithPrimaryOutputCodec(ILongTextWritingPrimaryOutputWireCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new LongTextWritingWireCodec([.. _featureCodecs], [.. _primaryCodecs, codec]);
    }

    /// <summary>
    /// SDK 代理侧编码：把类别 fluent 状态（显式参数）编码为 wire 参数包（POST input）。
    /// ref 桶按 <c>b0, b1, …</c> 分配 bucketId 并连同活动桶引用一起经
    /// <see cref="LongTextWritingWireInvocation.BucketRegistry" /> 返回，供代理响应 dataRequest；
    /// <paramref name="inlineHistoryBuckets" /> 为 true 时桶以 inline 全量快照编码（录制/手捏场景），不入注册表。
    /// </summary>
    public LongTextWritingWireInvocation EncodeInvocation(
        string worldSettings,
        string playerInput,
        string? playerPersona = null,
        string? currentState = null,
        string? stateSchema = null,
        IExpertPrimaryOutput? primaryOutput = null,
        IReadOnlyList<ILongTextWritingFeature>? features = null,
        IReadOnlyList<IHistoryBucket>? historyBuckets = null,
        bool inlineHistoryBuckets = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerInput);
        var registry = new Dictionary<string, IHistoryBucket>(StringComparer.Ordinal);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("worldSettings", worldSettings);
            writer.WriteString("playerInput", playerInput);
            if (playerPersona is not null) writer.WriteString("playerPersona", playerPersona);
            if (currentState is not null) writer.WriteString("currentState", currentState);
            if (stateSchema is not null) writer.WriteString("stateSchema", stateSchema);

            if (primaryOutput is not null)
            {
                writer.WritePropertyName("primaryOutput");
                WritePrimaryOutput(primaryOutput, writer);
            }

            if (features is { Count: > 0 })
            {
                writer.WriteStartArray("features");
                foreach (var feature in features)
                {
                    if (feature is null)
                        throw new ArgumentException("Features cannot contain null entries.", nameof(features));
                    WriteFeature(feature, writer);
                }

                writer.WriteEndArray();
            }

            if (historyBuckets is { Count: > 0 })
            {
                writer.WriteStartArray("historyBuckets");
                var index = 0;
                foreach (var bucket in historyBuckets)
                {
                    if (bucket is null)
                        throw new ArgumentException("History buckets cannot contain null entries.", nameof(historyBuckets));
                    if (inlineHistoryBuckets)
                        WriteInlineBucket(bucket, writer);
                    else
                    {
                        var bucketId = $"b{index}";
                        registry[bucketId] = bucket;
                        WriteRefBucket(bucketId, bucket, writer);
                    }

                    index++;
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return new LongTextWritingWireInvocation(
            JsonDocument.Parse(stream.ToArray()).RootElement.Clone(),
            registry);
    }

    /// <summary>
    /// 平台侧解码：把 wire 参数包还原为类别输入全集。Feature/主输出 callback 绑定到
    /// <paramref name="sink" />（语义事件发射器）；ref 桶经 <paramref name="bucketResolver" />
    /// 解析（平台注入惰性代理），inline 桶重建为只读投影。
    /// </summary>
    public LongTextWritingWireInvocationConfig DecodeInvocation(
        JsonElement input,
        IWireEventSink sink,
        Func<string, IHistoryBucket> bucketResolver)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(bucketResolver);
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Wire invocation input must be a JSON object.", nameof(input));

        var worldSettings = ReadRequiredString(input, "worldSettings");
        var playerInput = ReadRequiredString(input, "playerInput");
        var playerPersona = ReadOptionalString(input, "playerPersona");
        var currentState = ReadOptionalString(input, "currentState");
        var stateSchema = ReadOptionalString(input, "stateSchema");

        var accumulator = new WirePrimaryOutputAccumulator();
        IExpertPrimaryOutput primaryOutput;
        if (input.TryGetProperty("primaryOutput", out var wireOutput))
        {
            var kind = ReadKind(wireOutput, "primaryOutput");
            primaryOutput = _primaryByKind.TryGetValue(kind, out var primaryCodec)
                ? primaryCodec.ReadParameters(wireOutput, sink, accumulator)
                : throw new NotSupportedException(
                    $"Unknown primary output kind '{kind}'. Known kinds: {string.Join(", ", _primaryByKind.Keys.Order())}.");
        }
        else
        {
            // 参数包省略 primaryOutput 时按类别缺省处理（text 主输出、默认属性名），与旧录制兼容。
            primaryOutput = _primaryByKind["text"]
                .ReadParameters(JsonSerializer.SerializeToElement(new { kind = "text" }), sink, accumulator);
        }

        var features = ReadFeatures(input, sink);
        var buckets = ReadHistoryBuckets(input, bucketResolver);

        return new LongTextWritingWireInvocationConfig(
            worldSettings,
            playerInput,
            playerPersona,
            currentState,
            stateSchema,
            primaryOutput,
            features,
            buckets,
            accumulator);
    }

    private List<ILongTextWritingFeature> ReadFeatures(JsonElement input, IWireEventSink sink)
    {
        if (!input.TryGetProperty("features", out var wireFeatures) || wireFeatures.ValueKind != JsonValueKind.Array)
            return [];

        return [.. wireFeatures.EnumerateArray().Select(wireFeature => ReadFeature(wireFeature, sink))];
    }

    private ILongTextWritingFeature ReadFeature(JsonElement wireFeature, IWireEventSink sink)
    {
        var kind = ReadKind(wireFeature, "features[]");
        return _featuresByKind.TryGetValue(kind, out var featureCodec)
            ? featureCodec.ReadParameters(wireFeature, sink)
            : throw new NotSupportedException(
                $"Unknown feature kind '{kind}'. Known kinds: {string.Join(", ", _featuresByKind.Keys.Order())}.");
    }

    private static List<IHistoryBucket> ReadHistoryBuckets(
        JsonElement input, Func<string, IHistoryBucket> bucketResolver)
    {
        if (!input.TryGetProperty("historyBuckets", out var wireBuckets) ||
            wireBuckets.ValueKind != JsonValueKind.Array)
            return [];

        return [.. wireBuckets.EnumerateArray().Select(wireBucket => ReadBucket(wireBucket, bucketResolver))];
    }

    private static IHistoryBucket ReadBucket(JsonElement wireBucket, Func<string, IHistoryBucket> bucketResolver)
    {
        var kind = ReadKind(wireBucket, "historyBuckets[]");
        return kind switch
        {
            "ref" => bucketResolver(wireBucket.GetProperty("bucketId").GetString()
                ?? throw new ArgumentException("Ref history bucket requires a bucketId.")),
            "inline" => ReadInlineBucket(wireBucket),
            _ => throw new NotSupportedException($"Unknown history bucket kind '{kind}'."),
        };
    }

    /// <summary>平台侧 fluent 重放：把解码配置原样灌回专家实例（WithWorldSettings…WithHistoryBuckets）。</summary>
    public static void Apply(AbstractLongTextWritingExpert expert, LongTextWritingWireInvocationConfig config)
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(config);

        expert.WithWorldSettings(config.WorldSettings)
            .WithPlayerInput(config.PlayerInput)
            .WithPlayerPersona(config.PlayerPersona)
            .WithCurrentState(config.CurrentState)
            .WithStateSchema(config.StateSchema)
            .WithPrimaryOutput(config.PrimaryOutput);
        if (config.Features.Count > 0)
            expert.WithFeatures([.. config.Features]);
        if (config.HistoryBuckets.Count > 0)
            expert.WithHistoryBuckets([.. config.HistoryBuckets]);
    }

    /// <summary>
    /// SDK 代理侧事件路由：把 wire 语义事件分发到游戏当前持有的 Feature/主输出 callback 槽位。
    /// 未知事件类型抛 <see cref="NotSupportedException" />。
    /// </summary>
    public ValueTask DispatchEventAsync(
        IExpertPrimaryOutput? primaryOutput,
        IReadOnlyList<ILongTextWritingFeature>? features,
        string eventType,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (_primaryEventRoutes.TryGetValue(eventType, out var primaryCodec))
        {
            if (primaryOutput is null)
                throw new InvalidOperationException(
                    $"Wire event '{eventType}' targets the primary output, but no primary output is configured.");
            return primaryCodec.DispatchAsync(primaryOutput, eventType, payload, cancellationToken);
        }

        if (!_featureEventRoutes.TryGetValue(eventType, out var featureCodec))
            throw new NotSupportedException($"Unknown wire event type '{eventType}' for the long-text-writing category.");

        var feature = features?.FirstOrDefault(featureCodec.FeatureType.IsInstanceOfType)
            ?? throw new InvalidOperationException(
                $"Wire event '{eventType}' targets feature kind '{featureCodec.Kind}', " +
                "which is not configured for this invocation.");
        return featureCodec.DispatchAsync(feature, eventType, payload, cancellationToken);
    }

    /// <summary>
    /// 平台侧完成帧聚合：text 模式 primary = 累加器全文；json 模式 primary = 由事件序列重建的最终值
    /// （evidence/调试用）；metadata/reasoning 透传 <see cref="ExpertCompletionResult" />。
    /// </summary>
    public JsonElement EncodeCompletion(ExpertCompletionResult result, LongTextWritingWireInvocationConfig config)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(config);
        if (!_primaryByType.TryGetValue(config.PrimaryOutput.GetType(), out var primaryCodec))
            throw new NotSupportedException(
                $"Primary output type '{config.PrimaryOutput.GetType().FullName}' has no registered wire codec.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("primary");
            primaryCodec.WriteCompletionPrimary(writer, config.Accumulator);

            if (result.Metadata is { Count: > 0 })
            {
                writer.WriteStartObject("metadata");
                foreach (var pair in result.Metadata)
                    writer.WriteString(pair.Key, pair.Value);
                writer.WriteEndObject();
            }

            if (result.Reasoning is not null)
                writer.WriteString("reasoning", result.Reasoning);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// SDK 代理侧完成帧解码：text 主输出且声明了 OnCompleted 时以全文触发
    /// <see cref="TextCompletedEvent" />（json 主输出的语义交付走流事件，完成帧 primary 仅为
    /// evidence，不触发 callback）；metadata/reasoning 还原为 <see cref="ExpertCompletionResult" />。
    /// </summary>
    public static async ValueTask<ExpertCompletionResult> DecodeCompletionAsync(
        JsonElement output,
        IExpertPrimaryOutput? primaryOutput,
        CancellationToken cancellationToken = default)
    {
        if (output.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Wire completion output must be a JSON object.", nameof(output));

        if (primaryOutput is TextPrimaryOutput { OnCompleted: not null } text &&
            output.TryGetProperty("primary", out var primary) &&
            primary.ValueKind == JsonValueKind.String)
        {
            await text.OnCompleted(new TextCompletedEvent(primary.GetString()!), cancellationToken)
                .ConfigureAwait(false);
        }

        Dictionary<string, string>? metadata = null;
        if (output.TryGetProperty("metadata", out var metadataElement) &&
            metadataElement.ValueKind == JsonValueKind.Object)
        {
            metadata = ReadStringDictionary(metadataElement);
        }

        var reasoning = output.TryGetProperty("reasoning", out var reasoningElement) &&
                        reasoningElement.ValueKind == JsonValueKind.String
            ? reasoningElement.GetString()
            : null;
        return new ExpertCompletionResult(metadata, reasoning);
    }

    /// <summary>
    /// 宽容校验（平台 POST input → 400 problem+json 的 stable code 来源）：
    /// 必填标量存在且为 string、可选标量 string|null、primaryOutput/features/historyBuckets 的
    /// kind 属于注册白名单；不深入 codec 参数（参数错误由解码路径抛出）。
    /// </summary>
    public IReadOnlyList<WireValidationError> Validate(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return [new WireValidationError("input/notObject", "Invocation input must be a JSON object.", string.Empty)];

        return
        [
            .. RequiredStringErrors(input, "worldSettings"),
            .. RequiredStringErrors(input, "playerInput"),
            .. OptionalStringErrors(input, "playerPersona"),
            .. OptionalStringErrors(input, "currentState"),
            .. OptionalStringErrors(input, "stateSchema"),
            .. PrimaryOutputErrors(input),
            .. MemberErrors(input, "features", _featuresByKind.Keys),
            .. MemberErrors(input, "historyBuckets", ["inline", "ref"]),
        ];
    }

    private List<WireValidationError> PrimaryOutputErrors(JsonElement input)
    {
        if (!input.TryGetProperty("primaryOutput", out var wireOutput) ||
            wireOutput.ValueKind == JsonValueKind.Null)
            return [];

        return wireOutput.ValueKind != JsonValueKind.Object
            ? [new WireValidationError("primaryOutput/notObject", "primaryOutput must be a JSON object.", "/primaryOutput")]
            : KindErrors(wireOutput, "primaryOutput", _primaryByKind.Keys);
    }

    private static List<WireValidationError> MemberErrors(
        JsonElement input, string container, IReadOnlyCollection<string> knownKinds)
    {
        if (!input.TryGetProperty(container, out var members) || members.ValueKind == JsonValueKind.Null)
            return [];

        if (members.ValueKind != JsonValueKind.Array)
            return [new WireValidationError($"{container}/notArray", $"{container} must be a JSON array.", $"/{container}")];

        return [.. members.EnumerateArray().Index()
            .SelectMany(entry => MemberKindErrors(entry.Item, container, entry.Index, knownKinds))];
    }

    /// <summary>
    /// 派生契约 inputSchema（D6 单一真源）：标量输入由门面声明，primaryOutput/features 由注册
    /// codec 的参数 schema 派生，historyBuckets 为固定 ref|inline 双表示。指纹自动覆盖 codec 变更。
    /// </summary>
    public JsonElement BuildInputSchema()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["worldSettings"] = ScalarSchema("World settings text governing the whole session."),
                ["playerInput"] = ScalarSchema("The acting player's input for this turn."),
                ["playerPersona"] = ScalarSchema("Optional persona of the acting player."),
                ["currentState"] = ScalarSchema("Optional summary of the current game state."),
                ["stateSchema"] = ScalarSchema(
                    "Optional AI-facing schema of the current state, consumed by the variable-update pass."),
                ["primaryOutput"] = new JsonObject
                {
                    ["description"] = "Primary output declaration: the root property name and whether it streams text or JSON.",
                    ["anyOf"] = new JsonArray(
                        [.. _primaryCodecs.Select<ILongTextWritingPrimaryOutputWireCodec, JsonNode>(static codec => codec.ParameterSchema)]),
                },
                ["features"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Feature declarations enabled for this invocation.",
                    ["items"] = CombineSchemas([.. _featureCodecs.Select(codec => codec.ParameterSchema)]),
                },
                ["historyBuckets"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Read-only history bucket projections the expert may consume.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("inline", "ref") },
                        },
                        ["required"] = new JsonArray("kind"),
                    },
                },
            },
            ["required"] = new JsonArray("worldSettings", "playerInput"),
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject ScalarSchema(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject CombineSchemas(List<JsonObject> schemas) => schemas.Count == 1
        ? schemas[0]
        : new JsonObject
        {
            ["anyOf"] = new JsonArray(
                [.. schemas.Select<JsonObject, JsonNode>(static schema => schema)]),
        };

    private static List<WireValidationError> RequiredStringErrors(JsonElement input, string propertyName) =>
        !input.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null
            ? [new WireValidationError($"{propertyName}/missing", $"'{propertyName}' is required.", $"/{propertyName}")]
            : value.ValueKind != JsonValueKind.String
                ? NonStringError(propertyName, "must be a string.")
                : [];

    private static List<WireValidationError> OptionalStringErrors(JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            ? NonStringError(propertyName, "must be a string when present.")
            : [];

    private static List<WireValidationError> NonStringError(string propertyName, string requirement) =>
    [
        new($"{propertyName}/notString", $"'{propertyName}' {requirement}", $"/{propertyName}"),
    ];

    private static List<WireValidationError> MemberKindErrors(
        JsonElement member, string container, int index, IReadOnlyCollection<string> knownKinds)
    {
        if (member.ValueKind != JsonValueKind.Object)
        {
            return
            [
                new WireValidationError(
                    $"{container}[{index}]/notObject",
                    $"{container}[{index}] must be a JSON object.",
                    $"/{container}/{index}"),
            ];
        }

        return KindErrors(member, $"{container}[{index}]", knownKinds);
    }

    private static List<WireValidationError> KindErrors(
        JsonElement member, string name, IReadOnlyCollection<string> knownKinds)
    {
        if (!member.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            return [new WireValidationError($"{name}/kindMissing", $"{name} requires a string 'kind' discriminator.", $"/{name}")];
        }

        var kind = kindElement.GetString()!;
        return knownKinds.Contains(kind)
            ? []
            :
            [
                new WireValidationError(
                    $"{name}/unknownKind",
                    $"{name} has unknown kind '{kind}'. Known kinds: {string.Join(", ", knownKinds.Order())}.",
                    $"/{name}"),
            ];
    }

    private void WriteFeature(ILongTextWritingFeature feature, Utf8JsonWriter writer)
    {
        if (!_featuresByType.TryGetValue(feature.GetType(), out var codec))
            throw new NotSupportedException(
                $"Long text writing feature type '{feature.GetType().FullName}' has no registered wire codec. " +
                $"Known kinds: {string.Join(", ", _featuresByKind.Keys.Order())}. " +
                "Register a codec via LongTextWritingWireCodec.WithFeatureCodec.");

        writer.WriteStartObject();
        writer.WriteString("kind", codec.Kind);
        codec.WriteParameters(feature, writer);
        writer.WriteEndObject();
    }

    private void WritePrimaryOutput(IExpertPrimaryOutput output, Utf8JsonWriter writer)
    {
        if (!_primaryByType.TryGetValue(output.GetType(), out var codec))
            throw new NotSupportedException(
                $"Primary output type '{output.GetType().FullName}' has no registered wire codec. " +
                $"Known kinds: {string.Join(", ", _primaryByKind.Keys.Order())}.");

        writer.WriteStartObject();
        writer.WriteString("kind", codec.Kind);
        codec.WriteParameters(output, writer);
        writer.WriteEndObject();
    }

    private static void WriteRefBucket(string bucketId, IHistoryBucket bucket, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "ref");
        writer.WriteString("bucketId", bucketId);
        writer.WriteString("description", bucket.Description);
        writer.WriteEndObject();
    }

    private static void WriteInlineBucket(IHistoryBucket bucket, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "inline");
        writer.WriteString("description", bucket.Description);
        writer.WriteStartArray("turns");
        foreach (var turn in bucket.GetRawTurns())
            HistoryBucketWireProjection.WriteTurn(writer, turn);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static WireInlineHistoryBucket ReadInlineBucket(JsonElement wireBucket)
    {
        var description = wireBucket.GetProperty("description").GetString()!;
        var turns = wireBucket.GetProperty("turns").EnumerateArray()
            .Select(HistoryBucketWireProjection.ReadTurn)
            .ToArray();
        return new WireInlineHistoryBucket(description, turns);
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement metadataElement) => new(
        metadataElement.EnumerateObject()
            .Where(static property => property.Value.ValueKind == JsonValueKind.String)
            .Select(static property => new KeyValuePair<string, string>(
                property.Name, property.Value.GetString()!)),
        StringComparer.Ordinal);

    private static string ReadRequiredString(JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"Wire invocation input requires string property '{propertyName}'.");

    private static string? ReadOptionalString(JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadKind(JsonElement member, string name) =>
        member.ValueKind == JsonValueKind.Object &&
        member.TryGetProperty("kind", out var kindElement) &&
        kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString()!
            : throw new ArgumentException($"{name} requires a string 'kind' discriminator.");
}
