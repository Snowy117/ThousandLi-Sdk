using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class CompatibilityAndPackageLoaderTests
{
    [Fact]
    public void CompatibleManifestIsAccepted()
    {
        var manifest = GamePackageManifest.Parse(ManifestJson());
        var available = new DevHostCompatibility(
            SdkContracts.Runtime,
            SdkContracts.Frontend);

        CompatibilityValidator.Validate(manifest, available);

        Assert.Equal("thousandli_sample-game@0.1.0", manifest.PackageId);
        Assert.Equal("bin/ThousandLi.SampleGame.dll", manifest.EntryAssembly);
    }

    [Theory]
    [InlineData("/tmp/game.dll")]
    [InlineData("../game.dll")]
    [InlineData("bin\\game.dll")]
    public void UnsafeEntryAssemblyPathIsRejected(string path)
    {
        var exception = Assert.Throws<CompatibilityException>(() =>
            GamePackageManifest.Parse(ManifestJson(entryAssembly: path)));

        Assert.Contains("safe artifact-relative path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedRuntimeVersionIncludesRequiredAndAvailableVersions()
    {
        var manifest = GamePackageManifest.Parse(ManifestJson(runtimeMajor: 2));

        var exception = Assert.Throws<CompatibilityException>(() =>
            CompatibilityValidator.Validate(manifest, new DevHostCompatibility(
                SdkContracts.Runtime, SdkContracts.Frontend)));

        Assert.Contains("2.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains(SdkContracts.Runtime.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltArtifactContainsBackendFrontendManifestAndLoadsWithSharedContractsIdentity()
    {
        var artifact = Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");

        Assert.True(File.Exists(Path.Combine(artifact, "package.json")));
        Assert.True(File.Exists(Path.Combine(artifact, "frontend", "index.html")));
        Assert.True(File.Exists(Path.Combine(artifact, "bin", "ThousandLi.SampleGame.dll")));

        using var loaded = GamePackageLoader.Load(artifact);

        Assert.IsType<IGameBackend>(loaded.Backend, exactMatch: false);
        Assert.Same(typeof(IGameBackend).Assembly, loaded.Backend.GetType().Assembly
            .GetReferencedAssemblies()
            .Where(reference => reference.Name == typeof(IGameBackend).Assembly.GetName().Name)
            .Select(_ => typeof(IGameBackend).Assembly)
            .Single());
    }

    [Fact]
    public void ArtifactPathThroughSymbolicLinkIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"thousandli-loader-tests-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"thousandli-loader-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "bin"), outside);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                GamePackageLoader.ResolveArtifactPath(root, "bin/game.dll"));

            Assert.Contains("symbolic links", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ExpertLoaderRejectsGamePackageKindManifests()
    {
        var exception = Assert.Throws<CompatibilityException>(() =>
            ExpertPackageManifest.Parse(ExpertManifestJson(packageKind: "GamePackage")));

        Assert.Contains("packageKind 'ExpertPackage'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpertManifestOpenAiModelsAreOptionalAdvisoryMetadata()
    {
        var without = ExpertPackageManifest.Parse(ExpertManifestJson());
        Assert.Empty(without.OpenAiModels);
        Assert.Equal("thousandli_expert-probe@0.1.0", without.PackageId);

        var with = ExpertPackageManifest.Parse(ExpertManifestJson(openAiModels: """["m1","m2"]"""));
        Assert.Equal(["m1", "m2"], with.OpenAiModels);
    }

    [Theory]
    [InlineData("Bad Slug!")]
    [InlineData("")]
    [InlineData("UPPER")]
    public void ExpertManifestRejectsInvalidPackageNameSlugs(string packageName)
    {
        var exception = Assert.Throws<CompatibilityException>(() =>
            ExpertPackageManifest.Parse(ExpertManifestJson(packageName: packageName)));

        Assert.Contains("packageName", exception.Message, StringComparison.Ordinal);
    }

    private static string ManifestJson(string entryAssembly = "bin/ThousandLi.SampleGame.dll", int runtimeMajor = 1)
    {
        var escapedEntryAssembly = entryAssembly.Replace(@"\", @"\\", StringComparison.Ordinal);
        return $$"""
        {
          "authorId": "thousandli",
          "packageKind": "GamePackage",
          "packageName": "sample-game",
          "packageVersion": "0.1.0",
          "entryAssembly": "{{escapedEntryAssembly}}",
          "frontendRoot": "frontend",
          "compatibility": {
            "runtime": { "major": {{runtimeMajor}}, "minor": 0 },
            "frontend": { "major": 1, "minor": 0 }
          }
        }
        """;
    }

    private static string ExpertManifestJson(
        string packageKind = "ExpertPackage",
        string packageName = "expert-probe",
        string? openAiModels = null)
    {
        var models = openAiModels is null ? string.Empty : $""","openAiModels": {openAiModels}""";
        return $$"""
        {
          "authorId": "thousandli",
          "packageKind": "{{packageKind}}",
          "packageName": "{{packageName}}",
          "packageVersion": "0.1.0",
          "entryAssembly": "bin/probe.dll"
          {{models}}
        }
        """;
    }
}
