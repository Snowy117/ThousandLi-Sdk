using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Regression tests for the sample migration: the sample game and the sample expert package bind
/// the official <c>thousandli.expert/long-text-writing</c> contract through the local composition.
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
    public void SampleGameManifestCarriesRuntimeAndFrontendCompatibility()
    {
        var manifest = GamePackageManifest.Load(SampleGameArtifact);

        Assert.Equal("thousandli_sample-game@0.1.0", manifest.PackageId);
        Assert.Equal(new PackageVersion(1, 0), manifest.Compatibility.Runtime);
        Assert.Equal(new PackageVersion(1, 0), manifest.Compatibility.Frontend);
    }

    [Fact]
    public void SampleExpertPackageBindsTheOfficialLongTextWritingContract()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);

        Assert.Equal("thousandli_sample-expert@0.1.0", package.Manifest.PackageId);
        Assert.Equal(AbstractLongTextWritingExpert.ContractId, package.Shape.ContractId);
        Assert.Same(typeof(AbstractLongTextWritingExpert).Assembly, package.Shape.AbstractExpertType.Assembly);
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
        Assert.False(File.Exists(Path.Combine(bin, "Microsoft.Extensions.Logging.Abstractions.dll")));
    }

    [Fact]
    public async Task SampleExpertStreamsChunkEventsAndReturnsNarrationThroughTheLocalComposition()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var composition = new LocalExpertComposition(
            [package],
            null,
            new LocalExpertCompositionOptions(
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

        var result = await composition.ExecuteAsync(
            new ExpertInvocationRequest(
                AbstractLongTextWritingExpert.ContractId,
                "advance",
                TestSupport.Json("""{"worldSettings":"A quiet valley.","playerInput":"advance"}""")),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(
            [("chunk", """{"text":"The road "}"""), ("chunk", """{"text":"narrows."}""")],
            sink.Events.Select(semanticEvent =>
                (semanticEvent.EventType, JsonSerializer.Serialize(semanticEvent.Payload))));
        Assert.Equal("The road narrows.", result.Output.GetProperty("primary").GetString());
    }

    [Fact]
    public async Task SampleExpertReportsMissingInputPropertiesWithAnActionableDiagnostic()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var composition = new LocalExpertComposition(
            [package],
            null,
            new LocalExpertCompositionOptions(
                new RecordedBasicAi(["test-model"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer")));
        var sink = new RecordingSemanticSink();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await composition.ExecuteAsync(
                new ExpertInvocationRequest(
                    AbstractLongTextWritingExpert.ContractId,
                    "advance",
                    TestSupport.Json("""{"worldSettings":"A quiet valley"}""")),
                sink,
                TestSupport.CancellationToken));

        Assert.Contains("playerInput", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleGameAdvanceRunsPureFacadeAgainstTheSampleExpertPackageLocally()
    {
        using var expertPackage = ExpertPackageLoader.Load(SampleExpertArtifact);
        var composition = new LocalExpertComposition(
            [expertPackage],
            null,
            new LocalExpertCompositionOptions(
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
        using var gamePackage = GamePackageLoader.Load(SampleGameArtifact);
        var runtime = await LocalGameRuntime.CreateAsync(
            gamePackage.Manifest.PackageId,
            gamePackage.Backend,
            new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer"),
            new InMemoryLocalSessionStore(),
            new SessionId($"sample-local-{Guid.NewGuid():N}"),
            expertFacade: new LocalExpertFacade(composition),
            cancellationToken: TestSupport.CancellationToken);

        var events = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action("""{"choice":"advance"}"""), TestSupport.CancellationToken));

        Assert.Equal(ActionRuntimeEventKind.Committed, events[^1].Kind);
        var committed = runtime.CommittedState;
        Assert.Equal(1, committed.GetProperty("turn").GetInt32());
        Assert.Equal("The road narrows.", committed.GetProperty("lastNarrative").GetString());
    }
}
