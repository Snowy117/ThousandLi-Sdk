using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

[StructLayout(LayoutKind.Auto)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public readonly record struct ContractVersion
{
    public ContractVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }
    public int Minor { get; }

    public bool Supports(ContractVersion required) => Major == required.Major && Minor >= required.Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record ExpertContractDescriptor
{
    public ExpertContractDescriptor(string id, ContractVersion version, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        Id = id;
        Version = version;
        Fingerprint = fingerprint;
    }

    public string Id { get; }
    public ContractVersion Version { get; }
    public string Fingerprint { get; }
}

public sealed record ExpertInvocationRequest
{
    public ExpertInvocationRequest(ExpertContractDescriptor contract, string scenarioId, JsonElement input)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        JsonContractGuard.ThrowIfUndefined(input, nameof(input));
        ScenarioId = scenarioId;
        Input = input.Clone();
    }

    public ExpertContractDescriptor Contract { get; }
    public string ScenarioId { get; }
    public JsonElement Input { get; }
}

public sealed record ExpertInvocationRecord
{
    public ExpertInvocationRecord(ExpertInvocationRequest request, string invocationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        Request = new ExpertInvocationRequest(request.Contract, request.ScenarioId, request.Input);
        InvocationId = invocationId;
    }

    public ExpertInvocationRequest Request { get; }
    public string InvocationId { get; }
}

public sealed record ExpertSemanticEvent
{
    public ExpertSemanticEvent(string eventType, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        JsonContractGuard.ThrowIfUndefined(payload, nameof(payload));
        EventType = eventType;
        Payload = payload.Clone();
    }

    public string EventType { get; }
    public JsonElement Payload { get; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertInvocationResult
{
    public ExpertInvocationResult(string invocationId, JsonElement output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        JsonContractGuard.ThrowIfUndefined(output, nameof(output));
        InvocationId = invocationId;
        Output = output.Clone();
    }

    public string InvocationId { get; }
    public JsonElement Output { get; }
}

public interface IExpertSemanticEventSink
{
    ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default);
}

public interface IExpertExecutor
{
    ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);
}

public sealed record GamePackageCompatibility
{
    public GamePackageCompatibility(
        ContractVersion runtime,
        ContractVersion? frontend,
        IReadOnlyList<ExpertContractDescriptor>? expertContracts = null)
    {
        Runtime = runtime;
        Frontend = frontend;
        ExpertContracts = new ReadOnlyCollection<ExpertContractDescriptor>([.. expertContracts ?? []]);
    }

    public ContractVersion Runtime { get; }
    public ContractVersion? Frontend { get; }
    public IReadOnlyList<ExpertContractDescriptor> ExpertContracts { get; }
}

public static class SdkContracts
{
    public static ContractVersion Runtime { get; } = new(1, 0);
    public static ContractVersion Frontend { get; } = new(1, 0);
}
