using System.Net;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

public sealed class RemoteExpertClientTests
{
    private const string Token = "secret-token-123";

    private static RemoteExpertClient CreateClient(FakeRemoteHttpHandler handler) =>
        new(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test", () => Token));

    [Fact]
    public async Task ListContractsAsync_ParsesCatalogAndSendsBearerToken()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(
            """
            [
              {
                "contractId": "official/narrator",
                "name": "Narrator",
                "description": "Narrates stories",
                "version": { "major": 1, "minor": 2 },
                "fingerprint": "abc123",
                "inputSchema": { "type": "object" }
              },
              {
                "contractId": "other/second",
                "name": "Other",
                "description": "",
                "version": { "major": 2, "minor": 0 },
                "fingerprint": "def456"
              }
            ]
            """);
        var client = CreateClient(handler);

        var contracts = await client.ListContractsAsync(TestSupport.CancellationToken);

        var request = Assert.Single(handler.CapturedRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://platform.test/abstract-experts", request.Uri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(2, contracts.Count);
        Assert.Equal("official/narrator", contracts[0].ContractId);
        Assert.Equal(new ContractVersion(1, 2), contracts[0].Version);
        Assert.Equal("abc123", contracts[0].Fingerprint);
        Assert.NotNull(contracts[0].InputSchema);
        Assert.Null(contracts[1].InputSchema);
    }

    [Fact]
    public async Task ListExpertPackagesAsync_AppendsContractFilterAndParsesEntries()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(
            """
            [
              {
                "expertPackageId": "official/narrator-pro@1",
                "displayName": "Narrator Pro",
                "openAiModelIds": ["deepseek-v4-pro"],
                "hasSettings": true,
                "contractId": "official/narrator"
              }
            ]
            """);
        var client = CreateClient(handler);

        var packages = await client.ListExpertPackagesAsync("official/narrator", TestSupport.CancellationToken);

        var request = Assert.Single(handler.CapturedRequests);
        Assert.Equal("http://platform.test/expert-packages?contractId=official%2Fnarrator", request.Uri);
        var package = Assert.Single(packages);
        Assert.Equal("official/narrator-pro@1", package.ExpertPackageId);
        Assert.Equal(["deepseek-v4-pro"], package.OpenAiModelIds);
        Assert.True(package.HasSettings);
    }

    [Fact]
    public async Task StartInvocationAsync_SendsWireBodyAndParsesSnapshot()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(
            """
            {
              "expertInvocationId": "0199abc0-1111-7222-8333-444455556666",
              "status": "running",
              "replayed": false,
              "contractId": "official/narrator",
              "expertPackageId": "official/narrator-pro@1",
              "lastEventOrdinal": -1,
              "createdAt": "2026-08-26T10:00:00Z"
            }
            """,
            HttpStatusCode.Accepted);
        var client = CreateClient(handler);

        var snapshot = await client.StartInvocationAsync(
            new RemoteInvocationStartRequest(
                new ExpertContractDescriptor("official/narrator", new ContractVersion(1, 2), "abc123"),
                "official/narrator-pro@1",
                TestSupport.Json("""{"prompt":"hi"}"""),
                idempotencyKey: "channel-key-1",
                timeoutSeconds: 120,
                clientCorrelation: "scenario-7"),
            TestSupport.CancellationToken);

        var request = Assert.Single(handler.CapturedRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://platform.test/expert-invocations", request.Uri);
        Assert.NotNull(request.Body);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("official/narrator", root.GetProperty("contractId").GetString());
        Assert.Equal(1, root.GetProperty("version").GetProperty("major").GetInt32());
        Assert.Equal(2, root.GetProperty("version").GetProperty("minor").GetInt32());
        Assert.Equal("abc123", root.GetProperty("fingerprint").GetString());
        Assert.Equal("official/narrator-pro@1", root.GetProperty("expertPackageId").GetString());
        Assert.Equal("hi", root.GetProperty("input").GetProperty("prompt").GetString());
        Assert.Equal("channel-key-1", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal(120, root.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("scenario-7", root.GetProperty("clientCorrelation").GetString());

        Assert.Equal("0199abc0-1111-7222-8333-444455556666", snapshot.ExpertInvocationId);
        Assert.Equal(RemoteInvocationStatus.Running, snapshot.Status);
        Assert.False(snapshot.Replayed);
        Assert.Equal(-1, snapshot.LastEventOrdinal);
        Assert.Null(snapshot.Output);
    }

    [Fact]
    public async Task StartInvocationAsync_ParsesCompletedSnapshotWithOutputJsonText()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(
            """
            {
              "expertInvocationId": "id-2",
              "status": "completed",
              "replayed": true,
              "contractId": "official/narrator",
              "expertPackageId": "official/narrator-pro@1",
              "lastEventOrdinal": 4,
              "output": "{\"text\":\"done\"}",
              "errorCode": null,
              "errorMessage": null,
              "createdAt": "2026-08-26T10:00:00Z",
              "terminatedAt": "2026-08-26T10:00:05Z"
            }
            """,
            HttpStatusCode.Accepted);
        var client = CreateClient(handler);

        var snapshot = await client.StartInvocationAsync(
            new RemoteInvocationStartRequest(
                new ExpertContractDescriptor("official/narrator", new ContractVersion(1, 2), "abc123"),
                "official/narrator-pro@1",
                TestSupport.Json("{}")),
            TestSupport.CancellationToken);

        Assert.Equal(RemoteInvocationStatus.Completed, snapshot.Status);
        Assert.True(snapshot.Replayed);
        Assert.Equal(4, snapshot.LastEventOrdinal);
        Assert.NotNull(snapshot.Output);
        Assert.Equal("done", snapshot.Output!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetInvocationAsync_And_CancelInvocationAsync_UseInvocationRoutes()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson("""{"expertInvocationId":"id-9","status":"running","replayed":false,"contractId":"c","expertPackageId":"p","lastEventOrdinal":-1,"createdAt":"2026-08-26T10:00:00Z"}""");
        handler.EnqueueJson("""{"expertInvocationId":"id-9","status":"cancelled","replayed":false,"contractId":"c","expertPackageId":"p","lastEventOrdinal":2,"createdAt":"2026-08-26T10:00:00Z","terminatedAt":"2026-08-26T10:00:03Z"}""");
        var client = CreateClient(handler);

        var running = await client.GetInvocationAsync("id-9", TestSupport.CancellationToken);
        var cancelled = await client.CancelInvocationAsync("id-9", TestSupport.CancellationToken);

        Assert.Equal("http://platform.test/expert-invocations/id-9", handler.CapturedRequests[0].Uri);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequests[0].Method);
        Assert.Equal("http://platform.test/expert-invocations/id-9", handler.CapturedRequests[1].Uri);
        Assert.Equal(HttpMethod.Delete, handler.CapturedRequests[1].Method);
        Assert.Equal(RemoteInvocationStatus.Running, running.Status);
        Assert.Equal(RemoteInvocationStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task StreamEventsAsync_ReplaysFromLastEventIdHeaderWhenPresent()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueSse(
            """
            id: 3
            data: {"type":"event","eventType":"delta","payload":{"text":"a"}}

            data: {"type":"completed","output":"null"}

            """);
        var client = CreateClient(handler);

        var frames = new List<RemoteExpertStreamFrame>();
        await foreach (var frame in client.StreamEventsAsync("id-9", fromOrdinal: 2, TestSupport.CancellationToken))
            frames.Add(frame);

        var request = Assert.Single(handler.CapturedRequests);
        Assert.Equal("http://platform.test/expert-invocations/id-9/events", request.Uri);
        Assert.Equal("2", request.LastEventId);
        Assert.Equal(2, frames.Count);
        _ = Assert.IsType<RemoteExpertEventFrame>(frames[0]);
        _ = Assert.IsType<RemoteExpertCompletedFrame>(frames[1]);
    }

    [Fact]
    public async Task StreamEventsAsync_OmitsLastEventIdHeaderWhenReplayingEverything()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueSse("data: {\"type\":\"cancelled\"}\n\n");
        var client = CreateClient(handler);

        _ = await client.StreamEventsAsync("id-9", cancellationToken: TestSupport.CancellationToken)
            .ToListAsync(TestSupport.CancellationToken);

        var request = Assert.Single(handler.CapturedRequests);
        Assert.Null(request.LastEventId);
    }

    [Fact]
    public async Task ErrorResponses_WithProblemCode_MapToTypedExceptions()
    {
        var cases = new (string Json, Type Expected, int ExpectedStatus)[]
        {
            ("""{"type":"about:blank","title":"Not Found","status":404,"detail":"gone","code":"not-found"}""", typeof(RemoteInvocationNotFoundException), 400),
            ("""{"title":"Bad Request","status":400,"detail":"bad input","code":"validation"}""", typeof(RemoteValidationException), 400),
            ("""{"title":"Conflict","status":409,"detail":"key clash","code":"idempotency-conflict"}""", typeof(RemoteIdempotencyConflictException), 400),
            ("""{"title":"Server Error","status":500,"detail":"boom","code":"internal"}""", typeof(RemoteInternalException), 400)
        };
        foreach (var (json, expected, expectedStatus) in cases)
        {
            var handler = new FakeRemoteHttpHandler();
            handler.EnqueueProblem(json, HttpStatusCode.BadRequest);
            var client = CreateClient(handler);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                client.ListContractsAsync(TestSupport.CancellationToken));

            var remote = Assert.IsType<RemoteExpertException>(exception, exactMatch: false);
            Assert.Equal(expected, exception.GetType());
            Assert.Equal(expectedStatus, remote.StatusCode);
        }
    }

    [Fact]
    public async Task ProblemContractMismatch_ParsesExtensionDetails()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueProblem(
            """
            {
              "title": "Contract mismatch",
              "status": 409,
              "detail": "The invocation contract does not match the registered contract.",
              "code": "contract-mismatch",
              "extensions": {
                "requiredVersion": "1.2",
                "requiredFingerprint": "aaa",
                "availableVersion": "1.0",
                "availableFingerprint": "bbb"
              }
            }
            """,
            HttpStatusCode.Conflict);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RemoteContractMismatchException>(() =>
            client.ListContractsAsync(TestSupport.CancellationToken));

        Assert.Equal("1.2", exception.RequiredVersion);
        Assert.Equal("aaa", exception.RequiredFingerprint);
        Assert.Equal("1.0", exception.AvailableVersion);
        Assert.Equal("bbb", exception.AvailableFingerprint);
    }

    [Fact]
    public async Task ErrorResponse_WithoutCode_MapsToGenericExceptionWithStatus()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueProblem("""{"title":"Unauthorized","status":401,"detail":"no bearer"}""", HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RemoteExpertException>(() =>
            client.ListContractsAsync(TestSupport.CancellationToken));

        Assert.Null(exception.ErrorCode);
        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponses_RedactTheBearerToken()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueProblem(
            "{\"title\":\"Error\",\"status\":500,\"detail\":\"auth failed for " + Token +
            "\",\"code\":\"internal\"}",
            HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RemoteInternalException>(() =>
            client.ListContractsAsync(TestSupport.CancellationToken));

        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_BodiesAreTruncated()
    {
        var padding = new string('x', 4096);
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson($"{{\"detail\":\"{padding}\"}}", HttpStatusCode.BadGateway);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RemoteExpertException>(() =>
            client.ListContractsAsync(TestSupport.CancellationToken));

        Assert.True(exception.Message.Length < 1024, $"Message length {exception.Message.Length} is not truncated.");
    }

    [Fact]
    public void Options_EndpointRequiresAbsoluteHttpUri()
    {
        _ = Assert.Throws<ArgumentException>(() => new RemoteExpertClientOptions("platform.test"));
        _ = Assert.Throws<ArgumentException>(() => new RemoteExpertClientOptions("ftp://platform.test"));
    }

    [Fact]
    public void Options_EndpointWithoutTrailingSlashIsNormalized()
    {
        var options = new RemoteExpertClientOptions("http://platform.test/base");
        Assert.Equal("http://platform.test/base/", options.BaseUri.ToString());
    }
}
