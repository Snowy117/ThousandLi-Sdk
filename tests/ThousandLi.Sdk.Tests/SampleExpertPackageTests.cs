using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts;
using ThousandLi.ExpertContracts.Narration;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Regression tests for the Phase 6 sample migration: the sample game declares the official
/// <c>thousandli.expert/narrator</c> contract from ThousandLi.ExpertContracts (never a private
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
    public void SampleGameManifestRequiresTheOfficialNarratorContract()
    {
        var manifest = GamePackageManifest.Load(SampleGameArtifact);

        var required = Assert.Single(manifest.Compatibility.ExpertContracts);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Id, required.Id);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Version, required.Version);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Fingerprint, required.Fingerprint);
    }

    [Fact]
    public void SampleExpertPackageBindsTheOfficialNarratorContract()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);

        Assert.Equal("thousandli_sample-expert@0.1.0", package.Manifest.PackageId);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Id, package.Contract.Id);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Version, package.Contract.Version);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Fingerprint, package.Contract.Fingerprint);
        Assert.Same(typeof(AbstractNarratorExpert).Assembly, package.AbstractExpertType.Assembly);
        Assert.Equal(
            "ThousandLi.SampleExpert.SampleNarratorExpert",
            package.ExpertFactory().GetType().FullName);
    }

    [Fact]
    public void SampleExpertArtifactDoesNotDistributeSharedAssemblies()
    {
        var bin = Path.Combine(SampleExpertArtifact, "bin");

        Assert.True(File.Exists(Path.Combine(bin, "ThousandLi.SampleExpert.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.Contracts.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertContracts.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ThousandLi.ExpertAuthoring.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "Microsoft.Extensions.Logging.Abstractions.dll")));
    }

    [Fact]
    public async Task SampleExpertStreamsChunkEventsAndReturnsNarrationThroughTheLocalExecutor()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var executor = new LocalExpertExecutor(
            new ExpertContractRegistry([typeof(AbstractNarratorExpert).Assembly]),
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
                AbstractNarratorExpert.Descriptor,
                "advance",
                TestSupport.Json("""{"turn":1,"player":"Creator","action":{"choice":"advance"}}""")),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(
            [("chunk", """{"text":"The road "}"""), ("chunk", """{"text":"narrows."}""")],
            sink.Events.Select(semanticEvent =>
                (semanticEvent.EventType, JsonSerializer.Serialize(semanticEvent.Payload))));
        Assert.Equal("The road narrows.", result.Output.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SampleExpertReportsMissingInputPropertiesWithAnActionableDiagnostic()
    {
        using var package = ExpertPackageLoader.Load(SampleExpertArtifact);
        var executor = new LocalExpertExecutor(
            new ExpertContractRegistry([typeof(AbstractNarratorExpert).Assembly]),
            [package],
            null,
            new LocalExpertExecutorOptions(
                new RecordedBasicAi(["test-model"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer")));
        var sink = new RecordingSemanticSink();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await executor.ExecuteAsync(
                new ExpertInvocationRequest(
                    AbstractNarratorExpert.Descriptor,
                    "advance",
                    TestSupport.Json("""{"turn":1,"player":"Creator"}""")),
                sink,
                TestSupport.CancellationToken));

        Assert.Contains("'action'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(AbstractNarratorExpert.Descriptor.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("turn:int, player:string, action:*", exception.Message, StringComparison.Ordinal);
    }
}
