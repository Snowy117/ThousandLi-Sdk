using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

public sealed record FakeExpertScenario
{
    public FakeExpertScenario(
        string scenarioId,
        string contractId,
        IReadOnlyList<ExpertSemanticEvent> events,
        JsonElement result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        JsonContractGuardForTesting.ThrowIfUndefined(result, nameof(result));
        ScenarioId = scenarioId;
        ContractId = contractId;
        Events = new ReadOnlyCollection<ExpertSemanticEvent>([.. events ?? throw new ArgumentNullException(nameof(events))]);
        Result = result.Clone();
    }

    public string ScenarioId { get; }

    public string ContractId { get; }

    public IReadOnlyList<ExpertSemanticEvent> Events { get; }

    public JsonElement Result { get; }
}

public sealed class FakeExpertRunner : IExpertRunner
{
    private readonly Dictionary<string, FakeExpertScenario> _scenarios;
    private readonly ConcurrentQueue<ExpertInvocationRecord> _invocations = new();
    private int _invocationSequence;

    public FakeExpertRunner(IEnumerable<FakeExpertScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        _scenarios = scenarios.ToDictionary(
            scenario => CreateKey(scenario.ContractId, scenario.ScenarioId),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<ExpertInvocationRecord> Invocations => [.. _invocations];

    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        if (request.ScenarioId is null)
            throw new InvalidOperationException(
                $"Fake Expert execution requires a scenario key, but the invocation for contract '{request.ContractId}' does not provide one.");
        if (!_scenarios.TryGetValue(CreateKey(request.ContractId, request.ScenarioId), out var scenario))
        {
            throw new InvalidOperationException(
                $"Fake Expert scenario '{request.ScenarioId}' is not registered for contract '{request.ContractId}'.");
        }

        var invocationId = $"fake-{Interlocked.Increment(ref _invocationSequence):D4}";
        _invocations.Enqueue(new ExpertInvocationRecord(request, invocationId));
        foreach (var semanticEvent in scenario.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await events.WriteAsync(
                new ExpertSemanticEvent(semanticEvent.EventType, semanticEvent.Payload),
                cancellationToken).ConfigureAwait(false);
        }
        return new ExpertInvocationResult(invocationId, scenario.Result);
    }

    private static string CreateKey(string contractId, string scenarioId) => $"{contractId}\n{scenarioId}";
}

internal static class JsonContractGuardForTesting
{
    public static void ThrowIfUndefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("JSON value cannot be undefined.", parameterName);
    }
}
