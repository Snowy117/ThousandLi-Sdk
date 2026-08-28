using System.Collections.ObjectModel;
using System.Security.Cryptography;
using JetBrains.Annotations;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertContracts;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ExpertContractAttribute : Attribute
{
    public ExpertContractAttribute(string id, int majorVersion, int minorVersion, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        Id = id;
        Version = new ContractVersion(majorVersion, minorVersion);
        Fingerprint = fingerprint;
    }

    public string Id { get; }
    public ContractVersion Version { get; }
    public string Fingerprint { get; }
}

/// <summary>
/// Compile-time contract marker implemented by abstract expert types. Implementations are read by
/// the registry and tests directly on concrete types (static abstract members have no interface
/// dispatch), so this member is intentionally part of the published authoring surface.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertContract
{
    static abstract ExpertContractDefinition Definition { get; }
}

public sealed record ExpertContractDefinition
{
    public ExpertContractDefinition(
        JsonElement? inputSchema = null,
        IReadOnlyList<string>? semanticEventTypes = null,
        JsonElement? outputSchema = null)
    {
        if (inputSchema is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Input schema cannot be undefined.", nameof(inputSchema));
        if (outputSchema is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Output schema cannot be undefined.", nameof(outputSchema));
        var eventTypes = semanticEventTypes ?? [];
        foreach (var eventType in eventTypes)
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventTypes.Distinct(StringComparer.Ordinal).Count() != eventTypes.Count)
            throw new ArgumentException("Semantic event types must be unique.", nameof(semanticEventTypes));
        InputSchema = inputSchema?.Clone();
        OutputSchema = outputSchema?.Clone();
        SemanticEventTypes = new ReadOnlyCollection<string>([.. eventTypes]);
    }

    public JsonElement? InputSchema { get; }
    public IReadOnlyList<string> SemanticEventTypes { get; }
    public JsonElement? OutputSchema { get; }
}

public static class ExpertContractFingerprint
{
    public static string Compute(string id, ContractVersion version, ExpertContractDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(definition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractId", id);
            writer.WriteStartObject("version");
            writer.WriteNumber("major", version.Major);
            writer.WriteNumber("minor", version.Minor);
            writer.WriteEndObject();
            writer.WritePropertyName("inputSchema");
            WriteCanonicalJson(writer, definition.InputSchema);
            writer.WritePropertyName("semanticEventTypes");
            writer.WriteStartArray();
            foreach (var eventType in definition.SemanticEventTypes.Order(StringComparer.Ordinal))
                writer.WriteStringValue(eventType);
            writer.WriteEndArray();
            writer.WritePropertyName("outputSchema");
            WriteCanonicalJson(writer, definition.OutputSchema);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement? element)
    {
        if (element is null)
        {
            writer.WriteNullValue();
            return;
        }

        WriteCanonicalValue(writer, element.Value);
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalValue(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
