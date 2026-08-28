using System.Text.Json;
using System.Collections.Concurrent;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

[JetBrains.Annotations.PublicAPI]
public sealed class ScriptedFakeExpertExecutor : IExpertExecutor
{
    private readonly IReadOnlyDictionary<string, ScriptedScenario> _scenarios;
    private readonly ConcurrentQueue<ExpertInvocationRecord> _invocations = new();
    private long _sequence;

    private ScriptedFakeExpertExecutor(IReadOnlyDictionary<string, ScriptedScenario> scenarios)
    {
        _scenarios = scenarios;
    }

    public IReadOnlyList<ExpertContractDescriptor> Contracts =>
        [.. _scenarios.Values.Select(value => value.Contract).DistinctBy(value => value.Id).OrderBy(value => value.Id, StringComparer.Ordinal)];

    public IReadOnlyList<ExpertInvocationRecord> Invocations => [.. _invocations];

    public static ScriptedFakeExpertExecutor Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ScriptedFakeExpertExecutor(new Dictionary<string, ScriptedScenario>());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Fake scenario file root must be an array.");
        var scenarios = new Dictionary<string, ScriptedScenario>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Each Fake scenario must be a JSON object.");
            var contractElement = item.GetProperty("contract");
            var versionElement = contractElement.GetProperty("version");
            var contract = new ExpertContractDescriptor(
                contractElement.GetProperty("id").GetString()!,
                new ContractVersion(
                    versionElement.GetProperty("major").GetInt32(),
                    versionElement.GetProperty("minor").GetInt32()),
                contractElement.GetProperty("fingerprint").GetString()!);
            var scenarioId = item.GetProperty("scenarioId").GetString()!;
            var events = item.TryGetProperty("events", out var eventsElement)
                ? eventsElement.EnumerateArray().Select(value => new ExpertSemanticEvent(
                    value.GetProperty("eventType").GetString()!, value.GetProperty("payload"))).ToArray()
                : [];
            var scenario = new ScriptedScenario(contract, events, item.GetProperty("result"));
            if (!scenarios.TryAdd(Key(contract.Id, scenarioId), scenario))
                throw new InvalidOperationException($"Duplicate Fake scenario '{scenarioId}' for contract '{contract.Id}'.");
        }
        foreach (var contractGroup in scenarios.Values.GroupBy(scenario => scenario.Contract.Id, StringComparer.Ordinal))
        {
            var contracts = contractGroup.Select(scenario => scenario.Contract).Distinct().ToArray();
            if (contracts.Length != 1)
                throw new InvalidOperationException($"Fake scenarios for contract '{contractGroup.Key}' must use one version and fingerprint.");
        }
        return new ScriptedFakeExpertExecutor(scenarios);
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
                $"Fake Expert execution requires a scenario key, but the invocation for contract '{request.Contract.Id}' does not provide one.");
        if (!_scenarios.TryGetValue(Key(request.Contract.Id, request.ScenarioId), out var scenario))
            throw new InvalidOperationException($"No Fake scenario '{request.ScenarioId}' for '{request.Contract.Id}'.");
        if (!scenario.Contract.Version.Supports(request.Contract.Version) ||
            !string.Equals(scenario.Contract.Fingerprint, request.Contract.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fake scenario contract mismatch for '{request.Contract.Id}'.");
        }
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
        ExpertContractDescriptor Contract,
        IReadOnlyList<ExpertSemanticEvent> Events,
        JsonElement Result)
    {
        public JsonElement Result { get; } = Result.Clone();
    }
}
