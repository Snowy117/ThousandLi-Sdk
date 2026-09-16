using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// 脚本化长文本写作专家的一次确定性输出。可从 <c>--fake-scenarios</c> 文件加载，
/// 也可在测试中直接构造。<see cref="Output" /> 是一次「模型输出」JSON 对象——
/// 脚本专家按 <see cref="AbstractLongTextWritingExpert" /> 的固有输出契约解读其中的
/// 主输出属性、<c>actionOptions</c>/<c>timeTagStart</c>/<c>timeTagEnd</c> 与
/// <c>afterThinking</c>/<c>afterFormat</c> 字段。
/// </summary>
public sealed record ScriptedLongTextWritingScenario
{
    /// <summary>创建脚本化场景并校验输出为对象 JSON。</summary>
    /// <param name="scenarioId">场景标识（仅供记录/报告用）。</param>
    /// <param name="output">一次模型输出的 JSON 对象。</param>
    /// <param name="metadata">专家整理出的回合级 metadata（可与输出中的固有字段合并）。</param>
    /// <param name="reasoning">可选的专家推理文本。</param>
    public ScriptedLongTextWritingScenario(
        string scenarioId,
        JsonElement output,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? reasoning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        if (output.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Scripted long-text-writing output must be a JSON object.", nameof(output));
        ScenarioId = scenarioId;
        Output = output.Clone();
        Metadata = CopyMetadata(metadata);
        Reasoning = reasoning;
    }

    /// <summary>场景标识。</summary>
    public string ScenarioId { get; }

    /// <summary>一次模型输出的 JSON 对象。</summary>
    public JsonElement Output { get; }

    /// <summary>专家整理出的回合级 metadata。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; }

    /// <summary>可选的专家推理文本。</summary>
    public string? Reasoning { get; }

    private static ReadOnlyDictionary<string, string>? CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Scripted expert metadata keys cannot be null or blank.", nameof(metadata));
            copy[pair.Key] = pair.Value;
        }
        return copy.AsReadOnly();
    }
}

/// <summary>
/// 脚本化类型化专家 facade：<c>Use&lt;TAbstract&gt;()</c> 返回一个
/// <see cref="ScriptedLongTextWritingExpert" />，它重放确定性的脚本化「模型输出」，
/// 通过主输出与 Feature 的语义 callback 把输出流给 Game——DevHost 本地跑通
/// 依赖类型化专家 facade 的 Game Package，全程无真实 AI。
/// 每次调用都返回新实例（请求作用域），重放同一份默认脚本，输出确定。
/// </summary>
public sealed class ScriptedLongTextWritingExpertFacade : IExpertFacade
{
    private readonly IReadOnlyList<ScriptedLongTextWritingScenario> _scenarios;

    /// <summary>
    /// 创建 facade；至少需要一个场景。执行期优先按 PlayerInput 匹配 scenarioId，
    /// 无匹配时回退到 <c>scenarioId == "default"</c>，否则取第一个。
    /// </summary>
    /// <param name="scenarios">脚本化专家场景集合。</param>
    public ScriptedLongTextWritingExpertFacade(IEnumerable<ScriptedLongTextWritingScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        List<ScriptedLongTextWritingScenario> list = [.. scenarios];
        if (list.Count == 0)
            throw new ArgumentException(
                "At least one scripted long-text-writing scenario is required.", nameof(scenarios));
        _scenarios = list;
    }

    /// <summary>
    /// 从 <c>--fake-scenarios</c> JSON 数组加载脚本化类型化场景。
    /// 数组项的 <c>output</c> 属性（对象）作为脚本化模型输出；缺 <c>output</c> 的项只服务
    /// <see cref="ScriptedFakeExpertRunner" />（协议执行端口），此处跳过。
    /// 没有任何脚本化项时返回空 facade（输出空对象的默认场景），保证 <c>Use&lt;T&gt;</c> 可用。
    /// </summary>
    /// <param name="path">场景文件路径；null/空白时返回空 facade。</param>
    public static ScriptedLongTextWritingExpertFacade Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Fake scenario file root must be an array.");
        var scenarios = new List<ScriptedLongTextWritingScenario>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("output", out var output))
                continue;
            if (output.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Fake scenario 'output' must be a JSON object.");
            var scenarioId = item.TryGetProperty("scenarioId", out var id) ? id.GetString() : null;
            var metadata = ReadStringMetadata(item);
            var reasoning = item.TryGetProperty("reasoning", out var reasoningValue)
                ? reasoningValue.GetString()
                : null;
            scenarios.Add(new ScriptedLongTextWritingScenario(
                scenarioId ?? "default", output, metadata, reasoning));
        }
        return scenarios.Count == 0 ? Empty() : new ScriptedLongTextWritingExpertFacade(scenarios);
    }

    /// <summary>创建仅含空输出的 facade，供无场景配置时的安全回退。</summary>
    public static ScriptedLongTextWritingExpertFacade Empty() =>
        new([new ScriptedLongTextWritingScenario(
            "default",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)))]);

    /// <inheritdoc />
    public TAbstract Use<TAbstract>() where TAbstract : ExpertBase
    {
        var expert = new ScriptedLongTextWritingExpert(_scenarios);
        expert.Bind(ScriptedExpertExecutionContext.Instance);
        return (TAbstract)(ExpertBase)expert;
    }

    private static Dictionary<string, string>? ReadStringMetadata(JsonElement item)
    {
        if (!item.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException(
                    $"Fake scenario metadata property '{property.Name}' must be a string.");
            result[property.Name] = property.Value.GetString()!;
        }
        return result;
    }
}

/// <summary>
/// 脚本化长文本写作专家（DevHost 假场景）：重放 <see cref="ScriptedLongTextWritingScenario" /> 的
/// 确定性输出，把主输出与 Feature 的语义 callback 驱动起来，让使用
/// <c>context.Experts.Use&lt;AbstractLongTextWritingExpert&gt;()</c> 的 Game 在本地
/// 无 AI 端到端跑通。执行期间读取配置的历史桶（只读视图）并把回合数摘要写入结果 metadata，
/// 证明专家确实消费了历史桶。
/// </summary>
public sealed class ScriptedLongTextWritingExpert : AbstractLongTextWritingExpert
{
    private const int ChunkSize = 4;
    private readonly IReadOnlyList<ScriptedLongTextWritingScenario> _scenarios;
    private ScriptedLongTextWritingScenario _scenario;

    internal ScriptedLongTextWritingExpert(IReadOnlyList<ScriptedLongTextWritingScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        if (scenarios.Count == 0)
            throw new ArgumentException("At least one scripted scenario is required.", nameof(scenarios));
        _scenarios = scenarios;
        _scenario = scenarios[0];
    }

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
        => ExecuteAsync(cancellationToken);

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken)
        => ExecuteAsync(cancellationToken);

    private async Task<ExpertCompletionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
        _scenario = ResolveScenario();
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = await BuildMetadataAsync(cancellationToken).ConfigureAwait(false);
        await StreamPrimaryOutputAsync(cancellationToken).ConfigureAwait(false);
        await StreamFeaturesAsync(cancellationToken).ConfigureAwait(false);
        return new ExpertCompletionResult(metadata, _scenario.Reasoning);
    }

    /// <summary>
    /// Dogfood 场景选择约定：PlayerInput 文本包含 scenarioId（≥3 字符）时优先命中该场景，
    /// 否则回退到 <c>default</c> 或第一个场景。同一实例单次执行，选择在执行期完成。
    /// </summary>
    private ScriptedLongTextWritingScenario ResolveScenario()
    {
        if (PlayerInput is not { } input)
            return _scenarios.FirstOrDefault(scenario => scenario.ScenarioId == "default") ?? _scenarios[0];

        foreach (var scenario in _scenarios)
        {
            if (scenario.ScenarioId.Length >= 3 &&
                input.Contains(scenario.ScenarioId, StringComparison.Ordinal))
                return scenario;
        }

        return _scenarios.FirstOrDefault(scenario => scenario.ScenarioId == "default") ?? _scenarios[0];
    }

    private async ValueTask<Dictionary<string, string>> BuildMetadataAsync(CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_scenario.Metadata is not null)
        {
            foreach (var pair in _scenario.Metadata) metadata[pair.Key] = pair.Value;
        }

        foreach (var field in new[] { "afterThinking", "afterFormat" })
        {
            if (TryGetString(_scenario.Output, field) is { } value) metadata[field] = value;
        }

        var totalTurns = 0;
        foreach (var bucket in ConfiguredHistoryBuckets)
        {
            var count = (await bucket.GetRawTurnsAsync(cancellationToken).ConfigureAwait(false)).Count;
            metadata[$"history.{bucket.Description}"] = count.ToString(CultureInfo.InvariantCulture);
            totalTurns += count;
        }
        if (ConfiguredHistoryBuckets.Count > 0)
            metadata["historyTurns"] = totalTurns.ToString(CultureInfo.InvariantCulture);
        return metadata;
    }

    private async ValueTask StreamPrimaryOutputAsync(CancellationToken cancellationToken)
    {
        switch (ConfiguredPrimaryOutput)
        {
            case TextPrimaryOutput textOutput:
                var value = TryGetString(_scenario.Output, textOutput.PropertyName) ?? string.Empty;
                foreach (var chunk in Chunk(value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await textOutput.OnDelta(new TextDeltaEvent(chunk), cancellationToken).ConfigureAwait(false);
                }
                if (textOutput.OnCompleted is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await textOutput.OnCompleted(new TextCompletedEvent(value), cancellationToken).ConfigureAwait(false);
                }
                break;
            case JsonPrimaryOutput jsonOutput:
                if (!_scenario.Output.TryGetProperty(jsonOutput.PropertyName, out _)) break;
                foreach (var streamEvent in StreamPropertyAsRoot(_scenario.Output, jsonOutput.PropertyName))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await jsonOutput.OnJsonEvent(new PrimaryJsonStreamEvent(streamEvent), cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
        }
    }

    private async ValueTask StreamFeaturesAsync(CancellationToken cancellationToken)
    {
        foreach (var feature in ConfiguredFeatures)
        {
            switch (feature)
            {
                case TimeTagsFeature timeTags:
                    await StreamTimeTagsAsync(timeTags, cancellationToken).ConfigureAwait(false);
                    break;
                case ActionOptionsFeature actionOptions:
                    await StreamActionOptionsAsync(actionOptions, cancellationToken).ConfigureAwait(false);
                    break;
                case VariableUpdateFeature variableUpdate:
                    await ProposeVariableUpdatesAsync(variableUpdate, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// 把脚本化输出中的 <c>variableUpdates</c> 数组作为补丁提案交给 Game 回调（GameHelper
    /// 的 <c>WithVariableUpdate</c> 接线）：缺失或非数组时回退空提案（no-op），
    /// 与 <c>ExpertVariableUpdateExecution</c> 的容错语义一致；数组内畸形命令在解析边界抛
    /// <see cref="System.Text.Json.JsonException" />。
    /// </summary>
    private async ValueTask ProposeVariableUpdatesAsync(
        VariableUpdateFeature feature,
        CancellationToken cancellationToken)
    {
        if (!_scenario.Output.TryGetProperty("variableUpdates", out var patch) ||
            patch.ValueKind != JsonValueKind.Array)
        {
            await feature.OnPatchProposed(new VariableUpdatePatchProposal([]), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await feature.OnPatchProposed(VariableUpdatePatchProposal.FromJson(patch), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask StreamTimeTagsAsync(TimeTagsFeature feature, CancellationToken cancellationToken)
    {
        if (feature.OnStartTag is not null)
        {
            var start = TryGetString(_scenario.Output, "timeTagStart") ?? string.Empty;
            foreach (var chunk in Chunk(start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await feature.OnStartTag(new TimeTagDeltaEvent(chunk, IsStart: true), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        if (feature.OnEndTag is not null)
        {
            var end = TryGetString(_scenario.Output, "timeTagEnd") ?? string.Empty;
            foreach (var chunk in Chunk(end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await feature.OnEndTag(new TimeTagDeltaEvent(chunk, IsStart: false), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask StreamActionOptionsAsync(ActionOptionsFeature feature, CancellationToken cancellationToken)
    {
        if (!_scenario.Output.TryGetProperty("actionOptions", out var options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        if (feature.OnArrayStarted is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await feature.OnArrayStarted(cancellationToken).ConfigureAwait(false);
        }
        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Fake scenario actionOptions items must be strings.");
            cancellationToken.ThrowIfCancellationRequested();
            await feature.OnOptionCompleted(new ActionOptionCompletedEvent(index, option.GetString()!), cancellationToken)
                .ConfigureAwait(false);
            index++;
        }
        if (feature.OnArrayCompleted is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await feature.OnArrayCompleted(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<JsonStreamEvent> StreamPropertyAsRoot(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) yield break;
        yield return JsonStreamEvent.ObjectStarted("");
        yield return JsonStreamEvent.PropertyName($"/{propertyName}", propertyName);
        foreach (var streamEvent in StreamValue($"/{propertyName}", value)) yield return streamEvent;
        yield return JsonStreamEvent.ObjectCompleted("");
    }

    private static IEnumerable<JsonStreamEvent> StreamValue(string path, JsonElement value)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        // JsonValueKind.Undefined 在契约边界一律被拒绝，switch 已显式穷尽全部 8 个成员。
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                // JSON 契约边界已拒绝 Undefined；脚本输出中不会出现，故不产生流事件。
                yield break;
            case JsonValueKind.Object:
                yield return JsonStreamEvent.ObjectStarted(path);
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = $"{path}/{property.Name}";
                    yield return JsonStreamEvent.PropertyName(childPath, property.Name);
                    foreach (var streamEvent in StreamValue(childPath, property.Value)) yield return streamEvent;
                }
                yield return JsonStreamEvent.ObjectCompleted(path);
                break;
            case JsonValueKind.Array:
                yield return JsonStreamEvent.ArrayStarted(path);
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var childPath = $"{path}/{index}";
                    foreach (var streamEvent in StreamValue(childPath, item)) yield return streamEvent;
                    index++;
                }
                yield return JsonStreamEvent.ArrayCompleted(path);
                break;
            case JsonValueKind.String:
                yield return JsonStreamEvent.StringStarted(path);
                foreach (var chunk in Chunk(value.GetString() ?? string.Empty))
                    yield return JsonStreamEvent.StringChunk(path, chunk);
                yield return JsonStreamEvent.StringCompleted(path);
                break;
            case JsonValueKind.Number:
                yield return JsonStreamEvent.NumberValue(path, value.GetRawText());
                break;
            case JsonValueKind.True:
                yield return JsonStreamEvent.BooleanValue(path, true);
                break;
            case JsonValueKind.False:
                yield return JsonStreamEvent.BooleanValue(path, false);
                break;
            case JsonValueKind.Null:
                yield return JsonStreamEvent.NullValue(path);
                break;
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IEnumerable<string> Chunk(string value)
    {
        for (var index = 0; index < value.Length; index += ChunkSize)
            yield return value.Substring(index, Math.Min(ChunkSize, value.Length - index));
    }
}

/// <summary>
/// The execution context bound to scripted experts: replay drives the fluent callbacks directly, so
/// any model access or settings resolution attempt is a scripting bug and fails loudly.
/// </summary>
internal sealed class ScriptedExpertExecutionContext : IExpertExecutionContext
{
    public static ScriptedExpertExecutionContext Instance { get; } = new();

    public IRuntimeBasicAi BasicAi => throw new NotSupportedException(
        "Scripted experts replay deterministic scenarios and never invoke a model.");

    public ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(
        CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TSettings());
    }

    public BoundPlayerProfile PlayerProfile => new(
        new PlayerId("scripted-player"),
        "Scripted Player",
        "Scripted expert replay player");

    public ILogger Logger => NullLogger.Instance;
}
