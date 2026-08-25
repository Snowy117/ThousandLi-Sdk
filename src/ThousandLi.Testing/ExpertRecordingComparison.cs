using System.Text.Json;

namespace ThousandLi.Testing;

/// <summary>One replay comparison divergence, attributed to the determinism layer that detected it.</summary>
public sealed record RecordingComparisonDivergence(string Layer, string Detail);

/// <summary>The layered replay comparison outcome; <see cref="Matches"/> is true only when no layer diverges.</summary>
public sealed record RecordingComparisonResult(bool Matches, IReadOnlyList<RecordingComparisonDivergence> Divergences)
{
    public static RecordingComparisonResult Match { get; } = new(true, []);
}

/// <summary>
/// Layered deterministic comparison of a recorded invocation against a live invocation. Real
/// model output is naturally non-deterministic, so the default layers compare only what must be
/// stable: the contract identity, the event type sequence, the structural shape of every event
/// payload (recursive key sets and value kinds, never values), and the terminal schema (status
/// plus output shape). Exact payload, input, and output value comparison is the opt-in strict
/// mode. Error message text is never compared: it is diagnostic text, not behavior.
/// </summary>
public static class ExpertRecordingComparison
{
    public const string ContractLayer = "contract";
    public const string EventTypeSequenceLayer = "event-type-sequence";
    public const string EventShapeLayer = "event-shape";
    public const string TerminalLayer = "terminal";
    public const string StrictPayloadLayer = "strict-payload";
    public const string StrictInputLayer = "strict-input";

    private const int SnippetLength = 96;

    public static RecordingComparisonResult Compare(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        bool strictPayloads = false)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var divergences = new List<RecordingComparisonDivergence>();

        var expectedContract = expected.Contract;
        var actualContract = actual.Contract;
        if (!string.Equals(expectedContract.Id, actualContract.Id, StringComparison.Ordinal) ||
            !actualContract.Version.Supports(expectedContract.Version) ||
            !string.Equals(expectedContract.Fingerprint, actualContract.Fingerprint, StringComparison.Ordinal))
        {
            divergences.Add(new RecordingComparisonDivergence(
                ContractLayer,
                $"expected contract '{expectedContract.Id}' {expectedContract.Version} with fingerprint " +
                $"'{expectedContract.Fingerprint}', actual '{actualContract.Id}' {actualContract.Version} with " +
                $"fingerprint '{actualContract.Fingerprint}'."));
            return new RecordingComparisonResult(false, divergences);
        }

        var typesMatch = CompareEventTypeSequences(expected, actual, divergences);
        var shapesMatch = typesMatch && CompareEventShapes(expected, actual, divergences);
        if (strictPayloads)
        {
            if (shapesMatch)
                CompareStrictEventPayloads(expected, actual, divergences);
            CompareStrictInputs(expected, actual, divergences);
        }

        CompareTerminals(expected, actual, strictPayloads && shapesMatch, divergences);
        return new RecordingComparisonResult(divergences.Count == 0, divergences);
    }

    private static bool CompareEventTypeSequences(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        List<RecordingComparisonDivergence> divergences)
    {
        if (expected.Events.Count != actual.Events.Count)
        {
            divergences.Add(new RecordingComparisonDivergence(
                EventTypeSequenceLayer,
                $"expected {expected.Events.Count} events, actual {actual.Events.Count}."));
            return false;
        }

        for (var index = 0; index < expected.Events.Count; index++)
        {
            var expectedType = expected.Events[index].EventType;
            var actualType = actual.Events[index].EventType;
            if (string.Equals(expectedType, actualType, StringComparison.Ordinal))
                continue;
            divergences.Add(new RecordingComparisonDivergence(
                EventTypeSequenceLayer,
                $"first divergence at event {index}: expected type '{expectedType}', actual '{actualType}'."));
            return false;
        }

        return true;
    }

    private static bool CompareEventShapes(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        List<RecordingComparisonDivergence> divergences)
    {
        for (var index = 0; index < expected.Events.Count; index++)
        {
            var detail = DescribeShapeDivergence(
                expected.Events[index].Payload,
                actual.Events[index].Payload,
                $"events[{index}].payload");
            if (detail is null) continue;
            divergences.Add(new RecordingComparisonDivergence(EventShapeLayer, detail));
            return false;
        }

        return true;
    }

    private static void CompareStrictEventPayloads(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        List<RecordingComparisonDivergence> divergences)
    {
        for (var index = 0; index < expected.Events.Count; index++)
        {
            var expectedPayload = expected.Events[index].Payload;
            var actualPayload = actual.Events[index].Payload;
            if (JsonElement.DeepEquals(expectedPayload, actualPayload)) continue;
            divergences.Add(new RecordingComparisonDivergence(
                StrictPayloadLayer,
                $"events[{index}].payload differs: expected {Snippet(expectedPayload)}, " +
                $"actual {Snippet(actualPayload)}."));
            return;
        }
    }

    private static void CompareStrictInputs(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        List<RecordingComparisonDivergence> divergences)
    {
        if (JsonElement.DeepEquals(expected.Input, actual.Input)) return;
        divergences.Add(new RecordingComparisonDivergence(
            StrictInputLayer,
            $"input differs: expected {Snippet(expected.Input)}, actual {Snippet(actual.Input)}."));
    }

    private static void CompareTerminals(
        ExpertInvocationRecording expected,
        ExpertInvocationRecording actual,
        bool strictOutput,
        List<RecordingComparisonDivergence> divergences)
    {
        var expectedTerminal = expected.Terminal;
        var actualTerminal = actual.Terminal;
        if (!string.Equals(expectedTerminal.Status, actualTerminal.Status, StringComparison.Ordinal))
        {
            divergences.Add(new RecordingComparisonDivergence(
                TerminalLayer,
                $"expected terminal '{expectedTerminal.Status}', actual '{actualTerminal.Status}'."));
            return;
        }

        if (expectedTerminal.Status != ExpertRecordingTerminal.Committed) return;
        var expectedOutput = expectedTerminal.Output!.Value;
        var actualOutput = actualTerminal.Output!.Value;
        var shapeDetail = DescribeShapeDivergence(expectedOutput, actualOutput, "terminal.output");
        if (shapeDetail is not null)
        {
            divergences.Add(new RecordingComparisonDivergence(TerminalLayer, shapeDetail));
            return;
        }

        if (strictOutput && !JsonElement.DeepEquals(expectedOutput, actualOutput))
        {
            divergences.Add(new RecordingComparisonDivergence(
                StrictPayloadLayer,
                $"terminal.output differs: expected {Snippet(expectedOutput)}, actual {Snippet(actualOutput)}."));
        }
    }

    /// <summary>
    /// Recursive structural comparison: value kinds must match, object key sets must match
    /// (order-insensitive), and array lengths must match. Primitive values are never compared —
    /// that is the strict layer's job.
    /// </summary>
    private static string? DescribeShapeDivergence(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind)
            return $"{path}: expected kind {expected.ValueKind}, actual {actual.ValueKind}.";

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(property => property.Name).ToArray();
                var actualNames = actual.EnumerateObject().Select(property => property.Name).ToArray();
                var missing = expectedNames.Except(actualNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var extra = actualNames.Except(expectedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (missing.Length > 0 || extra.Length > 0)
                {
                    return $"{path}: object key sets differ (missing: [{string.Join(", ", missing)}], " +
                           $"extra: [{string.Join(", ", extra)}]).";
                }

                return expected.EnumerateObject()
                    .Select(property => DescribeShapeDivergence(
                        property.Value,
                        actual.GetProperty(property.Name),
                        $"{path}.{property.Name}"))
                    .FirstOrDefault(nested => nested is not null);
            case JsonValueKind.Array:
                var expectedLength = expected.GetArrayLength();
                var actualLength = actual.GetArrayLength();
                if (expectedLength != actualLength)
                    return $"{path}: expected {expectedLength} array elements, actual {actualLength}.";
                for (var index = 0; index < expectedLength; index++)
                {
                    var nested = DescribeShapeDivergence(expected[index], actual[index], $"{path}[{index}]");
                    if (nested is not null) return nested;
                }

                return null;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            default:
                return null;
        }
    }

    private static string Snippet(JsonElement value)
    {
        string text;
        try
        {
            text = value.ValueKind == JsonValueKind.String ? $"\"{value.GetString()}\"" : value.GetRawText();
        }
        catch (InvalidOperationException)
        {
            text = value.ToString();
        }

        return text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";
    }
}
