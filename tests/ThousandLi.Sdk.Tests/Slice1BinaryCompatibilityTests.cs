using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class Slice1BinaryCompatibilityTests
{
    [Fact]
    public void LegacyCompiledAssemblyExecutesOnCurrentRuntime()
    {
        var assemblyPath = FixtureAssemblyPath();
        Assert.True(File.Exists(assemblyPath), $"Binary-compat fixture DLL is missing: {assemblyPath}");

        var assembly = Assembly.LoadFrom(assemblyPath);
        var driver = assembly.GetType("ThousandLi.Slice1CompatFixture.Slice1CompatDriver", throwOnError: true)
            ?? throw new InvalidOperationException("Binary-compat fixture does not contain Slice1CompatDriver.");
        var entryPoint = driver.GetMethod("RunSlice1CompatScenario", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Binary-compat fixture does not expose RunSlice1CompatScenario.");

        var lines = Assert.IsType<string[]>(entryPoint.Invoke(null, null));

        string[] expected =
        [
            "scenario:default",
            "invocation:fake-0001",
            "events:progress|progress",
            "result:fixture-complete",
            "input:42"
        ];
        Assert.Equal(expected, lines);
    }

    [Fact]
    public void FixtureBindsTheCurrentContractsAssemblyIdentity()
    {
        var currentContractsVersion = typeof(ExpertContractDescriptor).Assembly.GetName().Version;

        using var stream = File.OpenRead(FixtureAssemblyPath());
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var referencedVersion = metadataReader.AssemblyReferences
            .Select(metadataReader.GetAssemblyReference)
            .Single(reference =>
                metadataReader.GetString(reference.Name) == "ThousandLi.Contracts")
            .Version;

        Assert.Equal(currentContractsVersion, referencedVersion);
    }

    private static string FixtureAssemblyPath() =>
        Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "tests", "ThousandLi.Sdk.Tests", "Assets", "Slice1Compat", "ThousandLi.Slice1CompatFixture.dll");
}
