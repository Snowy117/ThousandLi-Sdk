using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

public sealed record BoundPlayerProfile
{
    public BoundPlayerProfile(PlayerId playerId, string playerName, string persona)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        ArgumentNullException.ThrowIfNull(persona);
        PlayerId = playerId;
        PlayerName = playerName;
        Persona = persona;
    }

    public PlayerId PlayerId { get; }
    public string PlayerName { get; }
    public string Persona { get; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PlayerActionEnvelope
{
    public PlayerActionEnvelope(PlayerId playerId, JsonElement payload, string? clientActionId = null)
    {
        JsonContractGuard.ThrowIfUndefined(payload, nameof(payload));
        PlayerId = playerId;
        Payload = payload.Clone();
        ClientActionId = clientActionId;
    }

    public PlayerId PlayerId { get; }
    public JsonElement Payload { get; }
    public string? ClientActionId { get; }
}

public sealed record FrontendEvent
{
    public FrontendEvent(string eventType, JsonElement payload)
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
public sealed record FrontendRequestEnvelope
{
    public FrontendRequestEnvelope(PlayerId playerId, JsonElement payload, string? clientRequestId = null)
    {
        JsonContractGuard.ThrowIfUndefined(payload, nameof(payload));
        PlayerId = playerId;
        Payload = payload.Clone();
        ClientRequestId = clientRequestId;
    }

    public PlayerId PlayerId { get; }
    public JsonElement Payload { get; }
    public string? ClientRequestId { get; }
}

public sealed record FrontendRequestResult
{
    public FrontendRequestResult(JsonElement payload)
    {
        JsonContractGuard.ThrowIfUndefined(payload, nameof(payload));
        Payload = payload.Clone();
    }

    public JsonElement Payload { get; }
}

internal static class JsonContractGuard
{
    public static void ThrowIfUndefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("JSON value cannot be undefined.", parameterName);
    }
}
