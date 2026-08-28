using System.Net;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

public sealed class RemoteExpertExecutorTests
{
    private const string ContractId = "tests/narrator";
    private const string Fingerprint = "tests-narrator-v1";
    private const string PackageId = "tests/narrator-pro@1";
    private const string InvocationId = "0199abc0-1111-7222-8333-444455556666";

    private static string CatalogJson(string versionMajor = "1", string versionMinor = "0", string fingerprint = Fingerprint) =>
        "[{\"contractId\":\"" + ContractId + "\",\"name\":\"Narrator\",\"description\":\"\",\"version\":{\"major\":" +
        versionMajor + ",\"minor\":" + versionMinor + "},\"fingerprint\":\"" + fingerprint + "\"}]";

    private static string SnapshotJson() =>
        "{\"expertInvocationId\":\"" + InvocationId + "\",\"status\":\"running\",\"replayed\":false,\"contractId\":\"" +
        ContractId + "\",\"expertPackageId\":\"" + PackageId +
        "\",\"lastEventOrdinal\":-1,\"createdAt\":\"2026-08-26T10:00:00Z\"}";

    private static string EventSse(long ordinal, string text = "x") =>
        "id: " + ordinal + "\ndata: {\"type\":\"event\",\"eventType\":\"delta\",\"payload\":{\"text\":\"" + text +
        "\"}}\n\n";

    private static RemoteExpertExecutor CreateExecutor(
        FakeRemoteHttpHandler handler,
        TimeSpan? timeout = null,
        int maxReconnects = 3) =>
        new(
            new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
            new RemoteExpertExecutorOptions(
                new Dictionary<string, string> { [ContractId] = PackageId },
                timeout,
                maxReconnects));

    private static ExpertInvocationRequest CreateRequest(string? channelKey = null) => new(
        TestSupport.Contract,
        scenarioId: "scenario-1",
        input: TestSupport.Json("""{"prompt":"hi"}"""),
        channelKey: channelKey);

    private sealed class CollectingSink : IExpertSemanticEventSink
    {
        public List<ExpertSemanticEvent> Events { get; } = [];

        public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(semanticEvent);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithoutBinding_FailsDeterministicallyBeforeAnyTraffic()
    {
        var handler = new FakeRemoteHttpHandler();
        var executor = new RemoteExpertExecutor(
            new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
            new RemoteExpertExecutorOptions());

        var exception = await Assert.ThrowsAsync<RemoteExpertException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Empty(handler.CapturedRequests);
        Assert.Contains(ContractId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("none", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownContract_FailsPrecheck()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson("[]");
        var executor = CreateExecutor(handler);

        var exception = await Assert.ThrowsAsync<RemoteUnknownContractException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Single(handler.CapturedRequests);
        Assert.Contains(ContractId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithMismatchedCatalogContract_FailsPrecheckWithDetails()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson(fingerprint: "other-fingerprint"));
        var executor = CreateExecutor(handler);

        var exception = await Assert.ThrowsAsync<RemoteContractMismatchException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Single(handler.CapturedRequests);
        Assert.Equal(Fingerprint, exception.RequiredFingerprint);
        Assert.Equal("other-fingerprint", exception.AvailableFingerprint);
        Assert.Contains("Contract mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnsupportedMinorVersion_FailsPrecheck()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson(versionMinor: "0"));
        var olderRequest = new ExpertInvocationRequest(
            new ExpertContractDescriptor(ContractId, new ContractVersion(2, 0), Fingerprint),
            scenarioId: null,
            input: TestSupport.Json("{}"),
            channelKey: null);
        var executor = CreateExecutor(handler);

        _ = await Assert.ThrowsAsync<RemoteContractMismatchException>(() =>
            executor.ExecuteAsync(olderRequest, new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Single(handler.CapturedRequests);
    }

    [Fact]
    public async Task ExecuteAsync_StreamsEventsAndCompletes()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse(EventSse(0, "a") + EventSse(1, "b") +
            "data: {\"type\":\"completed\",\"output\":{\"done\":true}}\n\n");
        var executor = CreateExecutor(handler);
        var sink = new CollectingSink();

        var result = await executor.ExecuteAsync(
            CreateRequest(channelKey: "channel-42"), sink, TestSupport.CancellationToken);

        Assert.Equal(3, handler.CapturedRequests.Count);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequests[0].Method);
        Assert.EndsWith("/abstract-experts", handler.CapturedRequests[0].Uri, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequests[1].Method);
        Assert.EndsWith("/expert-invocations", handler.CapturedRequests[1].Uri, StringComparison.Ordinal);
        var startBodyText = handler.CapturedRequests[1].Body;
        Assert.NotNull(startBodyText);
        using var startBody = JsonDocument.Parse(startBodyText);
        Assert.Equal("channel-42", startBody.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal(PackageId, startBody.RootElement.GetProperty("expertPackageId").GetString());
        Assert.EndsWith("/expert-invocations/" + InvocationId + "/events", handler.CapturedRequests[2].Uri,
            StringComparison.Ordinal);

        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("delta", sink.Events[0].EventType);
        Assert.Equal("a", sink.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("b", sink.Events[1].Payload.GetProperty("text").GetString());
        Assert.Equal(InvocationId, result.InvocationId);
        Assert.True(result.Output.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutChannelKey_UsesRandomIdempotencyKey()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse("data: {\"type\":\"completed\"}\n\n");
        var executor = CreateExecutor(handler);

        _ = await executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken);

        var startBodyText = handler.CapturedRequests[1].Body;
        Assert.NotNull(startBodyText);
        using var startBody = JsonDocument.Parse(startBodyText);
        var key = startBody.RootElement.GetProperty("idempotencyKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.NotEqual("channel-42", key);
    }

    [Fact]
    public async Task ExecuteAsync_WithChannelKey_ReplaysSameIdempotencyKeyWithoutResemanticizing()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(
            "{\"expertInvocationId\":\"replayed-id\",\"status\":\"completed\",\"replayed\":true,\"contractId\":\"" +
            ContractId + "\",\"expertPackageId\":\"" + PackageId +
            "\",\"lastEventOrdinal\":1,\"output\":\"{}\",\"createdAt\":\"2026-08-26T10:00:00Z\"}",
            HttpStatusCode.Accepted);
        handler.EnqueueSse("data: {\"type\":\"completed\",\"output\":\"{}\"}\n\n");
        var executor = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            CreateRequest(channelKey: "stable-channel"), new CollectingSink(), TestSupport.CancellationToken);

        var startBodyText = handler.CapturedRequests[1].Body;
        Assert.NotNull(startBodyText);
        using var startBody = JsonDocument.Parse(startBodyText);
        Assert.Equal("stable-channel", startBody.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("replayed-id", result.InvocationId);
    }

    [Fact]
    public async Task ExecuteAsync_ReconnectsWithLastEventIdAndDeduplicatesReplayedOrdinals()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse(EventSse(0, "a"));
        handler.EnqueueSse(EventSse(0, "a") + EventSse(1, "b") +
            "data: {\"type\":\"completed\"}\n\n");
        var executor = CreateExecutor(handler);
        var sink = new CollectingSink();

        var result = await executor.ExecuteAsync(CreateRequest(), sink, TestSupport.CancellationToken);

        Assert.Equal(4, handler.CapturedRequests.Count);
        Assert.Equal("0", handler.CapturedRequests[3].LastEventId);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("a", sink.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("b", sink.Events[1].Payload.GetProperty("text").GetString());
        Assert.Equal(InvocationId, result.InvocationId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReconnectsAreExhausted_FailsWithConnectionLoss()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse(EventSse(0, "a"));
        handler.EnqueueSse(EventSse(1, "b"));
        var executor = CreateExecutor(handler, maxReconnects: 1);

        var exception = await Assert.ThrowsAsync<RemoteExpertException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Contains("after 2 connection attempts", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task ExecuteAsync_WithFailedTerminalFrame_MapsStableCode()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse(
            "data: {\"type\":\"failed\",\"code\":\"validation\",\"message\":\"schema violation\"}\n\n");
        var executor = CreateExecutor(handler);

        var exception = await Assert.ThrowsAsync<RemoteValidationException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Contains("schema violation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancelledTerminalFrame_ThrowsCancelled()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.EnqueueSse("data: {\"type\":\"cancelled\"}\n\n");
        var executor = CreateExecutor(handler);

        _ = await Assert.ThrowsAsync<RemoteInvocationCancelledException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ExecuteAsync_WithLocalCancellation_SendsDeleteThenThrowsCancelled()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.Enqueue(SseResponse(new PrefixThenHangingStream(
            Encoding.UTF8.GetBytes(EventSse(0, "a") + ": keepalive\n\n"))));
        handler.EnqueueJson(
            "{\"expertInvocationId\":\"" + InvocationId +
            "\",\"status\":\"cancelled\",\"replayed\":false,\"contractId\":\"c\",\"expertPackageId\":\"p\",\"lastEventOrdinal\":0,\"createdAt\":\"2026-08-26T10:00:00Z\"}");
        var executor = CreateExecutor(handler);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestSupport.CancellationToken);
        var sink = new CancellingSink(cts);

        var exception = await Assert.ThrowsAsync<RemoteInvocationCancelledException>(() =>
            executor.ExecuteAsync(CreateRequest(), sink, cts.Token).AsTask());
        _ = Assert.IsType<OperationCanceledException>(exception, exactMatch: false);

        Assert.Equal(4, handler.CapturedRequests.Count);
        Assert.Equal(HttpMethod.Delete, handler.CapturedRequests[3].Method);
        Assert.EndsWith("/expert-invocations/" + InvocationId, handler.CapturedRequests[3].Uri,
            StringComparison.Ordinal);

        // The stream hangs after the first event, so the cancellation surfaces from the blocked
        // stream read rather than from a remote terminal frame.
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeout_SendsDeleteThenThrowsTimeout()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), HttpStatusCode.Accepted);
        handler.Enqueue(SseResponse(new PrefixThenHangingStream(": keepalive\n\n"u8.ToArray())));
        handler.EnqueueJson(
            "{\"expertInvocationId\":\"" + InvocationId +
            "\",\"status\":\"cancelled\",\"replayed\":false,\"contractId\":\"c\",\"expertPackageId\":\"p\",\"lastEventOrdinal\":0,\"createdAt\":\"2026-08-26T10:00:00Z\"}");
        var executor = CreateExecutor(handler, timeout: TimeSpan.FromMilliseconds(200));

        var exception = await Assert.ThrowsAsync<RemoteInvocationTimeoutException>(() =>
            executor.ExecuteAsync(CreateRequest(), new CollectingSink(), TestSupport.CancellationToken).AsTask());

        Assert.Contains("timeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(4, handler.CapturedRequests.Count);
        Assert.Equal(HttpMethod.Delete, handler.CapturedRequests[3].Method);
    }

    private static HttpResponseMessage SseResponse(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream)
    };

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

    /// <summary>Delivers the prefix bytes once, then blocks every further read until cancelled.</summary>
    private sealed class PrefixThenHangingStream(byte[] prefix) : Stream
    {
        private bool _prefixDelivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_prefixDelivered)
            {
                _prefixDelivered = true;
                var count = Math.Min(prefix.Length, buffer.Length);
                prefix.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
