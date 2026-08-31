using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Phase G composition tests: the DevHost remote mode flags (parse + three-way mode
/// mutual exclusion), the composition factory, and the Playground remote merge / invoke paths
/// driven through the queue-based fake platform handler.
/// </summary>
public sealed class RemoteExpertCompositionTests
{
    private const string LongTextWritingContractId = "thousandli.expert/long-text-writing";
    private const string RemotePackageId = "official-longtextwriting@0.1.0";
    private const string RemoteInvocationId = "0199beef-1234-7556-8777-88889999aaaa";

    private static DevHostOptions BaseOptions(string mode = DevHostOptions.FakeExecutorName) => new()
    {
        ArtifactDirectory = ".",
        WorkspaceId = "default",
        FrontendUrl = "/game/",
        SessionId = "session",
        Experts = mode
    };

    private static string CatalogJson() =>
        "[{\"contractId\":\"" + LongTextWritingContractId + "\",\"name\":\"LongTextWriting\",\"description\":\"long text\"}]";

    private static string PackagesJson() =>
        "[{\"expertPackageId\":\"" + RemotePackageId + "\",\"displayName\":\"Official Long Text Writing\"," +
        "\"openAiModelIds\":[\"deepseek-v4-pro\"],\"hasSettings\":false,\"contractId\":\"" + LongTextWritingContractId + "\"}]";

    private static string SnapshotJson() =>
        "{\"expertInvocationId\":\"" + RemoteInvocationId + "\",\"status\":\"running\",\"replayed\":false," +
        "\"contractId\":\"" + LongTextWritingContractId + "\",\"expertPackageId\":\"" + RemotePackageId +
        "\",\"lastEventOrdinal\":-1,\"createdAt\":\"2026-08-26T10:00:00Z\"}";

    private static string EventSse(long ordinal, string text) =>
        "id: " + ordinal + "\ndata: {\"type\":\"event\",\"eventType\":\"chunk\",\"payload\":{\"text\":\"" + text +
        "\"}}\n\n";

    private static RemoteExpertComposition CreateComposition(FakeRemoteHttpHandler handler) =>
        ExpertComposition.CreateRemoteInvocationRunner(
            BaseOptions(DevHostOptions.RemoteExecutorName) with
            {
                RemoteEndpoint = "http://platform.test",
                RemoteBindings = new Dictionary<string, string> { [LongTextWritingContractId] = RemotePackageId }
            },
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    private static PlaygroundService CreatePlayground(RemoteExpertComposition remote) => new(
        ScriptedFakeExpertRunner.Load(null),
        local: null,
        new Dictionary<string, IExpertRunner>(StringComparer.Ordinal)
        {
            [DevHostOptions.RemoteExecutorName] = remote.Runner
        },
        new InMemoryExpertRecordingStore(),
        remote: remote);

    [Fact]
    public void ParseAcceptsRemoteModeArguments()
    {
        var options = DevHostOptions.Parse(
        [
            "--artifact", ".",
            "--experts", "remote",
            "--remote-endpoint", "http://localhost:5200",
            "--remote-token-env", "MY_PLATFORM_TOKEN",
            "--remote-binding", "thousandli.expert/long-text-writing=official-longtextwriting@0.1.0",
            "--remote-binding", "tests/other=tests/other-package"
        ]);

        Assert.Equal(DevHostOptions.RemoteExecutorName, options.Experts);
        Assert.Equal("http://localhost:5200", options.RemoteEndpoint);
        Assert.Equal("MY_PLATFORM_TOKEN", options.RemoteTokenEnvironmentVariable);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["thousandli.expert/long-text-writing"] = "official-longtextwriting@0.1.0",
                ["tests/other"] = "tests/other-package"
            },
            options.RemoteBindings);
    }

    [Fact]
    public void ParseDefaultsTheRemoteTokenEnvironmentVariable()
    {
        var options = DevHostOptions.Parse(["--artifact", ".", "--experts", "remote"]);

        Assert.Equal(DevHostOptions.DefaultRemoteTokenEnvironmentVariable, options.RemoteTokenEnvironmentVariable);
        Assert.Null(options.RemoteEndpoint);
        Assert.Empty(options.RemoteBindings);
    }

    [Fact]
    public void ParseRejectsMalformedAndDuplicateRemoteBindings()
    {
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--remote-binding", "no-separator"]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--remote-binding", "=package"]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--remote-binding", "a.b/c="]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
        [
            "--artifact", ".",
            "--remote-binding", "a.b/c=p1",
            "--remote-binding", "a.b/c=p2"
        ]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--experts", "cloud"]));
    }

    [Fact]
    public void ValidateExpertModeOptionsAcceptsMatchingModeFlags()
    {
        ExpertComposition.ValidateExpertModeOptions(BaseOptions());
        ExpertComposition.ValidateExpertModeOptions(BaseOptions(DevHostOptions.LocalExecutorName) with
        {
            ExpertArtifactDirectories = ["artifact"],
            ExpertBindings = new Dictionary<string, string> { ["a.b/c"] = "pkg" },
            GatewayEndpoint = "http://gateway",
            GatewayModels = ["model"]
        });
        ExpertComposition.ValidateExpertModeOptions(BaseOptions(DevHostOptions.RemoteExecutorName) with
        {
            RemoteEndpoint = "http://platform",
            RemoteBindings = new Dictionary<string, string> { ["a.b/c"] = "pkg" },
            RemoteTokenEnvironmentVariable = "MY_TOKEN"
        });
    }

    [Theory]
    [InlineData(DevHostOptions.FakeExecutorName, "gateway-endpoint")]
    [InlineData(DevHostOptions.RemoteExecutorName, "gateway-model")]
    [InlineData(DevHostOptions.RemoteExecutorName, "expert-artifact")]
    [InlineData(DevHostOptions.RemoteExecutorName, "expert-binding")]
    [InlineData(DevHostOptions.FakeExecutorName, "gateway-api-key-env")]
    public void ValidateExpertModeOptionsRejectsLocalOnlyFlagsUnderOtherModes(
        string mode,
        string expectedFlag)
    {
        var options = BaseOptions(mode) with
        {
            ExpertArtifactDirectories = expectedFlag == "expert-artifact" ? ["artifact"] : [],
            ExpertBindings = expectedFlag == "expert-binding"
                ? new Dictionary<string, string> { ["a.b/c"] = "pkg" }
                : [],
            GatewayEndpoint = expectedFlag == "gateway-endpoint" ? "http://gateway" : null,
            GatewayModels = expectedFlag == "gateway-model" ? ["model"] : [],
            GatewayApiKeyEnvironmentVariable = expectedFlag == "gateway-api-key-env"
                ? "MY_KEY_VAR"
                : DevHostOptions.DefaultGatewayApiKeyEnvironmentVariable
        };

        var exception = Assert.Throws<ArgumentException>(
            () => ExpertComposition.ValidateExpertModeOptions(options));
        Assert.Contains($"'--{expectedFlag}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"--experts {DevHostOptions.LocalExecutorName}", exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DevHostOptions.FakeExecutorName)]
    [InlineData(DevHostOptions.LocalExecutorName)]
    public void ValidateExpertModeOptionsRejectsRemoteOnlyFlagsUnderOtherExecutors(string mode)
    {
        var options = BaseOptions(mode) with
        {
            RemoteEndpoint = "http://platform",
            RemoteBindings = new Dictionary<string, string> { ["a.b/c"] = "pkg" },
            RemoteTokenEnvironmentVariable = "MY_TOKEN"
        };

        var exception = Assert.Throws<ArgumentException>(
            () => ExpertComposition.ValidateExpertModeOptions(options));
        Assert.Contains("'--remote-endpoint'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'--remote-binding'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'--remote-token-env'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"--experts {DevHostOptions.RemoteExecutorName}", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateExpertModeOptionsAcceptsTheDefaultRemoteTokenVariableUnderAnyMode()
    {
        ExpertComposition.ValidateExpertModeOptions(BaseOptions());
        ExpertComposition.ValidateExpertModeOptions(BaseOptions(DevHostOptions.LocalExecutorName));
    }

    [Fact]
    public void CreateRemoteInvocationRunnerRequiresAnEndpoint()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ExpertComposition.CreateRemoteInvocationRunner(
                BaseOptions(DevHostOptions.RemoteExecutorName),
                new HttpClient(new FakeRemoteHttpHandler())));
        Assert.Contains("--remote-endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposedClientSendsTheEnvironmentTokenOnEveryRequest()
    {
        const string variableName = "REMOTE_COMPOSITION_TESTS_TOKEN";
        Environment.SetEnvironmentVariable(variableName, "composition-token");
        try
        {
            var handler = new FakeRemoteHttpHandler();
            handler.EnqueueJson("[]");
            handler.EnqueueJson("[]");
            var composition = ExpertComposition.CreateRemoteInvocationRunner(
                BaseOptions(DevHostOptions.RemoteExecutorName) with
                {
                    RemoteEndpoint = "http://platform.test/base",
                    RemoteTokenEnvironmentVariable = variableName
                },
                new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

            _ = await composition.Client.ListContractsAsync(TestSupport.CancellationToken);
            _ = await composition.Client.ListExpertPackagesAsync(cancellationToken: TestSupport.CancellationToken);

            Assert.Equal(2, handler.CapturedRequests.Count);
            Assert.All(handler.CapturedRequests,
                request => Assert.Equal("Bearer composition-token", request.Authorization));
            Assert.All(handler.CapturedRequests,
                request => Assert.StartsWith("http://platform.test/base/", request.Uri, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task GetContractsMergesTheRemoteCatalogEntries()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(PackagesJson());
        var playground = CreatePlayground(CreateComposition(handler));

        var contracts = await playground.GetContractsAsync(TestSupport.CancellationToken);

        var contract = Assert.Single(contracts, entry => entry.ContractId == LongTextWritingContractId);
        Assert.Equal([DevHostOptions.RemoteExecutorName], contract.Executors);
        Assert.Equal([RemotePackageId], contract.ExpertPackageIds);
    }

    [Fact]
    public async Task GetContractsDegradesToLocalEntriesWhenThePlatformIsUnreachable()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueProblem(
            "{\"title\":\"Unauthorized\",\"status\":401}",
            System.Net.HttpStatusCode.Unauthorized);
        var playground = CreatePlayground(CreateComposition(handler));

        var contracts = await playground.GetContractsAsync(TestSupport.CancellationToken);

        Assert.Empty(contracts);
        Assert.Single(handler.CapturedRequests);
    }

    [Fact]
    public async Task InvokeStreamsRemoteEventsAndRecordsThePlatformInvocationId()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "A remote path opens.") +
            "data: {\"type\":\"completed\",\"output\":{\"text\":\"A remote path opens.\"}}\n\n");
        var playground = CreatePlayground(CreateComposition(handler));
        var sink = new RecordingSemanticSink();

        var outcome = await playground.InvokeAsync(
            new PlaygroundInvokeCommand(LongTextWritingContractId, DevHostOptions.RemoteExecutorName, TestSupport.Json("{}")),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status);
        Assert.Equal(RemoteInvocationId, outcome.Result!.InvocationId);
        Assert.Equal(RemoteInvocationId, outcome.Diagnostics.InvocationId);
        Assert.Equal("A remote path opens.", outcome.Result.Output.GetProperty("text").GetString());
        Assert.Single(sink.Events);

        var historyEntry = Assert.Single(playground.History);
        Assert.Equal(RemoteInvocationId, historyEntry.InvocationId);
        Assert.Equal(DevHostOptions.RemoteExecutorName, historyEntry.Executor);

        var start = handler.CapturedRequests.Single(request => request.Method == HttpMethod.Post);
        Assert.NotNull(start.Body);
        using var body = JsonDocument.Parse(start.Body);
        Assert.Equal(RemotePackageId, body.RootElement.GetProperty("expertPackageId").GetString());
        Assert.StartsWith("playground-", body.RootElement.GetProperty("idempotencyKey").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeHonorsAnExplicitRemotePackageChoicePerCall()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse("data: {\"type\":\"completed\",\"output\":{}}\n\n");
        var playground = CreatePlayground(CreateComposition(handler));

        var outcome = await playground.InvokeAsync(
            new PlaygroundInvokeCommand(
                LongTextWritingContractId,
                DevHostOptions.RemoteExecutorName,
                TestSupport.Json("{}"),
                ExpertPackageId: "official-longtextwriting-plus@0.1.0"),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken);

        Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status);
        var start = handler.CapturedRequests.Single(request => request.Method == HttpMethod.Post);
        Assert.NotNull(start.Body);
        using var body = JsonDocument.Parse(start.Body);
        Assert.Equal("official-longtextwriting-plus@0.1.0",
            body.RootElement.GetProperty("expertPackageId").GetString());
    }

    [Fact]
    public async Task InvokeSurfacesRemoteFailuresThroughTheErrorDiagnostics()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueProblem(
            "{\"title\":\"Unauthorized\",\"status\":401}",
            System.Net.HttpStatusCode.Unauthorized);
        var playground = CreatePlayground(CreateComposition(handler));

        var outcome = await playground.InvokeAsync(
            new PlaygroundInvokeCommand(LongTextWritingContractId, DevHostOptions.RemoteExecutorName, TestSupport.Json("{}")),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken);

        Assert.Equal(PlaygroundService.StatusError, outcome.Diagnostics.Status);
        Assert.IsType<RemoteExpertException>(outcome.Error, exactMatch: false);
        var historyEntry = Assert.Single(playground.History);
        Assert.Equal(PlaygroundService.StatusError, historyEntry.Status);
        Assert.Null(historyEntry.InvocationId);
    }
}
