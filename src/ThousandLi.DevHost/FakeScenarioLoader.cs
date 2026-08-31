using System.Text.Json;
using System.Collections.Concurrent;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// Deterministic scripted runner for the expert invocation protocol (Playground / tests). This
/// is a tool surface, not a game-authoring API; game code reaches experts through the typed facade.
/// </summary>
[JetBrains.Annotations.PublicAPI]
public sealed class ScriptedFakeExpertRunner : IExpertRunner
{
    private readonly IReadOnlyDictionary<string, ScriptedScenario> _scenarios;
    private readonly ConcurrentQueue<ExpertInvocationRecord> _invocations = new();
    private long _sequence;

    private ScriptedFakeExpertRunner(IReadOnlyDictionary<string, ScriptedScenario> scenarios)
    {
        _scenarios = scenarios;
    }

    /// <summary>已注册场景覆盖的契约 id 列表（确定性排序）。</summary>
    public IReadOnlyList<string> Contracts =>
        [.. _scenarios.Values.Select(value => value.ContractId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<ExpertInvocationRecord> Invocations => [.. _invocations];

    /// <summary>
    /// 从 <c>--fake-scenarios</c> JSON 数组加载脚本化场景：每项为
    /// <c>{ contract: "id", scenarioId, events?: [...], result }</c>。
    /// </summary>
    /// <param name="path">场景文件路径；null/空白时返回空 runner。</param>
    public static ScriptedFakeExpertRunner Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ScriptedFakeExpertRunner(new Dictionary<string, ScriptedScenario>());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Fake scenario file root must be an array.");
        var scenarios = new Dictionary<string, ScriptedScenario>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Each Fake scenario must be a JSON object.");
            var contractId = item.GetProperty("contract").GetString()!;
            var scenarioId = item.GetProperty("scenarioId").GetString()!;
            var events = item.TryGetProperty("events", out var eventsElement)
                ? eventsElement.EnumerateArray().Select(value => new ExpertSemanticEvent(
                    value.GetProperty("eventType").GetString()!, value.GetProperty("payload"))).ToArray()
                : [];
            var scenario = new ScriptedScenario(contractId, scenarioId, events, item.GetProperty("result"));
            if (!scenarios.TryAdd(Key(scenario.ContractId, scenario.ScenarioId), scenario))
                throw new InvalidOperationException($"Duplicate Fake scenario '{scenario.ScenarioId}' for contract '{scenario.ContractId}'.");
        }
        return new ScriptedFakeExpertRunner(scenarios);
    }

    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ScenarioId is null)
            throw new InvalidOperationException(
                $"Fake Expert execution requires a scenario key, but the invocation for contract '{request.ContractId}' does not provide one.");
        if (!_scenarios.TryGetValue(Key(request.ContractId, request.ScenarioId), out var scenario))
            throw new InvalidOperationException($"No Fake scenario '{request.ScenarioId}' for '{request.ContractId}'.");
        var invocationId = $"fake-{Interlocked.Increment(ref _sequence):D8}";
        _invocations.Enqueue(new ExpertInvocationRecord(request, invocationId));
        foreach (var semanticEvent in scenario.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await events.WriteAsync(semanticEvent, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ExpertInvocationResult(invocationId, scenario.Result);
    }

    private static string Key(string contractId, string scenarioId) => $"{contractId}\n{scenarioId}";

    private sealed record ScriptedScenario(
        string ContractId,
        string ScenarioId,
        IReadOnlyList<ExpertSemanticEvent> Events,
        JsonElement Result)
    {
        public JsonElement Result { get; } = Result.Clone();
    }
}
