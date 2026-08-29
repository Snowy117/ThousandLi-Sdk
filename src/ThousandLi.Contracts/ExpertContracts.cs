using System.Collections.ObjectModel;
using System.ComponentModel;
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
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ExpertInvocationRequest(ExpertContractDescriptor contract, string scenarioId, JsonElement input)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        JsonContractGuard.ThrowIfUndefined(input, nameof(input));
        ScenarioId = scenarioId;
        Input = input.Clone();
    }

    public ExpertInvocationRequest(ExpertContractDescriptor contract, string? scenarioId, JsonElement input, string? channelKey)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        if (scenarioId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        if (channelKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        JsonContractGuard.ThrowIfUndefined(input, nameof(input));
        ScenarioId = scenarioId;
        ChannelKey = channelKey;
        Input = input.Clone();
    }

    public ExpertContractDescriptor Contract { get; }
    public string? ScenarioId { get; }
    public string? ChannelKey { get; }
    public JsonElement Input { get; }
}

public sealed record ExpertInvocationRecord
{
    public ExpertInvocationRecord(ExpertInvocationRequest request, string invocationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        Request = new ExpertInvocationRequest(request.Contract, request.ScenarioId, request.Input, request.ChannelKey);
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

// The expert invocation protocol types above (request / semantic events / sink / result) are the
// Playground, recording, and remote-wire tool surface. Game authors use the typed expert facade
// (IExpertFacade.Use<TAbstract>()) instead; there is deliberately no polymorphic executor port on
// the game-facing ActionContext.

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
    public static ContractVersion Runtime { get; } = new(1, 1);
    public static ContractVersion Frontend { get; } = new(1, 0);
}
