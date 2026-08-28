using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using ThousandLi.BrokenContractFixtures;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;
using ThousandLi.LocalExpertNoContractFixture;
using ThousandLi.ExpertContracts;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.GameAuthoring;
using ThousandLi.UnregisteredContractFixture;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalExpertExecutorTests
{
    private static string RepositoryRoot => TestSupport.FindRepositoryRoot();

    private static string ValidArtifact =>
        Path.Combine(RepositoryRoot, "tests", "ThousandLi.LocalExpertFixture", "bin", "Debug", "net10.0", "PackageArtifact");

    private static string MultiEntryArtifact =>
        Path.Combine(RepositoryRoot, "tests", "ThousandLi.LocalExpertMultiEntryFixture", "bin", "Debug", "net10.0",
            "PackageArtifact");

    private static string WrongShapeArtifact =>
        Path.Combine(RepositoryRoot, "tests", "ThousandLi.LocalExpertWrongShapeFixture", "bin", "Debug", "net10.0",
            "PackageArtifact");

    private static string UnregisteredContractArtifact =>
        Path.Combine(RepositoryRoot, "tests", "ThousandLi.UnregisteredContractFixture", "bin", "Debug", "net10.0",
            "PackageArtifact");

    private static ExpertContractRegistry OfficialRegistry() =>
        new([typeof(AbstractNarratorExpert).Assembly]);

    private static LocalExpertExecutorOptions OptionsWith(RecordedBasicAi basicAi) => new(
        basicAi,
        new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer"));

    /// <summary>One recorded interaction whose single stream yields the deltas in order.</summary>
    private static RecordedBasicAi RecordedGateway(params string[] deltas) => new(
        ["test-model"],
        [new RecordedBasicAiInteraction(
            "test-model",
            streamEvents: [.. deltas.Select(delta => new ExpertTextDeltaEvent(delta))])]);

    /// <summary>One recorded interaction per turn; each turn's stream yields its full text.</summary>
    private static RecordedBasicAi RecordedGatewayTurns(params string[] turnTexts) => new(
        ["test-model"],
        [.. turnTexts.Select(text => new RecordedBasicAiInteraction(
            "test-model",
            streamEvents: [new ExpertTextDeltaEvent(text)]))]);

    private static JsonElement Input() => TestSupport.Json("""{"turn":1,"player":"Creator","action":{"choice":"advance"}}""");

    [Fact]
    public void LoadReadsManifestAndDeclaresRegisteredContract()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);

        Assert.Equal("thousandli_local-expert-fixture@0.1.0", package.Manifest.PackageId);
        Assert.Equal("thousandli.expert/narrator", package.Contract.Id);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Fingerprint, package.Contract.Fingerprint);
        Assert.True(typeof(AbstractNarratorExpert).IsAssignableFrom(package.AbstractExpertType));
        // The package assembly is loaded inside its own ALC, so the concrete type shares the
        // contract base (resolved from the shared ExpertContracts assembly) but is a distinct
        // runtime identity from any compile-time reference; assert by full name.
        Assert.Equal("ThousandLi.LocalExpertFixture.RecordedNarratorExpert", package.ExpertFactory().GetType().FullName);
    }

    [Fact]
    public void LoadResolvesSharedContractTypeToDevHostAssembly()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);

        Assert.Same(typeof(AbstractNarratorExpert).Assembly, package.AbstractExpertType.Assembly);
    }

    [Fact]
    public async Task ExecuteStreamsSemanticEventsAndReturnsOutput()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var executor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGateway("Hello ", "world")));
        var sink = new RecordingSemanticSink();

        var result = await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, "advance", Input()),
            sink,
            TestSupport.CancellationToken);

        Assert.StartsWith("local-", result.InvocationId, StringComparison.Ordinal);
        Assert.Equal("Hello world", result.Output.GetProperty("text").GetString());
        Assert.Equal("hi", result.Output.GetProperty("greeting").GetString());
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, semanticEvent => Assert.Equal("chunk", semanticEvent.EventType));
        Assert.Equal("Hello ", sink.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("world", sink.Events[1].Payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExecuteCreatesFreshExpertInstancePerInvocation()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var executor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGatewayTurns("a", "b")));

        var first = await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel-1"),
            new RecordingSemanticSink(), TestSupport.CancellationToken);
        var second = await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel-2"),
            new RecordingSemanticSink(), TestSupport.CancellationToken);

        Assert.NotEqual(
            first.Output.GetProperty("instanceId").GetString(),
            second.Output.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task ExecuteThroughGameRuntimeSeamMatchesFakeExecutorComposition()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var gateway = RecordedGateway("Hello world");
        var localExecutor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(gateway));
        var backend = new DelegateBackend(handleAction: async (_, context, _) =>
        {
            var input = TestSupport.Json("""{"turn":1,"player":"Creator","action":{}}""");
            var sink = new DelegateExpertSemanticEventSink(async (semanticEvent, token) =>
                await context.Frontend.WriteAsync(semanticEvent.EventType, semanticEvent.Payload, token)
                    .ConfigureAwait(false));
            var result = await context.ExpertExecutor.ExecuteAsync(
                new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, "advance", input),
                sink, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var turn = context.State.Get(new JsonPointer("/turn")).GetInt32() + 1;
            context.State.Replace(new JsonPointer("/turn"), JsonSerializer.SerializeToElement(turn));
            context.State.Add(
                new JsonPointer("/lastNarrative"),
                JsonSerializer.SerializeToElement(result.Output.GetProperty("text").GetString()));
        });

        var runtime = await TestSupport.CreateRuntimeAsync(backend, experts: localExecutor);
        var events = await TestSupport.CollectAsync(
            runtime.HandleActionAsync(TestSupport.Action(), TestContext.Current.CancellationToken));

        Assert.Contains(events, runtimeEvent => runtimeEvent.Kind == ActionRuntimeEventKind.Committed);
        Assert.DoesNotContain(events, runtimeEvent => runtimeEvent.Kind == ActionRuntimeEventKind.Aborted);
        var invocation = Assert.Single(gateway.Invocations);
        Assert.Equal("test-model", invocation.ModelId);
    }

    [Fact]
    public void UnboundMultiPackageSameContractIsRejectedWithCandidates()
    {
        using var first = ExpertPackageLoader.Load(ValidArtifact);
        using var second = ExpertPackageLoader.Load(CopyAsSecondPackage());

        var exception = Assert.Throws<LocalExpertException>(() => new LocalExpertExecutor(
            OfficialRegistry(), [first, second], null, OptionsWith(RecordedGateway("x"))));

        Assert.Contains("multiple Expert Packages without an explicit binding", exception.Message, StringComparison.Ordinal);
        Assert.Contains("thousandli_local-expert-fixture@0.1.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("thousandli_local-expert-fixture-b@0.1.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contractId=expertPackageId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitBindingDisambiguatesMultiPackageContract()
    {
        using var first = ExpertPackageLoader.Load(ValidArtifact);
        using var second = ExpertPackageLoader.Load(CopyAsSecondPackage());

        var executor = new LocalExpertExecutor(
            OfficialRegistry(),
            [first, second],
            new Dictionary<string, string> { ["thousandli.expert/narrator"] = "thousandli_local-expert-fixture-b@0.1.0" },
            OptionsWith(RecordedGateway("bound")));

        var result = await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel"),
            new RecordingSemanticSink(), TestSupport.CancellationToken);

        Assert.Equal("bound", result.Output.GetProperty("text").GetString());
    }

    [Fact]
    public void BindingToNonLoadedPackageIsRejected()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);

        var exception = Assert.Throws<LocalExpertException>(() => new LocalExpertExecutor(
            OfficialRegistry(),
            [package],
            new Dictionary<string, string> { ["thousandli.expert/narrator"] = "missing_pkg@9.9.9" },
            OptionsWith(RecordedGateway("x"))));

        Assert.Contains("'missing_pkg@9.9.9'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("which is not loaded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("thousandli_local-expert-fixture@0.1.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingToContractNoPackageServesIsRejected()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);

        var exception = Assert.Throws<LocalExpertException>(() => new LocalExpertExecutor(
            OfficialRegistry(),
            [package],
            new Dictionary<string, string> { ["tests/missing-contract"] = "thousandli_local-expert-fixture@0.1.0" },
            OptionsWith(RecordedGateway("x"))));

        Assert.Contains("tests/missing-contract", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no loaded Expert Package serves", exception.Message, StringComparison.Ordinal);
        Assert.Contains("thousandli.expert/narrator", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageForUnregisteredContractIsRejected()
    {
        using var package = ExpertPackageLoader.Load(UnregisteredContractArtifact);

        var exception = Assert.Throws<LocalExpertException>(() => new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGateway("x"))));

        Assert.Contains("tests.unregistered/narrator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("which is not registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroEntryPointsAreRejected()
    {
        var artifact = BuildArtifactFromAssembly(
            typeof(FixtureAssembly).Assembly.Location,
            "no-entry-fixture");

        var exception = Assert.Throws<InvalidOperationException>(() => ExpertPackageLoader.Load(artifact));

        Assert.Contains("exactly one ExpertPackageEntryPoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleEntryPointsAreRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ExpertPackageLoader.Load(MultiEntryArtifact));

        Assert.Contains("exactly one ExpertPackageEntryPoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declares 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongConcreteBaseShapeIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ExpertPackageLoader.Load(WrongShapeArtifact));

        Assert.Contains("must be a concrete class inheriting", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NotAnExpert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryWithoutContractAttributeIsRejected()
    {
        var artifact = BuildArtifactFromAssembly(
            typeof(LocalConcreteExpert).Assembly.Location,
            "no-contract-fixture");

        var exception = Assert.Throws<InvalidOperationException>(() => ExpertPackageLoader.Load(artifact));

        Assert.Contains("[ExpertContract]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAssemblyVersionHigherThanDevHostIsRejectedWithBothVersions()
    {
        var artifact = Directory.CreateTempSubdirectory("thousandli-shared-version-");
        try
        {
            Directory.CreateDirectory(Path.Combine(artifact.FullName, "bin"));
            File.WriteAllText(Path.Combine(artifact.FullName, "package.json"), """
            {
              "authorId": "thousandli",
              "packageKind": "ExpertPackage",
              "packageName": "version-probe",
              "packageVersion": "0.1.0",
              "entryAssembly": "bin/VersionProbe.dll"
            }
            """);
            File.WriteAllBytes(
                Path.Combine(artifact.FullName, "bin", "VersionProbe.dll"),
                SyntheticAssembly("VersionProbe", ("ThousandLi.Contracts", new Version(2, 5, 0, 0))));

            var exception = Assert.Throws<InvalidOperationException>(() => ExpertPackageLoader.Load(artifact.FullName));

            Assert.Contains("ThousandLi.Contracts", exception.Message, StringComparison.Ordinal);
            Assert.Contains("2.5", exception.Message, StringComparison.Ordinal);
            var devHostVersion = typeof(ExpertInvocationRequest).Assembly.GetName().Version ?? new Version();
            Assert.Contains(
                $"{devHostVersion.Major}.{devHostVersion.Minor}",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            artifact.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringLocalExecutionPropagates()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var executor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGateway("never-reached")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel"),
            new RecordingSemanticSink(),
            cancelled.Token));
    }

    [Fact]
    public async Task PlaygroundRecordsDiagnosticsWithCorrelationDurationAndStatus()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var localExecutor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGateway("once")));
        var playground = new PlaygroundService(
            ScriptedFakeExpertExecutor.Load(null), localExecutor, OfficialRegistry(), new InMemoryExpertRecordingStore());

        var committed = await playground.InvokeAsync(
            new PlaygroundInvokeCommand(
                "thousandli.expert/narrator", DevHostOptions.LocalExecutorName, Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken);
        var failed = await playground.InvokeAsync(
            new PlaygroundInvokeCommand(
                "thousandli.expert/narrator", DevHostOptions.LocalExecutorName, Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken);

        Assert.Equal(PlaygroundService.StatusCommitted, committed.Diagnostics.Status);
        Assert.Matches("""playground-\d{8}""", committed.Diagnostics.ChannelKey);
        Assert.NotNull(committed.Diagnostics.InvocationId);
        Assert.True(committed.Diagnostics.DurationMs >= 0);
        Assert.Equal(PlaygroundService.StatusError, failed.Diagnostics.Status);
        Assert.NotNull(failed.Diagnostics.Error);
        var history = playground.History;
        Assert.Equal(2, history.Count);
        Assert.Equal(committed.Diagnostics.ChannelKey, history[0].ChannelKey);
        Assert.Equal("thousandli.expert/narrator", history[0].ContractId);
    }

    [Fact]
    public void ValidateExecutorOptionsRejectsLocalArgumentsWithFakeExecutor()
    {
        var options = new DevHostOptions
        {
            ArtifactDirectory = ".",
            WorkspaceId = "default",
            FrontendUrl = "/game/",
            SessionId = "session",
            GatewayEndpoint = "http://localhost:1234/v1/chat/completions"
        };

        var exception = Assert.Throws<ArgumentException>(() => ExpertComposition.ValidateExecutorOptions(options));

        Assert.Contains("--gateway-endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--expert-executor local", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAcceptsRepeatedExpertArgumentsAndBindings()
    {
        var options = DevHostOptions.Parse(
        [
            "--artifact", ".",
            "--expert-executor", "local",
            "--expert-artifact", "/tmp/expert-a",
            "--expert-artifact", "/tmp/expert-b",
            "--contract-assembly", "/tmp/contracts.dll",
            "--expert-binding", "a.b/c=/tmp-id-a",
            "--expert-binding", "d.e/f=ide-b",
            "--gateway-endpoint", "http://localhost:8080/v1/chat/completions",
            "--gateway-model", "m1",
            "--gateway-model", "m2",
            "--gateway-api-key-env", "MY_KEY"
        ]);

        Assert.Equal(DevHostOptions.LocalExecutorName, options.ExpertExecutor);
        Assert.Equal(["/tmp/expert-a", "/tmp/expert-b"], options.ExpertArtifactDirectories);
        Assert.Equal(["/tmp/contracts.dll"], options.ContractAssemblies);
        Assert.Equal(
            new Dictionary<string, string> { ["a.b/c"] = "/tmp-id-a", ["d.e/f"] = "ide-b" },
            options.ExpertBindings);
        Assert.Equal(["m1", "m2"], options.GatewayModels);
        Assert.Equal("MY_KEY", options.GatewayApiKeyEnvironmentVariable);
    }

    [Fact]
    public void SharedArtifactListExcludesSharedDllsFromExpertArtifacts()
    {
        var bin = Path.Combine(ValidArtifact, "bin");

        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.Contracts.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertContracts.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertAuthoring.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "ThousandLi.LocalExpertFixture.dll")));
    }

    [Fact]
    public void ExplicitBindingToLoadedPackageServingDifferentContractIsRejectedAtConstruction()
    {
        using var narrator = ExpertPackageLoader.Load(ValidArtifact);
        using var other = ExpertPackageLoader.Load(UnregisteredContractArtifact);
        var registry = new ExpertContractRegistry(
            [typeof(AbstractNarratorExpert).Assembly, typeof(UnregisteredNarratorExpert).Assembly]);

        var exception = Assert.Throws<LocalExpertException>(() => new LocalExpertExecutor(
            registry,
            [narrator, other],
            new Dictionary<string, string> { ["tests.unregistered/narrator"] = narrator.Manifest.PackageId },
            OptionsWith(RecordedGateway("x"))));

        Assert.Contains("'tests.unregistered/narrator'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(narrator.Manifest.PackageId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'thousandli.expert/narrator'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageOverrideToNonLoadedPackageIsRejectedWithLoadedList()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var executor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(RecordedGateway("x")));

        var exception = await Assert.ThrowsAsync<LocalExpertException>(async () => await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel"),
            new RecordingSemanticSink(),
            expertPackageIdOverride: "missing_pkg@9.9.9",
            cancellationToken: TestSupport.CancellationToken));

        Assert.Contains("'missing_pkg@9.9.9'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not loaded", exception.Message, StringComparison.Ordinal);
        Assert.Contains(package.Manifest.PackageId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageOverrideServingDifferentContractIsRejected()
    {
        using var narrator = ExpertPackageLoader.Load(ValidArtifact);
        using var other = ExpertPackageLoader.Load(UnregisteredContractArtifact);
        var registry = new ExpertContractRegistry(
            [typeof(AbstractNarratorExpert).Assembly, typeof(UnregisteredNarratorExpert).Assembly]);
        var executor = new LocalExpertExecutor(registry, [narrator, other], null, OptionsWith(RecordedGateway("x")));

        var exception = await Assert.ThrowsAsync<LocalExpertException>(async () => await executor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel"),
            new RecordingSemanticSink(),
            expertPackageIdOverride: other.Manifest.PackageId,
            cancellationToken: TestSupport.CancellationToken));

        Assert.Contains(other.Manifest.PackageId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'tests.unregistered/narrator'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'thousandli.expert/narrator'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsOverrideFileWiresThroughExecutorOptionsToTheExpert()
    {
        var directory = Directory.CreateTempSubdirectory("thousandli-settings-override-");
        try
        {
            var overridePath = Path.Combine(directory.FullName, "expert-settings.json");
            await File.WriteAllTextAsync(overridePath, """{"Greeting":"hello"}""", TestSupport.CancellationToken);
            using var package = ExpertPackageLoader.Load(ValidArtifact);
            var options = new LocalExpertExecutorOptions(
                RecordedGateway("Hey"),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer"))
            {
                SettingsOverrideFile = new FileInfo(overridePath)
            };
            var executor = new LocalExpertExecutor(OfficialRegistry(), [package], null, options);

            var result = await executor.ExecuteAsync(
                new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel"),
                new RecordingSemanticSink(), TestSupport.CancellationToken);

            Assert.Equal("hello", result.Output.GetProperty("greeting").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PlaygroundConcurrentInvocationsKeepDistinctHistoryEntriesAndMergedContracts()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        const int count = 8;
        var gateway = new RecordedBasicAi(
            ["test-model"],
            [.. Enumerable.Range(0, count).Select(_ => new RecordedBasicAiInteraction(
                "test-model",
                streamEvents: [new ExpertTextDeltaEvent("parallel")]))]);
        var localExecutor = new LocalExpertExecutor(
            OfficialRegistry(), [package], null, OptionsWith(gateway));
        var playground = new PlaygroundService(
            ScriptedFakeExpertExecutor.Load(null), localExecutor, OfficialRegistry(), new InMemoryExpertRecordingStore());

        var outcomes = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => playground.InvokeAsync(
            new PlaygroundInvokeCommand("thousandli.expert/narrator", DevHostOptions.LocalExecutorName, Input()),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken)));

        Assert.All(outcomes, outcome =>
            Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status));
        var history = playground.History;
        Assert.Equal(count, history.Count);
        Assert.Equal(count, history.Select(entry => entry.ChannelKey).Distinct(StringComparer.Ordinal).Count());

        var contracts = await playground.GetContractsAsync(TestSupport.CancellationToken);
        var narrator = Assert.Single(contracts, contract => contract.ContractId == "thousandli.expert/narrator");
        Assert.Contains(DevHostOptions.LocalExecutorName, narrator.Executors);
        Assert.Equal([package.Manifest.PackageId], narrator.ExpertPackageIds);
    }

    [Fact]
    public async Task PlaygroundInvokeRejectsUnavailableLocalUnknownExecutorsAndScenariolessFake()
    {
        var playground = new PlaygroundService(
            ScriptedFakeExpertExecutor.Load(null), null, OfficialRegistry(), new InMemoryExpertRecordingStore());

        var unavailableLocal = await Assert.ThrowsAsync<ArgumentException>(() => playground.InvokeAsync(
            new PlaygroundInvokeCommand("thousandli.expert/narrator", DevHostOptions.LocalExecutorName, Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken));
        Assert.Contains("--expert-executor local", unavailableLocal.Message, StringComparison.Ordinal);

        var unavailableRemote = await Assert.ThrowsAsync<ArgumentException>(() => playground.InvokeAsync(
            new PlaygroundInvokeCommand("thousandli.expert/narrator", DevHostOptions.RemoteExecutorName, Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken));
        Assert.Contains("--expert-executor remote", unavailableRemote.Message, StringComparison.Ordinal);

        var unknownExecutor = await Assert.ThrowsAsync<ArgumentException>(() => playground.InvokeAsync(
            new PlaygroundInvokeCommand("thousandli.expert/narrator", "cloud", Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken));
        Assert.Contains("'cloud'", unknownExecutor.Message, StringComparison.Ordinal);

        var scenariolessFake = await Assert.ThrowsAsync<ArgumentException>(() => playground.InvokeAsync(
            new PlaygroundInvokeCommand("anything", DevHostOptions.FakeExecutorName, Input()),
            new RecordingSemanticSink(), TestSupport.CancellationToken));
        Assert.Contains("scenario id", scenariolessFake.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeExecutorRejectsInvocationWithoutScenarioKey()
    {
        var executor = ScriptedFakeExpertExecutor.Load(null);
        var request = new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, null, Input(), "channel");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await executor.ExecuteAsync(
            request, new RecordingSemanticSink(), TestSupport.CancellationToken));

        Assert.Contains("requires a scenario key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedExpertPackageDisposeIsIdempotent()
    {
        var package = ExpertPackageLoader.Load(ValidArtifact);
        package.Dispose();
        package.Dispose();
    }

    [Fact]
    public void ParseRejectsMalformedAndDuplicateExpertBindings()
    {
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--expert-binding", "no-separator"]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--expert-binding", "=package"]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--expert-binding", "a.b/c="]));
        Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", ".", "--expert-binding", "a.b/c=p1", "--expert-binding", "a.b/c=p2"]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("THOUSANDLI_GATEWAY_API_KEY", false)]
    [InlineData("MY_CUSTOM_KEY_VAR", true)]
    public void ValidateExecutorOptionsFlagsOnlyExplicitGatewayApiKeyVariables(string? environmentVariable, bool shouldReject)
    {
        var options = new DevHostOptions
        {
            ArtifactDirectory = ".",
            WorkspaceId = "default",
            FrontendUrl = "/game/",
            SessionId = "session",
            GatewayApiKeyEnvironmentVariable = environmentVariable!
        };

        if (!shouldReject)
        {
            ExpertComposition.ValidateExecutorOptions(options);
            return;
        }

        var exception = Assert.Throws<ArgumentException>(
            () => ExpertComposition.ValidateExecutorOptions(options));
        Assert.Contains("--gateway-api-key-env", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAssemblyVersionGuardAcceptsReferenceAtProvidedVersion()
    {
        var provided = typeof(IGameBackend).Assembly.GetName().Version!;
        RunWithSyntheticAssembly(
            "VersionProbeExact",
            [new Version(provided.Major, provided.Minor, 0, 0)],
            path => SharedAssemblyVersionGuard.Validate(path, ExpertSharedAssemblies.Base));
    }

    [Fact]
    public void SharedAssemblyVersionGuardAcceptsLowerMinorWithinSameMajor()
    {
        var provided = typeof(IGameBackend).Assembly.GetName().Version!;
        Assert.True(provided.Minor > 0, "Test expects the shared Contracts assembly to be at minor >= 1.");
        RunWithSyntheticAssembly(
            "VersionProbeLower",
            [new Version(provided.Major, provided.Minor - 1, 0, 0)],
            path => SharedAssemblyVersionGuard.Validate(path, ExpertSharedAssemblies.Base));
    }

    [Fact]
    public void SharedAssemblyVersionGuardRejectsHigherMinorWithinSameMajorWithBothVersions()
    {
        var provided = typeof(IGameBackend).Assembly.GetName().Version!;
        RunWithSyntheticAssembly(
            "VersionProbeHigherMinor",
            [new Version(provided.Major, provided.Minor + 1, 0, 0)],
            path =>
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => SharedAssemblyVersionGuard.Validate(path, ExpertSharedAssemblies.Base));
                Assert.Contains("ThousandLi.Contracts", exception.Message, StringComparison.Ordinal);
                Assert.Contains($"{provided.Major}.{provided.Minor + 1}", exception.Message, StringComparison.Ordinal);
                Assert.Contains($"{provided.Major}.{provided.Minor}", exception.Message, StringComparison.Ordinal);
            });
    }

    private static void RunWithSyntheticAssembly(
        string assemblyName,
        Version[] referenceVersions,
        Action<string> assertion)
    {
        var directory = Directory.CreateTempSubdirectory($"thousandli-version-guard-{assemblyName}-");
        try
        {
            var references = referenceVersions.Select(version => ("ThousandLi.Contracts", version)).ToArray();
            var path = Path.Combine(directory.FullName, assemblyName + ".dll");
            File.WriteAllBytes(path, SyntheticAssembly(assemblyName, references));
            assertion(path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CopyAsSecondPackage()
    {
        var target = Directory.CreateTempSubdirectory("thousandli-expert-copy-");
        foreach (var sourceDirectory in Directory.GetDirectories(ValidArtifact, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(sourceDirectory.Replace(ValidArtifact, target.FullName));
        foreach (var sourceFile in Directory.GetFiles(ValidArtifact, "*", SearchOption.AllDirectories))
            File.Copy(sourceFile, sourceFile.Replace(ValidArtifact, target.FullName));
        var manifestPath = Path.Combine(target.FullName, "package.json");
        var manifest = File.ReadAllText(manifestPath).Replace(
            "\"local-expert-fixture\"", "\"local-expert-fixture-b\"", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        return target.FullName;
    }

    private static string BuildArtifactFromAssembly(string assemblyPath, string packageName)
    {
        var target = Directory.CreateTempSubdirectory($"thousandli-{packageName}-");
        Directory.CreateDirectory(Path.Combine(target.FullName, "bin"));
        File.Copy(assemblyPath, Path.Combine(target.FullName, "bin", Path.GetFileName(assemblyPath)));
        File.WriteAllText(Path.Combine(target.FullName, "package.json"), $$"""
        {
          "authorId": "thousandli",
          "packageKind": "ExpertPackage",
          "packageName": "{{packageName}}",
          "packageVersion": "0.1.0",
          "entryAssembly": "bin/{{Path.GetFileName(assemblyPath)}}"
        }
        """);
        return target.FullName;
    }

    private static byte[] SyntheticAssembly(string assemblyName, params (string Reference, Version Version)[] references)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(assemblyName + ".dll"),
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);
        metadata.AddAssembly(
            name: metadata.GetOrAddString(assemblyName),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.None);
        foreach (var (reference, version) in references)
        {
            metadata.AddAssemblyReference(
                name: metadata.GetOrAddString(reference),
                version: version,
                culture: default,
                publicKeyOrToken: default,
                hashValue: default,
                flags: 0);
        }

        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }
}
