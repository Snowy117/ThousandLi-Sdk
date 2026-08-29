using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Options for <see cref="OpenAiCompatibleBasicAi"/>. Credentials enter only through
/// <see cref="ApiKeyProvider"/>: the composition root (DevHost/CLI) supplies a delegate that reads
/// the key from its own trusted sources; the adapter resolves it per request, applies the bearer
/// header, and neither stores nor exposes the raw value.
/// </summary>
public sealed class OpenAiCompatibleBasicAiOptions
{
    public OpenAiCompatibleBasicAiOptions(string endpoint, IReadOnlyList<string> models, Func<string?>? apiKeyProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{endpoint}' is not an absolute HTTP(S) endpoint.", nameof(endpoint));
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
            throw new ArgumentException("At least one model must be configured.", nameof(models));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model is null)
                throw new ArgumentException("Models cannot contain null entries.", nameof(models));
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            if (!seen.Add(model))
                throw new ArgumentException($"Model '{model}' is configured more than once.", nameof(models));
        }

        Endpoint = endpoint;
        Models = new ReadOnlyCollection<string>([.. models]);
        ApiKeyProvider = apiKeyProvider;
    }

    /// <summary>The absolute HTTP(S) chat-completions URL of the OpenAI-compatible gateway.</summary>
    public string Endpoint { get; }

    public IReadOnlyList<string> Models { get; }

    public Func<string?>? ApiKeyProvider { get; }
}

/// <summary>
/// The concrete OpenAI-compatible local gateway adapter. The composition root owns the HttpClient,
/// the endpoint, the model list, and the API-key delegate; experts see only
/// <see cref="ILocalBasicAi"/>. SSE framing follows the chat-completions streaming protocol:
/// <c>data:</c> JSON frames with <c>choices[0].delta.content</c>, terminated by <c>[DONE]</c>.
/// </summary>
public sealed class OpenAiCompatibleBasicAi(HttpClient httpClient, OpenAiCompatibleBasicAiOptions options)
    : ILocalBasicAi, IRuntimeBasicAi
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly OpenAiCompatibleBasicAiOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public IReadOnlyList<string> AvailableModels => _options.Models;

    IReadOnlyList<BasicAiModelDescriptor> IRuntimeBasicAi.AvailableModels =>
        [.. _options.Models.Select(model => new BasicAiModelDescriptor(model))];

    public async Task<LocalBasicAiCompletionResult> CompleteAsync(
        LocalBasicAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfiguredModel(request.ModelId);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateHttpRequest(request, stream: false, out var apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccessStatus(response, body, request.ModelId, apiKey);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new LocalBasicAiException(
                $"The gateway response for model '{request.ModelId}' is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            var content = ExtractMessageContent(document.RootElement, request.ModelId);
            if (request.ResponseMode == LocalBasicAiResponseMode.Json &&
                !content.TrimStart().StartsWith('{') &&
                !content.TrimStart().StartsWith('['))
            {
                throw new LocalBasicAiException(
                    $"The completion for model '{request.ModelId}' does not start with a JSON object or array root.");
            }

            return new LocalBasicAiCompletionResult(content);
        }
    }

    public async Task<BasicAiCompletionResult> CompleteAsync(
        BasicAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfiguredModel(request.ModelId);
        EnsureToolsSupported(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateRuntimeHttpRequest(request, stream: false, out var apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccessStatus(response, body, request.ModelId, apiKey);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new LocalBasicAiException(
                $"The gateway response for model '{request.ModelId}' is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            var content = ExtractMessageContent(document.RootElement, request.ModelId);
            var reasoning = ExtractMessageReasoning(document.RootElement);
            JsonDocument contentDocument;
            try
            {
                contentDocument = JsonDocument.Parse(content);
            }
            catch (JsonException exception)
            {
                throw new LocalBasicAiException(
                    $"The completion for model '{request.ModelId}' is not valid JSON.",
                    innerException: exception);
            }

            using (contentDocument)
            {
                if (contentDocument.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    throw new LocalBasicAiException(
                        $"The completion for model '{request.ModelId}' does not contain a JSON object or array root.");
                }

                return new BasicAiCompletionResult(contentDocument.RootElement.Clone(), reasoning);
            }
        }
    }

    public async IAsyncEnumerable<ExpertTextDeltaEvent> StreamTextAsync(
        LocalBasicAiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResponseMode != LocalBasicAiResponseMode.Text)
            throw new ArgumentException("StreamTextAsync requires a Text response mode request. Use StreamJsonAsync for Json mode.", nameof(request));
        EnsureConfiguredModel(request.ModelId);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateHttpRequest(request, stream: true, out var apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateStatusException(response, body, request.ModelId, apiKey);
        }

        await foreach (var frame in ReadContentFramesAsync(response.Content, request.ModelId, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (frame.Content is { } delta)
                yield return new ExpertTextDeltaEvent(delta);
        }
    }

    public async IAsyncEnumerable<JsonStreamEvent> StreamJsonAsync(
        LocalBasicAiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResponseMode != LocalBasicAiResponseMode.Json)
            throw new ArgumentException("StreamJsonAsync requires a Json response mode request. Use StreamTextAsync for Text mode.", nameof(request));
        EnsureConfiguredModel(request.ModelId);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateHttpRequest(request, stream: true, out var apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateStatusException(response, body, request.ModelId, apiKey);
        }

        var parser = new JsonStreamParser();
        await foreach (var frame in ReadContentFramesAsync(response.Content, request.ModelId, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (frame.Content is not { } content)
                continue;
            foreach (var streamEvent in parser.Feed(content))
                yield return streamEvent;
        }

        foreach (var streamEvent in parser.Complete())
            yield return streamEvent;
    }

    public async IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
        BasicAiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfiguredModel(request.ModelId);
        EnsureToolsSupported(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateRuntimeHttpRequest(request, stream: true, out var apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateStatusException(response, body, request.ModelId, apiKey);
        }

        var parser = new JsonStreamParser();
        await foreach (var frame in ReadContentFramesAsync(response.Content, request.ModelId, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (frame.Reasoning is { } reasoning)
                yield return new BasicAiReasoningStreamEvent(reasoning);
            if (frame.Content is not { } content)
                continue;
            foreach (var streamEvent in parser.Feed(content))
                yield return new BasicAiJsonStreamEvent(streamEvent);
        }

        foreach (var streamEvent in parser.Complete())
            yield return new BasicAiJsonStreamEvent(streamEvent);
    }

    private HttpRequestMessage CreateHttpRequest(LocalBasicAiRequest request, bool stream, out string? apiKey)
    {
        apiKey = _options.ApiKeyProvider?.Invoke();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        var payload = new
        {
            model = request.ModelId,
            stream,
            messages = request.Messages.Select(message => new { role = message.Role, content = message.Content })
        };
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return httpRequest;
    }

    private HttpRequestMessage CreateRuntimeHttpRequest(BasicAiRequest request, bool stream, out string? apiKey)
    {
        apiKey = _options.ApiKeyProvider?.Invoke();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["stream"] = stream,
            ["messages"] = request.Messages.Select(message => new Dictionary<string, object?>
            {
                ["role"] = ToWireRole(message.Role),
                ["content"] = message.Content
            }),
            ["response_format"] = new Dictionary<string, object?> { ["type"] = "json_object" }
        };
        if (request.Sampling?.Temperature is { } temperature)
            payload["temperature"] = temperature;
        if (request.Sampling?.TopP is { } topP)
            payload["top_p"] = topP;
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return httpRequest;
    }

    private static string ToWireRole(BasicAiMessageRole role) => role switch
    {
        BasicAiMessageRole.System => "system",
        BasicAiMessageRole.User => "user",
        BasicAiMessageRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.")
    };

    private readonly record struct ContentFrame(string? Content, string? Reasoning);

    private static async IAsyncEnumerable<ContentFrame> ReadContentFramesAsync(
        HttpContent content,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || line.StartsWith(':'))
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                yield break;
            if (ExtractDeltaFrame(payload, modelId) is { } frame)
                yield return frame;
        }
    }

    private static ContentFrame? ExtractDeltaFrame(string payload, string modelId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new LocalBasicAiException(
                $"A streamed frame for model '{modelId}' is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices))
                throw new LocalBasicAiException($"A streamed frame for model '{modelId}' does not contain 'choices'.");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new LocalBasicAiException($"A streamed frame for model '{modelId}' contains no choices.");
            var firstChoice = choices[0];
            if (firstChoice.ValueKind != JsonValueKind.Object ||
                !firstChoice.TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ContentFrame(
                NonEmpty(ReadStringProperty(delta, "content")),
                NonEmpty(ReadStringProperty(delta, "reasoning_content")));
        }
    }

    private static string? ReadStringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ExtractMessageContent(JsonElement root, string modelId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices))
            throw new LocalBasicAiException($"The completion response for model '{modelId}' does not contain 'choices'.");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new LocalBasicAiException($"The completion response for model '{modelId}' contains no choices.");
        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object ||
            !firstChoice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            throw new LocalBasicAiException(
                $"The completion response for model '{modelId}' does not contain choices[0].message.content.");
        }

        return content.GetString() ?? string.Empty;
    }

    private static string? ExtractMessageReasoning(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices))
            return null;
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;
        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object ||
            !firstChoice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            return null;

        return NonEmpty(ReadStringProperty(message, "reasoning_content"));
    }

    private static void EnsureToolsSupported(BasicAiRequest request)
    {
        if (request.Tools.Count > 0)
        {
            throw new NotSupportedException(
                "The OpenAI-compatible local gateway adapter does not forward tool declarations; tools are reserved for platform-hosted bridges.");
        }
    }

    private static void EnsureSuccessStatus(HttpResponseMessage response, string body, string modelId, string? apiKey)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw CreateStatusException(response, body, modelId, apiKey);
    }

    private static LocalBasicAiException CreateStatusException(
        HttpResponseMessage response,
        string body,
        string modelId,
        string? apiKey) =>
        new(
            $"The gateway returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for model '{modelId}': {Truncate(Redact(body, apiKey))}",
            (int?)response.StatusCode);

    /// <summary>
    /// Gateways may echo the bearer credential inside error bodies (for example
    /// "Incorrect API key provided: sk-…"). The resolved key must never surface through exceptions
    /// that propagate into expert package code, so every embedded body is redacted first.
    /// </summary>
    private static string Redact(string text, string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? text : text.Replace(apiKey, "***", StringComparison.Ordinal);

    private static string Truncate(string text) =>
        text.Length <= 512 ? text : $"{text.AsSpan(0, 512)}…";

    private void EnsureConfiguredModel(string modelId)
    {
        if (!_options.Models.Contains(modelId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' is not configured on the local gateway. Available models: " +
                string.Join(", ", _options.Models.Select(model => $"'{model}'")) + ".");
        }
    }
}
