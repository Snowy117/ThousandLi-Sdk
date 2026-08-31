using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// Options for <see cref="RemoteExpertClient" />. The endpoint is the platform HTTP API base (for
/// example <c>http://localhost:5200</c>); route paths are appended to it. Credentials enter only
/// through <see cref="TokenProvider" />: the composition root (DevHost/CLI) supplies a delegate that
/// resolves the bearer token per request from its own trusted sources; the client never stores or
/// exposes the raw value.
/// </summary>
public sealed class RemoteExpertClientOptions
{
    public RemoteExpertClientOptions(string endpoint, Func<string?>? tokenProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{endpoint}' is not an absolute HTTP(S) endpoint.", nameof(endpoint));

        var builder = new UriBuilder(uri);
        if (builder.Path.Length == 0 || builder.Path.EndsWith('/'))
            BaseUri = uri;
        else
        {
            builder.Path += "/";
            BaseUri = builder.Uri;
        }

        TokenProvider = tokenProvider;
    }

    /// <summary>The absolute platform base URI with a trailing slash, so relative route paths append below it.</summary>
    public Uri BaseUri { get; }

    public Func<string?>? TokenProvider { get; }
}

/// <summary>The lifecycle status of a remote invocation, as serialized on the wire (lowercase).</summary>
public enum RemoteInvocationStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A contract entry of the platform catalog (<c>GET /abstract-experts</c>).</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RemoteExpertContract(
    string ContractId,
    string Name,
    string Description,
    JsonElement? InputSchema = null);

/// <summary>An Expert Package entry of the platform catalog (<c>GET /expert-packages</c>).</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RemoteExpertPackage(
    string ExpertPackageId,
    string DisplayName,
    IReadOnlyList<string> OpenAiModelIds,
    bool HasSettings,
    string ContractId);

/// <summary>The start request for <c>POST /expert-invocations</c>.</summary>
public sealed record RemoteInvocationStartRequest
{
    public RemoteInvocationStartRequest(
        string contractId,
        string expertPackageId,
        JsonElement input,
        string? idempotencyKey = null,
        int? timeoutSeconds = null,
        string? clientCorrelation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expertPackageId);
        if (input.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("The input JSON value is undefined.", nameof(input));
        if (timeoutSeconds is < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "TimeoutSeconds must be positive.");
        ContractId = contractId;
        ExpertPackageId = expertPackageId;
        Input = input.Clone();
        IdempotencyKey = idempotencyKey;
        TimeoutSeconds = timeoutSeconds;
        ClientCorrelation = clientCorrelation;
    }

    public string ContractId { get; }

    public string ExpertPackageId { get; }

    public JsonElement Input { get; }

    public string? IdempotencyKey { get; }

    public int? TimeoutSeconds { get; }

    public string? ClientCorrelation { get; }
}

/// <summary>The invocation status snapshot returned by POST/GET/DELETE.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RemoteInvocationSnapshot(
    string ExpertInvocationId,
    RemoteInvocationStatus Status,
    bool Replayed,
    string ContractId,
    string ExpertPackageId,
    long LastEventOrdinal,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TerminatedAt);

/// <summary>
/// The raw HTTP client for the platform remote-invocation API: the catalog, the two-step invocation
/// lifecycle, and the SSE event stream. The composition root owns the <see cref="HttpClient" />;
/// for long SSE streams it must disable the default 100-second request timeout
/// (<c>Timeout = Timeout.InfiniteTimeSpan</c>). All non-2xx responses surface as the typed
/// <see cref="RemoteExpertException" /> family with the bearer token redacted.
/// </summary>
public sealed class RemoteExpertClient(HttpClient httpClient, RemoteExpertClientOptions options)
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly RemoteExpertClientOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Lists the platform contract catalog (<c>GET /abstract-experts</c>).</summary>
    public async Task<IReadOnlyList<RemoteExpertContract>> ListContractsAsync(
        CancellationToken cancellationToken = default)
    {
        var wire = await GetJsonAsync<IReadOnlyList<WireContract>>("abstract-experts", cancellationToken)
            .ConfigureAwait(false);
        return [.. wire.Select(ToContract)];
    }

    /// <summary>Lists the usable Expert Packages (<c>GET /expert-packages?contractId=</c>), optionally filtered by contract.</summary>
    public async Task<IReadOnlyList<RemoteExpertPackage>> ListExpertPackagesAsync(
        string? contractId = null,
        CancellationToken cancellationToken = default)
    {
        var path = contractId is null
            ? "expert-packages"
            : $"expert-packages?contractId={Uri.EscapeDataString(contractId)}";
        var wire = await GetJsonAsync<IReadOnlyList<WirePackage>>(path, cancellationToken).ConfigureAwait(false);
        return [.. wire.Select(package => new RemoteExpertPackage(
            package.ExpertPackageId,
            package.DisplayName ?? string.Empty,
            package.OpenAiModelIds ?? [],
            package.HasSettings,
            package.ContractId))];
    }

    /// <summary>Starts (or idempotently replays) an invocation (<c>POST /expert-invocations</c>) and returns the status snapshot.</summary>
    public Task<RemoteInvocationSnapshot> StartInvocationAsync(
        RemoteInvocationStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wire = new WireStartRequest(
            request.ContractId,
            request.ExpertPackageId,
            CloneElement(request.Input),
            request.IdempotencyKey,
            request.TimeoutSeconds,
            request.ClientCorrelation);
        return SendJsonAsync<WireSnapshot, RemoteInvocationSnapshot>(
            HttpMethod.Post,
            "expert-invocations",
            wire,
            ToSnapshot,
            cancellationToken);
    }

    /// <summary>Fetches the invocation status snapshot (<c>GET /expert-invocations/{id}</c>).</summary>
    public Task<RemoteInvocationSnapshot> GetInvocationAsync(
        string invocationId,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<WireSnapshot, RemoteInvocationSnapshot>(
            HttpMethod.Get,
            InvocationPath(invocationId),
            requestBody: null,
            ToSnapshot,
            cancellationToken);

    /// <summary>Cancels the invocation (<c>DELETE /expert-invocations/{id}</c>) and returns the resulting snapshot.</summary>
    public Task<RemoteInvocationSnapshot> CancelInvocationAsync(
        string invocationId,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<WireSnapshot, RemoteInvocationSnapshot>(
            HttpMethod.Delete,
            InvocationPath(invocationId),
            requestBody: null,
            ToSnapshot,
            cancellationToken);

    /// <summary>
    /// Streams the invocation event frames (<c>GET /expert-invocations/{id}/events</c>). The
    /// platform replays persisted events with an ordinal greater than <paramref name="fromOrdinal" />
    /// before switching to the live channel; pass the last delivered ordinal to resume, or the
    /// default to replay everything.
    /// </summary>
    /// <param name="invocationId">The invocation identifier.</param>
    /// <param name="fromOrdinal">
    /// The exclusive lower ordinal bound; <c>-1</c> (the default) requests the full replay by
    /// omitting the <c>Last-Event-ID</c> header.
    /// </param>
    /// <param name="cancellationToken">Propagated to the request and stream reads.</param>
    public async IAsyncEnumerable<RemoteExpertStreamFrame> StreamEventsAsync(
        string invocationId,
        long fromOrdinal = -1,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);

        using var request = CreateRequest(
            HttpMethod.Get, $"{InvocationPath(invocationId)}/events", out var token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (fromOrdinal >= 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Last-Event-ID",
                fromOrdinal.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateStatusException(response, body, token);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in RemoteSemanticEventDecoder
                           .DecodeAsync(stream, cancellationToken)
                           .ConfigureAwait(false))
            yield return frame;
    }

    /// <summary>
    /// Answers a lazy <c>dataRequest</c> frame (<c>POST /expert-invocations/{id}/data/{requestId}</c>).
    /// The platform memoizes the first response per request id, so re-sends after a reconnect are
    /// safe; an unknown or expired request id surfaces through the typed 404 exception family. The
    /// response body is not meaningful to the sender and is not parsed.
    /// </summary>
    /// <param name="invocationId">The invocation identifier.</param>
    /// <param name="requestId">The dataRequest frame's request identifier.</param>
    /// <param name="body">The projected view response JSON.</param>
    /// <param name="cancellationToken">Propagated to the request.</param>
    public async Task PostInvocationDataAsync(
        string invocationId,
        string requestId,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (body.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("The data response JSON value is undefined.", nameof(body));

        using var request = CreateRequest(
            HttpMethod.Post, $"{InvocationPath(invocationId)}/data/{Uri.EscapeDataString(requestId)}", out var token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw CreateStatusException(response, responseBody, token);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path, out var token);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? Deserialize<T>(body)
            : throw CreateStatusException(response, body, token);
    }

    private async Task<TResult> SendJsonAsync<TWire, TResult>(
        HttpMethod method,
        string path,
        object? requestBody,
        Func<TWire, TResult> map,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, out var token);
        if (requestBody is not null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody, WireJson), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? map(Deserialize<TWire>(body))
            : throw CreateStatusException(response, body, token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, out string? token)
    {
        token = _options.TokenProvider?.Invoke();
        var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return request;
    }

    private static string InvocationPath(string invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        return $"expert-invocations/{Uri.EscapeDataString(invocationId)}";
    }

    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, WireJson) ??
                   throw new RemoteProtocolException("The remote endpoint returned an empty JSON payload.");
        }
        catch (JsonException exception)
        {
            throw new RemoteProtocolException(
                $"The remote endpoint returned invalid JSON: {Truncate(body)}.", exception);
        }
    }

    private static RemoteExpertContract ToContract(WireContract wire) => new(
        wire.ContractId,
        wire.Name ?? string.Empty,
        wire.Description ?? string.Empty,
        CloneOptional(wire.InputSchema));

    private static RemoteInvocationSnapshot ToSnapshot(WireSnapshot wire) => new(
        wire.ExpertInvocationId,
        wire.Status switch
        {
            "running" => RemoteInvocationStatus.Running,
            "completed" => RemoteInvocationStatus.Completed,
            "failed" => RemoteInvocationStatus.Failed,
            "cancelled" => RemoteInvocationStatus.Cancelled,
            _ => throw new RemoteProtocolException($"Unknown invocation status '{wire.Status}'.")
        },
        wire.Replayed,
        wire.ContractId,
        wire.ExpertPackageId,
        wire.LastEventOrdinal,
        ParseOutputJson(wire.Output),
        wire.ErrorCode,
        wire.ErrorMessage,
        wire.CreatedAt,
        wire.StartedAt,
        wire.TerminatedAt);

    private static JsonElement? ParseOutputJson(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        try
        {
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new RemoteProtocolException("The invocation snapshot output is not valid JSON.", exception);
        }
    }

    private static JsonElement CloneElement(JsonElement element) => element.Clone();

    private static JsonElement? CloneOptional(JsonElement? element) => element?.Clone();

    /// <summary>
    /// Maps a non-2xx response onto the typed exception family, preferring the problem+json stable
    /// code. Platforms may echo the bearer credential inside error bodies; every embedded body is
    /// redacted first, so the resolved token never surfaces through exceptions or logs.
    /// </summary>
    private static Exception CreateStatusException(HttpResponseMessage response, string body, string? token)
    {
        var redactedBody = Redact(body, token);
        var problem = TryParseProblem(redactedBody);
        if (problem is not null)
        {
            return RemoteExpertExceptionFactory.Create(
                problem.Code,
                $"The remote platform returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {Truncate(problem.Describe())}",
                (int)response.StatusCode,
                problem,
                innerException: null);
        }

        return new RemoteExpertException(
            $"The remote platform returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {Truncate(redactedBody)}",
            errorCode: null,
            (int?)response.StatusCode);
    }

    private static JsonProblemPayload? TryParseProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            var problem = JsonSerializer.Deserialize<WireProblem>(body, WireJson);
            if (problem is null)
                return null;
            return new JsonProblemPayload(
                problem.Type,
                problem.Title,
                problem.Status,
                problem.Detail,
                problem.Code,
                problem.Extensions is { ValueKind: JsonValueKind.Object } extensions
                    ? RemoteContractMismatchDetails.TryParse(extensions.Clone())
                    : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Redact(string text, string? token) =>
        string.IsNullOrWhiteSpace(token) ? text : text.Replace(token, "***", StringComparison.Ordinal);

    private static string Truncate(string text) =>
        text.Length <= 512 ? text : $"{text.AsSpan(0, 512)}…";

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record WireContract(
        string ContractId,
        string? Name,
        string? Description,
        JsonElement? InputSchema);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record WirePackage(
        string ExpertPackageId,
        string? DisplayName,
        IReadOnlyList<string>? OpenAiModelIds,
        bool HasSettings,
        string ContractId);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record WireStartRequest(
        string ContractId,
        string ExpertPackageId,
        JsonElement Input,
        string? IdempotencyKey,
        int? TimeoutSeconds,
        string? ClientCorrelation);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record WireSnapshot(
        string ExpertInvocationId,
        string Status,
        bool Replayed,
        string ContractId,
        string ExpertPackageId,
        long LastEventOrdinal,
        string? Output,
        string? ErrorCode,
        string? ErrorMessage,
        string? TerminationReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? TerminatedAt);

    private sealed record WireProblem(
        string? Type,
        string? Title,
        int? Status,
        string? Detail,
        string? Code,
        JsonElement? Extensions);
}
