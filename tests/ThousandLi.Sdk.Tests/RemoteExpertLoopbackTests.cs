using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Full-host loopback tests: a minimal TCP HTTP server plays the platform (catalog, 202 snapshot,
/// SSE event stream, DELETE) so the whole <see cref="RemoteInvocationRunner" /> lifecycle — including
/// mid-stream disconnections and cancellation propagation — runs over real sockets.
/// </summary>
public sealed class RemoteExpertLoopbackTests
{
    private const string ContractId = "tests/story";
    private const string PackageId = "tests/story-pro@1";
    private const string InvocationId = "0199def0-aaaa-7bbb-8ccc-dddd00001111";
    private const string Token = "loopback-token";

    [Fact]
    public async Task ExecuteAsync_CompletesOverLoopbackWithRealSockets()
    {
        using var platform = LoopbackFakePlatform.Start(request => request switch
        {
            { Method: "GET", Path: "/abstract-experts" } => LoopbackResponse.Json(200, CatalogJson),
            { Method: "POST", Path: "/expert-invocations" } => LoopbackResponse.Json(202, SnapshotJson),
            { Method: "GET", Path: "/expert-invocations/" + InvocationId + "/events" } =>
                LoopbackResponse.Sse([
                    SseFrames.Event(0, "a"),
                    SseFrames.Event(1, "b"),
                    SseFrames.Completed("""{"done":true}""")
                ]),
            _ => throw new InvalidOperationException($"Unexpected loopback request {request.Method} {request.Path}.")
        });

        var runner = CreateRunner(platform);
        var sink = new RecordingSemanticSink();

        var result = await runner.ExecuteAsync(
            new ExpertInvocationRequest(
                ContractId,
                scenarioId: "loopback-scenario",
                input: TestSupport.Json("""{"prompt":"hello"}"""),
                channelKey: "loopback-channel"),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(InvocationId, result.InvocationId);
        Assert.True(result.Output.GetProperty("done").GetBoolean());
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("a", sink.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("b", sink.Events[1].Payload.GetProperty("text").GetString());

        var start = platform.Requests.Single(request => request.Method == "POST");
        Assert.Equal($"Bearer {Token}", start.Headers["authorization"]);
        using var body = JsonDocument.Parse(start.Body);
        Assert.Equal("loopback-channel", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("loopback-scenario", body.RootElement.GetProperty("clientCorrelation").GetString());
        Assert.Equal(PackageId, body.RootElement.GetProperty("expertPackageId").GetString());

        var eventsRequest = platform.Requests.Single(request => request.Path.EndsWith("/events", StringComparison.Ordinal));
        Assert.False(eventsRequest.Headers.ContainsKey("last-event-id"));
    }

    [Fact]
    public async Task ExecuteAsync_ResumesAcrossMidStreamDisconnectsWithLastEventId()
    {
        var eventsCalls = 0;
        using var platform = LoopbackFakePlatform.Start(request =>
        {
            switch (request)
            {
                case { Method: "GET", Path: "/abstract-experts" }:
                    return LoopbackResponse.Json(200, CatalogJson);
                case { Method: "POST", Path: "/expert-invocations" }:
                    return LoopbackResponse.Json(202, SnapshotJson);
                case { Method: "GET", Path: "/expert-invocations/" + InvocationId + "/events" }:
                    eventsCalls++;
                    if (eventsCalls == 1)
                        // Half of the story, then the connection dies without a terminal frame.
                        return LoopbackResponse.Sse([SseFrames.Event(0, "a"), SseFrames.Event(1, "b")], dropConnection: true);
                    return LoopbackResponse.Sse([
                        SseFrames.Event(1, "b"),
                        SseFrames.Event(2, "c"),
                        SseFrames.Completed("""{"done":true}""")
                    ]);
                default:
                    throw new InvalidOperationException($"Unexpected loopback request {request.Method} {request.Path}.");
            }
        });

        var runner = CreateRunner(platform);
        var sink = new RecordingSemanticSink();

        var result = await runner.ExecuteAsync(
            new ExpertInvocationRequest(
                ContractId,
                scenarioId: null,
                input: TestSupport.Json("""{"prompt":"hello"}"""),
                channelKey: null),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(2, eventsCalls);
        var resumed = platform.Requests
            .Where(request => request.Path.EndsWith("/events", StringComparison.Ordinal))
            .ToList();
        _ = resumed[0].Headers.TryGetValue("last-event-id", out var firstCursor);
        _ = resumed[1].Headers.TryGetValue("last-event-id", out var resumeCursor);
        Assert.Null(firstCursor);
        Assert.Equal("1", resumeCursor);

        // The reconnect replayed ordinal 1, but the sink must observe each event exactly once.
        Assert.Equal(3, sink.Events.Count);
        Assert.Equal("a", sink.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("b", sink.Events[1].Payload.GetProperty("text").GetString());
        Assert.Equal("c", sink.Events[2].Payload.GetProperty("text").GetString());
        Assert.True(result.Output.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesLocalCancellationAsDelete()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var platform = LoopbackFakePlatform.Start(request =>
        {
            switch (request)
            {
                case { Method: "GET", Path: "/abstract-experts" }:
                    return LoopbackResponse.Json(200, CatalogJson);
                case { Method: "POST", Path: "/expert-invocations" }:
                    return LoopbackResponse.Json(202, SnapshotJson);
                case { Method: "GET", Path: "/expert-invocations/" + InvocationId + "/events" }:
                    return LoopbackResponse.HangingSse([SseFrames.Event(0, "a")], hang.Task);
                case { Method: "DELETE", Path: "/expert-invocations/" + InvocationId }:
                    _ = deleteSeen.TrySetResult();
                    _ = hang.TrySetResult();
                    return LoopbackResponse.Json(200, CancelledSnapshotJson);
                default:
                    throw new InvalidOperationException($"Unexpected loopback request {request.Method} {request.Path}.");
            }
        });

        var runner = CreateRunner(platform);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestSupport.CancellationToken);
        var sink = new CancellingSink(cts);

        _ = await Assert.ThrowsAsync<RemoteInvocationCancelledException>(() =>
            runner.ExecuteAsync(
                new ExpertInvocationRequest(
                    ContractId,
                    scenarioId: null,
                    input: TestSupport.Json("""{"prompt":"hello"}"""),
                    channelKey: null),
                sink,
                cts.Token).AsTask());

        Assert.True(await Task.WhenAny(
            deleteSeen.Task,
            Task.Delay(TimeSpan.FromSeconds(10), TestSupport.CancellationToken)) == deleteSeen.Task);
        Assert.Single(sink.Events);
    }

    private static string CatalogJson =>
        "[{\"contractId\":\"" + ContractId + "\",\"name\":\"LongTextWriting\",\"description\":\"\"}]";

    private static string SnapshotJson =>
        "{\"expertInvocationId\":\"" + InvocationId + "\",\"status\":\"running\",\"replayed\":false,\"contractId\":\"" +
        ContractId + "\",\"expertPackageId\":\"" + PackageId +
        "\",\"lastEventOrdinal\":-1,\"createdAt\":\"2026-08-26T10:00:00Z\"}";

    private static string CancelledSnapshotJson =>
        "{\"expertInvocationId\":\"" + InvocationId + "\",\"status\":\"cancelled\",\"replayed\":false,\"contractId\":\"" +
        ContractId + "\",\"expertPackageId\":\"" + PackageId +
        "\",\"lastEventOrdinal\":0,\"createdAt\":\"2026-08-26T10:00:00Z\",\"terminatedAt\":\"2026-08-26T10:00:02Z\"}";

    private static RemoteInvocationRunner CreateRunner(LoopbackFakePlatform platform) => new(
        new RemoteExpertClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            new RemoteExpertClientOptions(platform.BaseUri.ToString(), () => Token)),
        new RemoteInvocationRunnerOptions(
            new Dictionary<string, string> { [ContractId] = PackageId },
            timeout: TimeSpan.FromMinutes(2)));

    private sealed class RecordingSemanticSink : IExpertSemanticEventSink
    {
        public List<ExpertSemanticEvent> Events { get; } = [];

        public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(semanticEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingSink(CancellationTokenSource cts) : IExpertSemanticEventSink
    {
        public List<ExpertSemanticEvent> Events { get; } = [];

        public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(semanticEvent);
            cts.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    private static class SseFrames
    {
        public static string Event(long ordinal, string text) =>
            "id: " + ordinal + "\ndata: {\"type\":\"event\",\"eventType\":\"delta\",\"payload\":{\"text\":\"" + text +
            "\"}}\n\n";

        public static string Completed(string outputJson) =>
            "data: {\"type\":\"completed\",\"output\":" + outputJson + "}\n\n";
    }

    internal sealed record LoopbackRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    internal abstract record LoopbackResponse
    {
        public static LoopbackResponse Json(int status, string json) => new JsonBody(status, json);

        public static LoopbackResponse Sse(IReadOnlyList<string> frames, bool dropConnection = false) =>
            new SseBody(frames, dropConnection);

        public static LoopbackResponse HangingSse(IReadOnlyList<string> frames, Task release) =>
            new HangingSseBody(frames, release);

        internal sealed record JsonBody(int Status, string Body) : LoopbackResponse;

        internal sealed record SseBody(IReadOnlyList<string> Frames, bool DropConnection) : LoopbackResponse;

        internal sealed record HangingSseBody(IReadOnlyList<string> Frames, Task Release) : LoopbackResponse;
    }

    internal sealed class LoopbackFakePlatform : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;
        private readonly Func<LoopbackRequest, LoopbackResponse> _route;
        private readonly Lock _gate = new();

        private LoopbackFakePlatform(TcpListener listener, Func<LoopbackRequest, LoopbackResponse> route)
        {
            _listener = listener;
            _route = route;
            BaseUri = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
            _serveTask = Task.Run(() => ServeAsync(_cts.Token));
        }

        public Uri BaseUri { get; }

        public List<LoopbackRequest> Requests { get; } = [];

        public static LoopbackFakePlatform Start(Func<LoopbackRequest, LoopbackResponse> route)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackFakePlatform(listener, route);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _serveTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Shutdown races with in-flight connections are irrelevant to the assertions.
            }
            _cts.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 20_000;
                    client.SendTimeout = 20_000;
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, cancellationToken);
                    lock (_gate)
                    {
                        Requests.Add(request);
                    }
                    await WriteResponseAsync(client, stream, _route(request), cancellationToken);
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or ObjectDisposedException)
                {
                    // A client aborting mid-response (cancellation tests) is expected.
                }
            }
        }

        private static async Task<LoopbackRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    throw new IOException("The client closed the connection before sending headers.");
                buffer.Write(chunk, 0, read);
                headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            }

            var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
            var lines = headerText.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            var headerBytes = headerEnd + 4;
            var total = (int)buffer.Length;
            var contentLength = headers.TryGetValue("Content-Length", out var lengthText)
                ? int.Parse(lengthText)
                : 0;
            while (total - headerBytes < contentLength)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    throw new IOException("The client closed the connection before sending the full body.");
                buffer.Write(chunk, 0, read);
                total = (int)buffer.Length;
            }

            var body = Encoding.UTF8.GetString(buffer.GetBuffer(), headerBytes, contentLength);
            return new LoopbackRequest(requestLine[0], requestLine[1], headers, body);
        }

        private static int IndexOfHeaderEnd(byte[] bytes, int length)
        {
            for (var index = 0; index < length - 3; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' && bytes[index + 2] == '\r' &&
                    bytes[index + 3] == '\n')
                    return index;
            }
            return -1;
        }

        private static async Task WriteResponseAsync(
            TcpClient client,
            NetworkStream stream,
            LoopbackResponse response,
            CancellationToken cancellationToken)
        {
            switch (response)
            {
                case LoopbackResponse.JsonBody json:
                    {
                        var body = Encoding.UTF8.GetBytes(json.Body);
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 " + json.Status + " OK\r\nContent-Type: application/json\r\nContent-Length: " +
                            body.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head, cancellationToken);
                        await stream.WriteAsync(body, cancellationToken);
                        break;
                    }
                case LoopbackResponse.SseBody { DropConnection: true } sse:
                    {
                        // Send the frames, then terminate the socket abruptly: the client sees a broken
                        // stream without a terminal frame and must reconnect.
                        await stream.WriteAsync(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"u8.ToArray(),
                            cancellationToken);
                        foreach (var frame in sse.Frames)
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken);
                        client.Close();
                        return;
                    }
                case LoopbackResponse.SseBody sse:
                    {
                        await stream.WriteAsync(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"u8.ToArray(),
                            cancellationToken);
                        foreach (var frame in sse.Frames)
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken);
                        break;
                    }
                case LoopbackResponse.HangingSseBody hanging:
                    {
                        await stream.WriteAsync(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"u8.ToArray(),
                            cancellationToken);
                        foreach (var frame in hanging.Frames)
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken);
                        // Hold the stream open until the test scenario releases it (e.g. on DELETE).
                        await hanging.Release.WaitAsync(cancellationToken);
                        break;
                    }
                default:
                    throw new InvalidOperationException("Unknown loopback response.");
            }
        }
    }
}
