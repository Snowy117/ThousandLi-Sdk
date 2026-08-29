using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Regression tests for the Phase 6 sample migration: the sample game declares the official
/// <c>thousandli.expert/long-text-writing</c> contract from ThousandLi.Contracts (never a private
/// copy), and the sample expert package binds that same contract through the local executor.
/// </summary>
public sealed class SampleExpertPackageTests
{
    private static string SampleGameArtifact =>
        Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");

    private static string SampleExpertArtifact =>
        Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "samples", "ThousandLi.SampleExpert", "bin", "Debug", "net10.0", "PackageArtifact");

    [Fact]
    public void SampleGameManifestRequiresTheOfficialLongTextWritingContract()
    {
        var manifest = GamePackageManifest.Load(SampleGameArtifact);

        var required = Assert.Single(manifest.Compatibility.ExpertContracts);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Id, required.Id);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Version, required.Version);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Fingerprint, required.Fingerprint);
    }

    [Fact]
    public void SampleExpertPackageBindsTheOfficialLongTextWritingContract()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);

        Assert.Equal("thousandli_sample-expert@0.1.0", package.Manifest.PackageId);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Id, package.Contract.Id);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Version, package.Contract.Version);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Fingerprint, package.Contract.Fingerprint);
        Assert.Same(typeof(AbstractLongTextWritingExpert).Assembly, package.AbstractExpertType.Assembly);
        Assert.Equal(
            "ThousandLi.SampleExpert.SampleLongTextWritingExpert",
            package.ExpertFactory().GetType().FullName);
    }

    [Fact]
    public void SampleExpertArtifactDoesNotDistributeSharedAssemblies()
    {
        var bin = Path.Combine(SampleExpertArtifact, "bin");

        Assert.True(File.Exists(Path.Combine(bin, "ThousandLi.SampleExpert.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.Contracts.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertAuthoring.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertAuthoring.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "Microsoft.Extensions.Logging.Abstractions.dll")));
    }

    [Fact]
    public async Task SampleExpertStreamsChunkEventsAndReturnsNarrationThroughTheLocalExecutor()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var executor = new LocalExpertExecutor(
            new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]),
            [package],
            null,
            new LocalExpertExecutorOptions(
                new RecordedBasicAi(
                    ["test-model"],
                    [],
                    [new RecordedRuntimeBasicAiInteraction(
                        "test-model",
                        streamEvents:
                        [
                            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "The road ")),
                            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "narrows."))
                        ])]),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer")));
        var sink = new RecordingSemanticSink();

        var result = await executor.ExecuteAsync(
            new ExpertInvocationRequest(
                AbstractLongTextWritingExpert.Descriptor,
                "advance",
                TestSupport.Json("""{"worldSettings":"A quiet valley.","playerInput":"advance"}""")),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(
            [("chunk", """{"text":"The road "}"""), ("chunk", """{"text":"narrows."}""")],
            sink.Events.Select(semanticEvent =>
                (semanticEvent.EventType, JsonSerializer.Serialize(semanticEvent.Payload))));
        Assert.Equal("The road narrows.", result.Output.GetProperty("narrative").GetString());
    }

    [Fact]
    public async Task SampleExpertReportsMissingInputPropertiesWithAnActionableDiagnostic()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var executor = new LocalExpertExecutor(
            new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]),
            [package],
            null,
            new LocalExpertExecutorOptions(
                new RecordedBasicAi(["test-model"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer")));
        var sink = new RecordingSemanticSink();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await executor.ExecuteAsync(
                new ExpertInvocationRequest(
                    AbstractLongTextWritingExpert.Descriptor,
                    "advance",
                    TestSupport.Json("""{"worldSettings":"A quiet valley"}""")),
                sink,
                TestSupport.CancellationToken));

        Assert.Contains("'playerInput'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(AbstractLongTextWritingExpert.Descriptor.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("worldSettings:string, playerInput:string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleGameAdvanceRunsPureFacadeAgainstTheSampleExpertPackageLocally()
    {
        using var expertPackage = ExpertPackageLoader.Load(SampleExpertArtifact);
        var executor = new LocalExpertExecutor(
            new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]),
            [expertPackage],
            null,
            new LocalExpertExecutorOptions(
                new RecordedBasicAi(
                    ["test-model"],
                    [],
                    [new RecordedRuntimeBasicAiInteraction(
                        "test-model",
                        streamEvents:
                        [
                            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "The road ")),
                            new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "narrows."))
                        ])]),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer")));
        using var gamePackage = GamePackageLoader.Load(
            SampleGameArtifact, [AbstractLongTextWritingExpert.Descriptor]);
        var runtime = await LocalGameRuntime.CreateAsync(
            gamePackage.Manifest.PackageId,
            gamePackage.Backend,
            new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer"),
            new InMemoryLocalSessionStore(),
            new SessionId($"sample-local-{Guid.NewGuid():N}"),
            expertFacade: new LocalExpertFacade(executor),
            cancellationToken: TestSupport.CancellationToken);

        var events = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action("""{"choice":"advance"}"""), TestSupport.CancellationToken));

        Assert.Equal(ActionRuntimeEventKind.Committed, events[^1].Kind);
        var committed = runtime.CommittedState;
        Assert.Equal(1, committed.GetProperty("turn").GetInt32());
        Assert.Equal("The road narrows.", committed.GetProperty("lastNarrative").GetString());
    }
}
