using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.SampleGame;

namespace ThousandLi.Sdk.Tests;

public sealed class CompatibilityAndPackageLoaderTests
{
    [Fact]
    public void CompatibleManifestIsAccepted()
    {
        var manifest = GamePackageManifest.Parse(ManifestJson());
        var available = new DevHostCompatibility(
            SdkContracts.Runtime,
            SdkContracts.Frontend,
            [SampleGameBackend.NarratorContract]);

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
                SdkContracts.Runtime, SdkContracts.Frontend, [SampleGameBackend.NarratorContract])));

        Assert.Contains("2.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpertFingerprintMismatchIsRejectedBeforePackageLoad()
    {
        var manifest = GamePackageManifest.Parse(ManifestJson());
        var incompatible = new ExpertContractDescriptor(
            SampleGameBackend.NarratorContract.Id,
            SampleGameBackend.NarratorContract.Version,
            "wrong");

        var exception = Assert.Throws<CompatibilityException>(() =>
            CompatibilityValidator.Validate(manifest,
                new DevHostCompatibility(SdkContracts.Runtime, SdkContracts.Frontend, [incompatible])));

        Assert.Contains("fingerprint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltArtifactContainsBackendFrontendManifestAndLoadsWithSharedContractsIdentity()
    {
        var artifact = Path.Combine(
            FindRepositoryRoot(),
            "samples", "ThousandLi.SampleGame", "bin", "Debug", "net10.0", "PackageArtifact");

        Assert.True(File.Exists(Path.Combine(artifact, "package.json")));
        Assert.True(File.Exists(Path.Combine(artifact, "frontend", "index.html")));
        Assert.True(File.Exists(Path.Combine(artifact, "bin", "ThousandLi.SampleGame.dll")));

        using var loaded = GamePackageLoader.Load(artifact, [SampleGameBackend.NarratorContract]);

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
            "frontend": { "major": 1, "minor": 0 },
            "expertContracts": [
              {
                "id": "thousandli.sample/narrator",
                "version": { "major": 1, "minor": 0 },
                "fingerprint": "sample-narrator-v1"
              }
            ]
          }
        }
        """;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThousandLi.Sdk.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the SDK repository root.");
    }
}
