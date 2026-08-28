using System.Text.Json;
using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The schema-driven BasicAi capability used by expert execution flows. Callers supply messages,
/// a target schema, and an explicit model id per call; streams yield structured JSON events plus
/// optional reasoning deltas, and completions return the final JSON value plus optional reasoning.
/// Implementations are supplied by the composition root (local gateway adapter, deterministic
/// recordings, or a platform-hosted bridge); expert code never sees credentials through this surface.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IRuntimeBasicAi
{
    /// <summary>The models this execution can select from. Requests must name one explicitly.</summary>
    IReadOnlyList<BasicAiModelDescriptor> AvailableModels { get; }

    /// <summary>Streams the structured completion of a request as JSON stream events with optional reasoning deltas.</summary>
    IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
        BasicAiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Completes a request non-streamingly and returns the final structured JSON output.</summary>
    Task<BasicAiCompletionResult> CompleteAsync(
        BasicAiRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>The conversation role of a <see cref="BasicAiMessage"/>.</summary>
public enum BasicAiMessageRole
{
    /// <summary>System instruction.</summary>
    System = 0,

    /// <summary>User input.</summary>
    User = 1,

    /// <summary>Historical assistant turn.</summary>
    Assistant = 2
}

/// <summary>A conversation message of a <see cref="BasicAiRequest"/>.</summary>
public sealed record BasicAiMessage
{
    public BasicAiMessage(BasicAiMessageRole role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Role = role;
        Content = content;
    }

    /// <summary>The message role.</summary>
    public BasicAiMessageRole Role { get; }

    /// <summary>The message text content.</summary>
    public string Content { get; }

    /// <summary>Creates a system message.</summary>
    public static BasicAiMessage System(string content) => new(BasicAiMessageRole.System, content);

    /// <summary>Creates a user message.</summary>
    public static BasicAiMessage User(string content) => new(BasicAiMessageRole.User, content);

    /// <summary>Creates a historical assistant message.</summary>
    public static BasicAiMessage Assistant(string content) => new(BasicAiMessageRole.Assistant, content);
}

/// <summary>A tool declaration skeleton. The full tool-call loop is owned by later slices.</summary>
public sealed record BasicAiToolDescriptor
{
    public BasicAiToolDescriptor(string name, string description, AiObjectSchema parametersSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Name = name;
        Description = description;
        ParametersSchema = parametersSchema ?? throw new ArgumentNullException(nameof(parametersSchema));
    }

    /// <summary>The tool name.</summary>
    public string Name { get; }

    /// <summary>The tool description.</summary>
    public string Description { get; }

    /// <summary>The tool parameter object schema.</summary>
    public AiObjectSchema ParametersSchema { get; }
}

/// <summary>The description of one model this execution can select from.</summary>
public sealed record BasicAiModelDescriptor
{
    public BasicAiModelDescriptor(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ModelId = modelId;
    }

    /// <summary>The model identifier, matching the composition root's configured model ids.</summary>
    public string ModelId { get; }
}

/// <summary>Optional sampling parameters of a <see cref="BasicAiRequest"/>. Unset values defer to provider defaults.</summary>
public sealed record BasicAiSamplingParameters
{
    /// <summary>Sampling temperature; unset defers to the provider default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Nucleus sampling probability; unset defers to the provider default.</summary>
    public float? TopP { get; init; }
}

/// <summary>
/// One schema-driven BasicAi call. It describes only the model input/output boundary; prompt
/// engineering semantics belong to the expert that assembles it.
/// </summary>
public sealed record BasicAiRequest
{
    public BasicAiRequest(
        string modelId,
        IReadOnlyList<BasicAiMessage> messages,
        AiJsonSchema targetSchema,
        IReadOnlyList<BasicAiToolDescriptor>? tools = null,
        BasicAiSamplingParameters? sampling = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            throw new ArgumentException("A BasicAi request must contain at least one message.", nameof(messages));

        ArgumentNullException.ThrowIfNull(targetSchema);
        ModelId = modelId;
        Messages = CopyMessages(messages);
        TargetSchema = targetSchema;
        Tools = CopyTools(tools);
        Sampling = sampling;
    }

    /// <summary>The model id explicitly selected for this call; there is no session-level default model.</summary>
    public string ModelId { get; }

    /// <summary>The caller-provided model messages.</summary>
    public IReadOnlyList<BasicAiMessage> Messages { get; }

    /// <summary>The caller-declared target output schema.</summary>
    [UsedImplicitly]
    public AiJsonSchema TargetSchema { get; }

    /// <summary>The caller-declared tool declarations; the full tool-call loop is owned by later slices.</summary>
    public IReadOnlyList<BasicAiToolDescriptor> Tools { get; }

    /// <summary>Optional sampling parameters; unset values defer to provider defaults.</summary>
    public BasicAiSamplingParameters? Sampling { get; }

    private static List<BasicAiToolDescriptor> CopyTools(IReadOnlyList<BasicAiToolDescriptor>? tools)
    {
        if (tools is null || tools.Count == 0)
            return [];

        var copied = new List<BasicAiToolDescriptor>(tools.Count);
        foreach (var tool in tools)
        {
            copied.Add(tool ?? throw new ArgumentException(
                "BasicAi tool descriptors cannot contain null entries.",
                nameof(tools)));
        }

        return copied;
    }

    private static List<BasicAiMessage> CopyMessages(IReadOnlyList<BasicAiMessage> messages)
    {
        var copied = new List<BasicAiMessage>(messages.Count);
        foreach (var message in messages)
        {
            copied.Add(message ?? throw new ArgumentException(
                "BasicAi request messages cannot contain null entries.",
                nameof(messages)));
        }

        return copied;
    }
}

/// <summary>The result of one non-streaming BasicAi completion.</summary>
public sealed record BasicAiCompletionResult
{
    public BasicAiCompletionResult(JsonElement json, string? reasoning = null)
    {
        if (json.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("BasicAi completion result JSON cannot be undefined.", nameof(json));
        if (reasoning is not null && string.IsNullOrWhiteSpace(reasoning))
            throw new ArgumentException("Reasoning content cannot be empty or whitespace.", nameof(reasoning));

        Json = json.Clone();
        Reasoning = reasoning;
    }

    /// <summary>The final structured JSON output.</summary>
    public JsonElement Json { get; }

    /// <summary>Optional provider reasoning text; null when the provider produced none.</summary>
    public string? Reasoning { get; }
}

/// <summary>One normalized event of a streaming BasicAi call.</summary>
public abstract record BasicAiStreamEvent;

/// <summary>A JSON stream event parsed from the structured completion content.</summary>
public sealed record BasicAiJsonStreamEvent(JsonStreamEvent Event) : BasicAiStreamEvent;

/// <summary>An incremental provider reasoning delta. Whitespace-only deltas are valid reasoning content.</summary>
public sealed record BasicAiReasoningStreamEvent : BasicAiStreamEvent
{
    public BasicAiReasoningStreamEvent(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
            throw new ArgumentException("Reasoning deltas cannot be empty.", nameof(delta));
        Delta = delta;
    }

    /// <summary>The reasoning delta fragment.</summary>
    public string Delta { get; }
}
