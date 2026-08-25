using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class PlaygroundHttpTests
{
    private const string SampleContractId = "thousandli.expert/narrator";
    private const string SampleContractFingerprint =
        "c514466424e626a6f24dfb5b53894c493351fb2466d30f1ba6e00e5153264b10";

    [Fact]
    public async Task PlaygroundPageAndReadRoutesServe()
    {
        await using var app = await StartAppAsync(ephemeral: true, dataRoot: null);
        using var client = CreateClient(app);

        var page = await client.GetAsync("/playground", TestSupport.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "ThousandLi Expert Playground",
            await page.Content.ReadAsStringAsync(TestSupport.CancellationToken),
            StringComparison.Ordinal);

        var contracts = await client.GetFromJsonAsync<JsonElement>("/api/playground/contracts", TestSupport.CancellationToken);
        var narrator = Assert.Single(contracts.EnumerateArray());
        Assert.Equal(SampleContractId, narrator.GetProperty("contractId").GetString());
        Assert.Equal("1.0", narrator.GetProperty("version").GetString());
        Assert.Equal(SampleContractFingerprint, narrator.GetProperty("fingerprint").GetString());
        Assert.Equal(["fake"], narrator.GetProperty("executors").EnumerateArray().Select(entry => entry.GetString()));

        var history = await client.GetFromJsonAsync<JsonElement>("/api/playground/history", TestSupport.CancellationToken);
        Assert.Equal(JsonValueKind.Array, history.ValueKind);
        Assert.Equal(0, history.GetArrayLength());

        var recordings = await client.GetFromJsonAsync<JsonElement>("/api/playground/recordings", TestSupport.CancellationToken);
        Assert.Equal(0, recordings.GetArrayLength());
    }

    [Fact]
    public async Task InvokeStreamsSemanticEventsThenInvocationTerminalAndPersistsAFileRecording()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-http-");
        try
        {
            const string workspaceId = "playground-http-tests";
            await using var app = await StartAppAsync(ephemeral: false, dataRoot: root.FullName, workspaceId: workspaceId);
            using var client = CreateClient(app);

            using var response = await client.PostAsJsonAsync(
                "/api/playground/invoke",
                new
                {
                    contractId = SampleContractId,
                    executor = "fake",
                    scenarioId = "advance",
                    input = new { choice = "advance" },
                    record = true
                },
                TestSupport.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            var frames = await ReadSseFramesAsync(response);

            var eventFrame = frames[0];
            Assert.Equal("event", eventFrame.GetProperty("type").GetString());
            Assert.Equal("chunk", eventFrame.GetProperty("eventType").GetString());
            Assert.Equal("A new path opens.", eventFrame.GetProperty("payload").GetProperty("text").GetString());

            var terminalFrame = frames[^1];
            Assert.Equal("invocation", terminalFrame.GetProperty("type").GetString());
            Assert.Matches("""^fake-\d+$""", terminalFrame.GetProperty("invocationId").GetString());
            Assert.Matches("""^playground-\d{8}$""", terminalFrame.GetProperty("channelKey").GetString());
            Assert.True(terminalFrame.GetProperty("durationMs").GetInt64() >= 0);
            Assert.Equal("A new path opens.", terminalFrame.GetProperty("output").GetProperty("text").GetString());
            var recordingId = terminalFrame.GetProperty("recordingId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(recordingId));

            var history = await client.GetFromJsonAsync<JsonElement>("/api/playground/history", TestSupport.CancellationToken);
            var historyEntry = Assert.Single(history.EnumerateArray());
            Assert.Equal("committed", historyEntry.GetProperty("status").GetString());
            Assert.Equal(recordingId, historyEntry.GetProperty("recordingId").GetString());
            Assert.Equal(SampleContractId, historyEntry.GetProperty("contractId").GetString());
            Assert.Equal("fake", historyEntry.GetProperty("executor").GetString());
            Assert.Matches("""^playground-\d{8}$""", historyEntry.GetProperty("channelKey").GetString());
            Assert.Matches("""^fake-\d+$""", historyEntry.GetProperty("invocationId").GetString());
            Assert.True(historyEntry.GetProperty("durationMs").GetInt64() >= 0);

            var summaries = await client.GetFromJsonAsync<JsonElement>("/api/playground/recordings", TestSupport.CancellationToken);
            var summary = Assert.Single(summaries.EnumerateArray());
            Assert.Equal(recordingId, summary.GetProperty("recordingId").GetString());
            Assert.Equal(SampleContractId, summary.GetProperty("contractId").GetString());
            Assert.Equal("committed", summary.GetProperty("status").GetString());
            Assert.Equal("fake", summary.GetProperty("executor").GetString());
            Assert.Equal("advance", summary.GetProperty("scenarioId").GetString());

            var recording = await client.GetFromJsonAsync<JsonElement>(
                $"/api/playground/recordings/{recordingId}", TestSupport.CancellationToken);
            Assert.Equal(1, recording.GetProperty("formatMajor").GetInt32());
            Assert.Equal(SampleContractId, recording.GetProperty("contract").GetProperty("id").GetString());
            Assert.Equal("committed", recording.GetProperty("terminal").GetProperty("status").GetString());

            var recordingsDirectory = Path.Combine(root.FullName, "recordings", workspaceId);
            var files = Directory.GetFiles(recordingsDirectory);
            var file = Assert.Single(files);
            Assert.Equal(recordingId + ".json", Path.GetFileName(file));
            Assert.Empty(Directory.GetFiles(recordingsDirectory, "*.tmp"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InvokeRejectsInvalidCommandsPreStreamWithProblemJson()
    {
        await using var app = await StartAppAsync(ephemeral: true, dataRoot: null);
        using var client = CreateClient(app);

        await AssertRejectedAsync(client, new
        {
            contractId = SampleContractId,
            executor = "remote",
            scenarioId = "advance",
            input = new { }
        }, "remote");
        await AssertRejectedAsync(client, new
        {
            contractId = SampleContractId,
            executor = "fake",
            input = new { }
        }, "scenario id");
        await AssertRejectedAsync(client, new
        {
            contractId = "tests/unknown",
            executor = "fake",
            scenarioId = "advance",
            input = new { }
        }, "tests/unknown");
        await AssertRejectedAsync(client, new
        {
            contractId = SampleContractId,
            executor = "local",
            input = new { }
        }, "--expert-executor local");

        using var malformed = await client.PostAsync(
            "/api/playground/invoke",
            new StringContent("{ not json", new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
            TestSupport.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("application/problem+json", malformed.Content.Headers.ContentType?.MediaType);
        return;

        static async Task AssertRejectedAsync(
            HttpClient httpClient,
            object command,
            string expectedFragment)
        {
            using var response = await httpClient.PostAsJsonAsync("/api/playground/invoke", command, TestSupport.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestSupport.CancellationToken);
            Assert.Contains(expectedFragment, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InvokeEmitsErrorTerminalFrameForFailingExecution()
    {
        await using var app = await StartAppAsync(ephemeral: true, dataRoot: null);
        using var client = CreateClient(app);

        using var response = await client.PostAsJsonAsync(
            "/api/playground/invoke",
            new
            {
                contractId = SampleContractId,
                executor = "fake",
                scenarioId = "missing-scenario",
                input = new { }
            },
            TestSupport.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var frames = await ReadSseFramesAsync(response);
        var frame = Assert.Single(frames);
        Assert.Equal("error", frame.GetProperty("type").GetString());
        Assert.Contains("No Fake scenario", frame.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Matches("""^playground-\d{8}$""", frame.GetProperty("channelKey").GetString());

        var history = await client.GetFromJsonAsync<JsonElement>("/api/playground/history", TestSupport.CancellationToken);
        var entry = Assert.Single(history.EnumerateArray());
        Assert.Equal("error", entry.GetProperty("status").GetString());
        Assert.NotNull(entry.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReplayComparesAgainstTheStoredRecordingAndRejectsUnknownIds()
    {
        await using var app = await StartAppAsync(ephemeral: true, dataRoot: null);
        using var client = CreateClient(app);

        var recordingId = await InvokeRecordedAsync(client);
        var replay = await client.PostAsJsonAsync(
            "/api/playground/replay",
            new { recordingId, executor = "fake" },
            TestSupport.CancellationToken);
        replay.EnsureSuccessStatusCode();
        var report = await replay.Content.ReadFromJsonAsync<JsonElement>(TestSupport.CancellationToken);
        Assert.True(report.GetProperty("matches").GetBoolean());
        Assert.False(report.GetProperty("strictPayloads").GetBoolean());
        Assert.Equal(0, report.GetProperty("divergences").GetArrayLength());
        Assert.Equal("committed", report.GetProperty("diagnostics").GetProperty("status").GetString());

        using var unknown = await client.PostAsJsonAsync(
            "/api/playground/replay",
            new { recordingId = "no-such-recording", executor = "fake" },
            TestSupport.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var problem = await unknown.Content.ReadFromJsonAsync<JsonElement>(TestSupport.CancellationToken);
        Assert.Contains("does not exist", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EphemeralModeServesRecordingsFromMemoryWithoutTouchingDisk()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-http-");
        try
        {
            await using var app = await StartAppAsync(ephemeral: true, dataRoot: root.FullName);
            using var client = CreateClient(app);

            var recordingId = await InvokeRecordedAsync(client);

            var summaries = await client.GetFromJsonAsync<JsonElement>("/api/playground/recordings", TestSupport.CancellationToken);
            var summary = Assert.Single(summaries.EnumerateArray());
            Assert.Equal(recordingId, summary.GetProperty("recordingId").GetString());
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "recordings")));
            Assert.Empty(Directory.GetFiles(root.FullName, "*.json", SearchOption.AllDirectories));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<string> InvokeRecordedAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/playground/invoke",
            new
            {
                contractId = SampleContractId,
                executor = "fake",
                scenarioId = "advance",
                input = new { },
                record = true
            },
            TestSupport.CancellationToken);
        response.EnsureSuccessStatusCode();
        var frames = await ReadSseFramesAsync(response);
        var terminalFrame = frames[^1];
        Assert.Equal("invocation", terminalFrame.GetProperty("type").GetString());
        var recordingId = terminalFrame.GetProperty("recordingId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(recordingId));
        Assert.NotNull(recordingId);
        return recordingId;
    }

    private static async Task<List<JsonElement>> ReadSseFramesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestSupport.CancellationToken);
        var frames = new List<JsonElement>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            using var document = JsonDocument.Parse(block["data: ".Length..]);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    private static HttpClient CreateClient(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task<WebApplication> StartAppAsync(bool ephemeral, string? dataRoot, string? workspaceId = null)
    {
        var port = ReservePort();
        var repositoryRoot = TestSupport.FindRepositoryRoot();
        var options = new DevHostOptions
        {
            ArtifactDirectory = Path.Combine(
                repositoryRoot,
                "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact"),
            WorkspaceId = workspaceId ?? $"playground-http-{Guid.NewGuid():N}",
            FrontendUrl = "/game/",
            SessionId = $"session-{Guid.NewGuid():N}",
            Port = port,
            Ephemeral = ephemeral,
            DataRoot = dataRoot,
            FakeScenariosPath = Path.Combine(repositoryRoot, "samples", "ThousandLi.SampleGame", "fake-scenarios.json")
        };
        var app = await DevHostApplication.BuildAsync(options, webApplicationArgs: [], TestSupport.CancellationToken);
        await app.StartAsync(TestSupport.CancellationToken);
        return app;
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
