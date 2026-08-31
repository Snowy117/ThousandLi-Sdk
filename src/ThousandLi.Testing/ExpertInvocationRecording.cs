using System.Collections.ObjectModel;
using System.Text.Json;
using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// A recording file could not be loaded or saved: unsupported format version, malformed data, or
/// an underlying storage failure. The message always includes actionable reset guidance.
/// </summary>
public sealed class ExpertRecordingException : InvalidOperationException
{
    public ExpertRecordingException(string message)
        : base(message)
    {
    }

    public ExpertRecordingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The terminal outcome captured in a semantic recording.</summary>
public sealed record ExpertRecordingTerminal
{
    public const string Committed = "committed";
    public const string Aborted = "aborted";
    public const string ErrorStatus = "error";

    public ExpertRecordingTerminal(string status, JsonElement? output = null, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (status is not (Committed or Aborted or ErrorStatus))
            throw new ArgumentException(
                $"Terminal status must be '{Committed}', '{Aborted}', or '{ErrorStatus}', but was '{status}'.",
                nameof(status));
        var hasOutput = output is { } value && value.ValueKind != JsonValueKind.Undefined;
        if (status == Committed)
        {
            if (!hasOutput)
                throw new ArgumentException($"A '{Committed}' terminal requires a JSON output.", nameof(output));
            if (!string.IsNullOrWhiteSpace(error))
                throw new ArgumentException($"A '{Committed}' terminal cannot carry an error message.", nameof(error));
        }
        else if (hasOutput)
        {
            throw new ArgumentException($"Only a '{Committed}' terminal carries an output.", nameof(output));
        }

        Status = status;
        Output = hasOutput ? output!.Value.Clone() : null;
        Error = error;
    }

    public string Status { get; }

    /// <summary>The invocation output; present only for a <see cref="Committed"/> terminal.</summary>
    public JsonElement? Output { get; }

    /// <summary>The failure summary; error message text is diagnostic only and never compared on replay.</summary>
    public string? Error { get; }
}

/// <summary>
/// One semantic expert invocation recording: the contract id, the structured input, the ordered
/// semantic event stream, the terminal result, and deterministic correlation metadata.
///
/// Recordings live at the expert invocation seam (contract-level expert behavior). They are
/// deliberately separate from <c>RecordedBasicAi</c> (ThousandLi.ExpertAuthoring), which replays
/// model-level gateway interactions below the expert seam: one expert invocation maps to zero or
/// many model calls, so the two formats have no one-to-one correspondence. Both share the same
/// replay conventions: deterministic consumption order, fail-fast exhaustion, and no silent
/// partial loading.
///
/// The JSON format is SDK-internal development data, not a public archive contract: the format
/// major is versioned, and loading an incompatible file fails with explicit reset guidance
/// instead of degrading silently. Recordings written by the pre-contract-id format (embedded
/// version/fingerprint descriptors) are rejected as a different SDK generation.
/// </summary>
public sealed record ExpertInvocationRecording
{
    public const int CurrentFormatMajor = 2;

    private static readonly JsonSerializerOptions SJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ExpertInvocationRecording(
        string contractId,
        JsonElement input,
        IReadOnlyList<ExpertSemanticEvent> events,
        ExpertRecordingTerminal terminal,
        string? scenarioId = null,
        string? channelKey = null,
        string? invocationId = null,
        string? executor = null,
        string? expertPackageId = null,
        DateTimeOffset recordedAtUtc = default,
        long durationMs = 0,
        int formatMajor = CurrentFormatMajor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        JsonContractGuardForTesting.ThrowIfUndefined(input, nameof(input));
        ArgumentNullException.ThrowIfNull(events);
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index] is null)
                throw new ArgumentException($"Events cannot contain null entries at index {index}.", nameof(events));
        }

        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        if (formatMajor != CurrentFormatMajor)
            throw new ArgumentException(
                $"Recording format major {formatMajor} is not supported; this SDK supports {CurrentFormatMajor}.",
                nameof(formatMajor));
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), durationMs, "Duration cannot be negative.");

        ContractId = contractId;
        Input = input.Clone();
        Events = new ReadOnlyCollection<ExpertSemanticEvent>([.. events]);
        ScenarioId = scenarioId;
        ChannelKey = channelKey;
        InvocationId = invocationId;
        Executor = executor;
        ExpertPackageId = expertPackageId;
        RecordedAtUtc = recordedAtUtc;
        DurationMs = durationMs;
        FormatMajor = formatMajor;
    }

    public string ContractId { get; }

    public JsonElement Input { get; }

    public IReadOnlyList<ExpertSemanticEvent> Events { get; }

    public ExpertRecordingTerminal Terminal { get; }

    /// <summary>The Fake executor query key, when the recording came from a Fake invocation.</summary>
    public string? ScenarioId { get; }

    /// <summary>The playground correlation key assigned to the recorded invocation.</summary>
    public string? ChannelKey { get; }

    public string? InvocationId { get; }

    /// <summary>The executor that produced the recording ('fake', 'local', or 'remote').</summary>
    public string? Executor { get; }

    public string? ExpertPackageId { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public long DurationMs { get; }

    public int FormatMajor { get; }

    /// <summary>Serializes the recording to indented camelCase JSON in the current format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SJsonOptions);

    /// <summary>
    /// Parses a recording document. Unsupported format majors and malformed documents throw
    /// <see cref="ExpertRecordingException"/> with reset guidance; callers that know the file path
    /// should wrap the message with the path for better diagnostics.
    /// </summary>
    public static ExpertInvocationRecording FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RecordingDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RecordingDocument>(json, SJsonOptions)
                ?? throw new ExpertRecordingException("The recording document was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExpertRecordingException(
                "The recording document is not valid JSON: " + exception.Message
                + " Delete the recording file and record a new one.", exception);
        }

        if (document.FormatMajor is not { } formatMajor)
            throw new ExpertRecordingException("The recording document is missing 'formatMajor'.");
        if (formatMajor != CurrentFormatMajor)
            throw new ExpertRecordingException(
                $"The recording uses format major {formatMajor}, but this SDK supports {CurrentFormatMajor}. "
                + "The recording was written by a different SDK generation; delete it and record a new one.");
        if (document.Input is not { } input || input.ValueKind == JsonValueKind.Undefined)
            throw new ExpertRecordingException("The recording document is missing 'input'.");
        if (document.Terminal is null)
            throw new ExpertRecordingException("The recording document is missing 'terminal'.");

        var events = new List<ExpertSemanticEvent>();
        foreach (var semanticEvent in document.Events ?? [])
        {
            if (semanticEvent is null)
                throw new ExpertRecordingException("The recording document contains a null event entry.");
            if (semanticEvent.Payload is not { } payload || payload.ValueKind == JsonValueKind.Undefined)
                throw new ExpertRecordingException("A recorded event is missing 'payload'.");
            events.Add(new ExpertSemanticEvent(
                RequireString(semanticEvent.EventType, "events[].eventType"), payload));
        }

        try
        {
            var terminal = new ExpertRecordingTerminal(
                RequireString(document.Terminal.Status, "terminal.status"),
                document.Terminal.Output,
                document.Terminal.Error);
            return new ExpertInvocationRecording(
                RequireString(document.ContractId, "contractId"),
                input,
                events,
                terminal,
                document.ScenarioId,
                document.ChannelKey,
                document.InvocationId,
                document.Executor,
                document.ExpertPackageId,
                document.RecordedAtUtc ?? DateTimeOffset.UtcNow,
                document.DurationMs ?? 0,
                formatMajor);
        }
        catch (ArgumentException exception)
        {
            throw new ExpertRecordingException("The recording document is invalid: " + exception.Message, exception);
        }
    }

    private static string RequireString(string? value, string location) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ExpertRecordingException($"The recording document has a blank or missing '{location}'.")
            : value;

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class RecordingDocument
    {
        public int? FormatMajor { get; init; }

        public string? ContractId { get; init; }

        public JsonElement? Input { get; init; }

        public List<SemanticEventDocument?>? Events { get; init; }

        public TerminalDocument? Terminal { get; init; }

        public string? ScenarioId { get; init; }

        public string? ChannelKey { get; init; }

        public string? InvocationId { get; init; }

        public string? Executor { get; init; }

        public string? ExpertPackageId { get; init; }

        public DateTimeOffset? RecordedAtUtc { get; init; }

        public long? DurationMs { get; init; }
    }

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class SemanticEventDocument
    {
        public string? EventType { get; init; }

        public JsonElement? Payload { get; init; }
    }

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class TerminalDocument
    {
        public string? Status { get; init; }

        public JsonElement? Output { get; init; }

        public string? Error { get; init; }
    }
}
