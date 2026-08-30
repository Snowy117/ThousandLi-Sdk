using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// A queue-driven <see cref="HttpMessageHandler" /> for the remote-expert wire tests: each request
/// captures its method/URI/headers/body and pops the next canned response (or fails when the queue
/// is empty, which proves no unexpected traffic).
/// </summary>
internal sealed class FakeRemoteHttpHandler : HttpMessageHandler
{
    private readonly Queue<object> _responses = [];

    private List<CapturedRemoteRequest> Requests { get; } = [];

    public IReadOnlyList<CapturedRemoteRequest> CapturedRequests => new ReadOnlyCollection<CapturedRemoteRequest>(Requests);

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    /// <summary>Fails the next request with a transport-level exception (after capturing the request).</summary>
    public void EnqueueFailure(Exception exception) => _responses.Enqueue(exception);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    public void EnqueueProblem(string json, HttpStatusCode status) =>
        Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/problem+json")
        });

    public void EnqueueSse(string sseText, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(sseText, Encoding.UTF8, "text/event-stream")
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRemoteRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Last-Event-ID", out var lastEventId) ? lastEventId.FirstOrDefault() : null,
            body));
        if (_responses.Count == 0)
            throw new InvalidOperationException("The fake platform received an unexpected request.");
        var next = _responses.Dequeue();
        return next is Exception exception ? throw exception : (HttpResponseMessage)next;
    }
}

internal sealed record CapturedRemoteRequest(
    HttpMethod Method,
    string Uri,
    string? Authorization,
    string? LastEventId,
    string? Body);
