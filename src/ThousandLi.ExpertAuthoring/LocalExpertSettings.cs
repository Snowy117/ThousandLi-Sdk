using System.Text.Json;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Settings resolution failure: malformed or non-object local override files, unreadable files, or
/// typed deserialization failures. The <see cref="FilePath"/> carries the offending file when known.
/// </summary>
public sealed class LocalExpertSettingsException(
    string message,
    Exception? innerException = null,
    string? filePath = null)
    : Exception(message, innerException)
{
    public string? FilePath { get; } = filePath;
}

/// <summary>
/// The local two-layer expert settings policy: schema-declared defaults merged with a single
/// optional local override file. The production game-scoped and session-scoped layers are platform
/// exclusions and deliberately absent here. Objects merge recursively; arrays and scalars are
/// replaced whole by the override layer.
/// </summary>
public static class LocalExpertSettingsResolver
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static JsonElement Merge(JsonElement? schemaDefaults, JsonElement? localOverrides)
    {
        ValidateLayer(schemaDefaults, nameof(schemaDefaults));
        ValidateLayer(localOverrides, nameof(localOverrides));
        return MergeLayers(schemaDefaults, localOverrides);
    }

    /// <summary>
    /// Resolves the effective settings document. A null or missing override file yields the schema
    /// defaults; a present override file that is unreadable, invalid JSON, or not an object fails
    /// fast with <see cref="LocalExpertSettingsException"/>.
    /// </summary>
    public static async ValueTask<JsonElement> ResolveAsync(
        JsonElement? schemaDefaults,
        FileInfo? overrideFile,
        CancellationToken cancellationToken = default)
    {
        ValidateLayer(schemaDefaults, nameof(schemaDefaults));
        if (overrideFile is null || !overrideFile.Exists)
            return MergeLayers(schemaDefaults, null);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(overrideFile.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalExpertSettingsException(
                $"The local expert settings override file '{overrideFile.FullName}' could not be read.",
                exception,
                overrideFile.FullName);
        }

        JsonElement overrides;
        try
        {
            using var document = JsonDocument.Parse(text);
            overrides = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new LocalExpertSettingsException(
                $"The local expert settings override file '{overrideFile.FullName}' is not valid JSON.",
                exception,
                overrideFile.FullName);
        }

        if (overrides.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
        {
            throw new LocalExpertSettingsException(
                $"The local expert settings override file '{overrideFile.FullName}' must contain a JSON object.",
                filePath: overrideFile.FullName);
        }

        return MergeLayers(schemaDefaults, overrides);
    }

    private static void ValidateLayer(JsonElement? layer, string parameterName)
    {
        switch (layer)
        {
            case { ValueKind: JsonValueKind.Undefined }:
                throw new ArgumentException("JSON value cannot be undefined.", parameterName);
            case { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null }:
                throw new ArgumentException("Expert settings layers must be JSON objects.", parameterName);
        }
    }

    private static JsonElement MergeLayers(JsonElement? defaults, JsonElement? overrides)
    {
        if (defaults is not { ValueKind: JsonValueKind.Object } defaultObject)
            return overrides is { ValueKind: JsonValueKind.Object } overrideObject ? overrideObject.Clone() : EmptyObject;
        if (overrides is not { ValueKind: JsonValueKind.Object } overrideLayer)
            return defaultObject.Clone();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in defaultObject.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                var hasOverride = overrideLayer.TryGetProperty(property.Name, out var overrideValue);
                if (hasOverride &&
                    property.Value.ValueKind == JsonValueKind.Object &&
                    overrideValue.ValueKind == JsonValueKind.Object)
                {
                    WriteMergedObject(writer, overrideValue, property.Value);
                }
                else
                {
                    (hasOverride ? overrideValue : property.Value).WriteTo(writer);
                }
            }

            foreach (var property in overrideLayer.EnumerateObject()
                         .Where(property => !defaultObject.TryGetProperty(property.Name, out _)))
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteMergedObject(Utf8JsonWriter writer, JsonElement high, JsonElement low)
    {
        writer.WriteStartObject();
        foreach (var property in low.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            var hasHigh = high.TryGetProperty(property.Name, out var highValue);
            if (hasHigh &&
                property.Value.ValueKind == JsonValueKind.Object &&
                highValue.ValueKind == JsonValueKind.Object)
            {
                WriteMergedObject(writer, highValue, property.Value);
            }
            else
            {
                (hasHigh ? highValue : property.Value).WriteTo(writer);
            }
        }

        foreach (var property in high.EnumerateObject()
                     .Where(property => !low.TryGetProperty(property.Name, out _)))
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
