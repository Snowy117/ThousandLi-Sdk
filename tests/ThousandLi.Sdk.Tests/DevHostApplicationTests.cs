using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class DevHostApplicationTests
{
    [Fact]
    public async Task RunningDevHostServesPreviewActionStreamAndFrontendRequest()
    {
        var port = ReservePort();
        var repositoryRoot = FindRepositoryRoot();
        var artifact = Path.Combine(
            repositoryRoot,
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");
        var scenarios = Path.Combine(repositoryRoot, "samples", "ThousandLi.SampleGame", "fake-scenarios.json");
        var options = new DevHostOptions
        {
            ArtifactDirectory = artifact,
            WorkspaceId = "http-tests",
            FrontendUrl = "/game/",
            SessionId = $"session-{Guid.NewGuid():N}",
            Port = port,
            Ephemeral = true,
            FakeScenariosPath = scenarios
        };
        await using var app = await DevHostApplication.BuildAsync(
            options,
            webApplicationArgs: [],
            TestSupport.CancellationToken);
        await app.StartAsync(TestSupport.CancellationToken);
        try
        {
            using var client = CreateClient(new Uri($"http://127.0.0.1:{port}"));

            var shell = await client.GetStringAsync("/", TestSupport.CancellationToken);
            Assert.Contains("<iframe", shell, StringComparison.Ordinal);
            var frontend = await client.GetAsync("/game/index.html", TestSupport.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, frontend.StatusCode);

            var initial = await client.GetFromJsonAsync<JsonElement>("/api/session", TestSupport.CancellationToken);
            Assert.Equal(0, initial.GetProperty("committedState").GetProperty("turn").GetInt32());

            var actionResponse = await client.PostAsJsonAsync(
                "/api/actions",
                new { choice = "advance" },
                TestSupport.CancellationToken);
            actionResponse.EnsureSuccessStatusCode();
            var lines = (await actionResponse.Content.ReadAsStringAsync(TestSupport.CancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Equal(["started", "frontendEvent", "committed"],
                    lines.Select(line => line.RootElement.GetProperty("type").GetString()));
                Assert.Single(lines, line =>
                    line.RootElement.GetProperty("type").GetString() is "committed" or "aborted");
            }
            finally
            {
                foreach (var line in lines) line.Dispose();
            }

            var requestResponse = await client.PostAsJsonAsync(
                "/api/frontend-requests",
                new { request = "state" },
                TestSupport.CancellationToken);
            requestResponse.EnsureSuccessStatusCode();
            var committed = await requestResponse.Content.ReadFromJsonAsync<JsonElement>(
                TestSupport.CancellationToken);
            Assert.Equal(1, committed.GetProperty("turn").GetInt32());
            Assert.Equal("A new path opens.", committed.GetProperty("lastNarrative").GetString());
        }
        finally
        {
            await app.StopAsync(TestSupport.CancellationToken);
        }
    }

    [Fact]
    public async Task ReflectActionStreamsTypedExpertAndCommitsVariableUpdates()
    {
        var port = ReservePort();
        var repositoryRoot = FindRepositoryRoot();
        var artifact = Path.Combine(
            repositoryRoot,
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");
        var scenarios = Path.Combine(repositoryRoot, "samples", "ThousandLi.SampleGame", "fake-scenarios.json");
        var options = new DevHostOptions
        {
            ArtifactDirectory = artifact,
            WorkspaceId = "http-tests",
            FrontendUrl = "/game/",
            SessionId = $"session-{Guid.NewGuid():N}",
            Port = port,
            Ephemeral = true,
            FakeScenariosPath = scenarios
        };
        await using var app = await DevHostApplication.BuildAsync(
            options,
            webApplicationArgs: [],
            TestSupport.CancellationToken);
        await app.StartAsync(TestSupport.CancellationToken);
        try
        {
            using var client = CreateClient(new Uri($"http://127.0.0.1:{port}"));

            var actionResponse = await client.PostAsJsonAsync(
                "/api/actions",
                new { type = "reflect" },
                TestSupport.CancellationToken);
            actionResponse.EnsureSuccessStatusCode();
            var lines = (await actionResponse.Content.ReadAsStringAsync(TestSupport.CancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var types = lines.Select(line => line.RootElement.GetProperty("type").GetString()).ToArray();
                Assert.Equal("started", types[0]);
                Assert.Equal("committed", types[^1]);
                Assert.All(types[1..^1], type => Assert.Equal("frontendEvent", type));
                Assert.Contains(lines, line =>
                    line.RootElement.GetProperty("type").GetString() == "frontendEvent" &&
                    line.RootElement.GetProperty("frontendEvent").GetProperty("eventType").GetString() ==
                    "narrativeDelta");
            }
            finally
            {
                foreach (var line in lines) line.Dispose();
            }

            var requestResponse = await client.PostAsJsonAsync(
                "/api/frontend-requests",
                new { request = "state" },
                TestSupport.CancellationToken);
            requestResponse.EnsureSuccessStatusCode();
            var committed = await requestResponse.Content.ReadFromJsonAsync<JsonElement>(
                TestSupport.CancellationToken);
            var variables = committed.GetProperty("_gameHelper").GetProperty("sessionVariables");
            Assert.Equal(0, committed.GetProperty("turn").GetInt32());
            Assert.Equal(
                "You pause and consider how far you have come.",
                committed.GetProperty("lastNarrative").GetString());
            // 脚本化 variableUpdates 的 delta/replace 生效；指向仅持久化成员的命令被拒绝，
            // reflectCount 由游戏侧在补丁后状态上簿记。
            Assert.Equal(15, variables.GetProperty("courage").GetInt32());
            Assert.Equal(80, variables.GetProperty("trust").GetInt32());
            Assert.Equal(1, variables.GetProperty("reflectCount").GetInt32());
        }
        finally
        {
            await app.StopAsync(TestSupport.CancellationToken);
        }
    }

    [Fact]
    public async Task RemoteExecutorStartsGameArtifactsWithoutFakeScenarioBindings()
    {
        var port = ReservePort();
        var repositoryRoot = FindRepositoryRoot();
        var artifact = Path.Combine(
            repositoryRoot,
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");
        var options = new DevHostOptions
        {
            ArtifactDirectory = artifact,
            WorkspaceId = "http-tests",
            FrontendUrl = "/game/",
            SessionId = $"session-{Guid.NewGuid():N}",
            Port = port,
            Ephemeral = true,
            ExpertExecutor = DevHostOptions.RemoteExecutorName,
            RemoteEndpoint = "http://127.0.0.1:9/"
        };
        await using var app = await DevHostApplication.BuildAsync(
            options,
            webApplicationArgs: [],
            TestSupport.CancellationToken);
        await app.StartAsync(TestSupport.CancellationToken);
        try
        {
            using var client = CreateClient(new Uri($"http://127.0.0.1:{port}"));

            var shell = await client.GetStringAsync("/", TestSupport.CancellationToken);
            Assert.Contains("<iframe", shell, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestSupport.CancellationToken);
        }
    }

    private static HttpClient CreateClient(Uri baseAddress) => new() { BaseAddress = baseAddress };

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThousandLi.Sdk.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the SDK repository root.");
    }
}
