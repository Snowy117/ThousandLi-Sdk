using System.Collections.ObjectModel;
using JetBrains.Annotations;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The local BasicAi capability surface. Implementations are supplied by the composition root and
/// expose neither credentials nor transport configuration. Both methods are published
/// authoring-surface members even where current in-repo flows exercise only one of them directly.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface ILocalBasicAi
{
    /// <summary>The model ids configured on the local gateway. Experts must not hardcode models.</summary>
    IReadOnlyList<string> AvailableModels { get; }

    /// <summary>Performs a non-streaming completion and returns the final text content.</summary>
    Task<LocalBasicAiCompletionResult> CompleteAsync(
        LocalBasicAiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a streaming completion. Yields <see cref="ExpertTextDeltaEvent"/> values in Text
    /// response mode and <see cref="ExpertJsonStreamEvent"/> values in Json response mode.
    /// </summary>
    IAsyncEnumerable<ExpertStreamEvent> StreamAsync(
        LocalBasicAiRequest request,
        CancellationToken cancellationToken = default);
}

public enum LocalBasicAiResponseMode
{
    /// <summary>The completion content is treated as plain narrative text and streamed as text deltas.</summary>
    Text = 0,

    /// <summary>The completion content is treated as a JSON value and streamed as JSON stream events.</summary>
    Json = 1
}

public sealed record LocalBasicAiMessage
{
    public LocalBasicAiMessage(string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);
        Role = role;
        Content = content;
    }

    public string Role { get; }

    /// <summary>Free-form content. May be empty or whitespace-only; empty assistant turns are
    /// legitimate gateway payloads.</summary>
    public string Content { get; }
}

public sealed record LocalBasicAiRequest
{
    public LocalBasicAiRequest(
        string modelId,
        IReadOnlyList<LocalBasicAiMessage> messages,
        LocalBasicAiResponseMode responseMode = LocalBasicAiResponseMode.Text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));
        if (messages.Any(message => (object?)message is null))
            throw new ArgumentException("Messages cannot contain null entries.", nameof(messages));
        if (responseMode is not (LocalBasicAiResponseMode.Text or LocalBasicAiResponseMode.Json))
            throw new ArgumentOutOfRangeException(nameof(responseMode), responseMode, "Unknown response mode.");

        ModelId = modelId;
        Messages = new ReadOnlyCollection<LocalBasicAiMessage>([.. messages]);
        ResponseMode = responseMode;
    }

    public string ModelId { get; }

    public IReadOnlyList<LocalBasicAiMessage> Messages { get; }

    public LocalBasicAiResponseMode ResponseMode { get; }
}

public sealed record LocalBasicAiCompletionResult
{
    public LocalBasicAiCompletionResult(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The final assistant content. May be empty; never null.</summary>
    public string Text { get; }
}

/// <summary>
/// Transport or protocol failure of a local gateway call (HTTP error status, invalid response
/// envelope, malformed SSE framing). Malformed model JSON inside a Json-mode stream surfaces as
/// <see cref="ExpertJsonStreamException"/> instead.
/// </summary>
public sealed class LocalBasicAiException(string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? StatusCode { get; } = statusCode;
}
