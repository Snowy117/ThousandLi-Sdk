using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

public sealed record FakeExpertScenario
{
    public FakeExpertScenario(
        string scenarioId,
        ExpertContractDescriptor contract,
        IReadOnlyList<ExpertSemanticEvent> events,
        JsonElement result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        JsonContractGuardForTesting.ThrowIfUndefined(result, nameof(result));
        ScenarioId = scenarioId;
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Events = new ReadOnlyCollection<ExpertSemanticEvent>([.. events ?? throw new ArgumentNullException(nameof(events))]);
        Result = result.Clone();
    }

    public string ScenarioId { get; }
    public ExpertContractDescriptor Contract { get; }
    public IReadOnlyList<ExpertSemanticEvent> Events { get; }
    public JsonElement Result { get; }
}

public sealed class FakeExpertExecutor : IExpertExecutor
{
    private readonly Dictionary<string, FakeExpertScenario> _scenarios;
    private readonly ConcurrentQueue<ExpertInvocationRecord> _invocations = new();
    private int _invocationSequence;

    public FakeExpertExecutor(IEnumerable<FakeExpertScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        _scenarios = scenarios.ToDictionary(
            scenario => CreateKey(scenario.Contract.Id, scenario.ScenarioId),
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
                $"Fake Expert execution requires a scenario key, but the invocation for contract '{request.Contract.Id}' does not provide one.");
        if (!_scenarios.TryGetValue(CreateKey(request.Contract.Id, request.ScenarioId), out var scenario))
        {
            throw new InvalidOperationException(
                $"Fake Expert scenario '{request.ScenarioId}' is not registered for contract '{request.Contract.Id}'.");
        }

        ValidateContract(request.Contract, scenario.Contract);
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

    private static void ValidateContract(ExpertContractDescriptor required, ExpertContractDescriptor available)
    {
        if (!string.Equals(required.Id, available.Id, StringComparison.Ordinal) ||
            !available.Version.Supports(required.Version) ||
            !string.Equals(required.Fingerprint, available.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fake Expert contract mismatch for '{required.Id}': required {required.Version}/" +
                $"{required.Fingerprint}, available {available.Version}/{available.Fingerprint}.");
        }
    }

    private static string CreateKey(string contractId, string scenarioId) => $"{contractId}\n{scenarioId}";
}

[JetBrains.Annotations.PublicAPI]
public sealed class ThrowingExpertExecutor : IExpertExecutor
{
    /// <summary>共享实例（无状态）。</summary>
    public static ThrowingExpertExecutor Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("No Expert executor is configured for this local runtime.");
    }
}

internal static class JsonContractGuardForTesting
{
    public static void ThrowIfUndefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("JSON value cannot be undefined.", parameterName);
    }
}
