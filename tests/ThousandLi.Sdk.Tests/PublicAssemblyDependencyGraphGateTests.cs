using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ThousandLi.Sdk.Tests;

public sealed class PublicAssemblyDependencyGraphGateTests
{
    [Fact]
    public void DirectForbiddenReferenceIsReported()
    {
        var violations = CollectViolationsInTempDirectory(
            ["ThousandLi.GateProbe.Root"],
            ("ThousandLi.GateProbe.Root", ["ThousandLi.Host"]));

        var violation = Assert.Single(violations);
        Assert.Contains("ThousandLi.GateProbe.Root", violation, StringComparison.Ordinal);
        Assert.Contains("forbidden assembly 'ThousandLi.Host'", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TransitiveForbiddenReferenceIsReported()
    {
        var violations = CollectViolationsInTempDirectory(
            ["ThousandLi.GateProbe.Root"],
            ("ThousandLi.GateProbe.Root", ["ThousandLi.GateProbe.Middle"]),
            ("ThousandLi.GateProbe.Middle", ["Microsoft.EntityFrameworkCore"]));

        var violation = Assert.Single(violations);
        Assert.Contains("ThousandLi.GateProbe.Middle", violation, StringComparison.Ordinal);
        Assert.Contains("forbidden assembly 'Microsoft.EntityFrameworkCore'", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownThirdPartyReferenceIsReported()
    {
        var violations = CollectViolationsInTempDirectory(
            ["ThousandLi.GateProbe.Root"],
            ("ThousandLi.GateProbe.Root", ["Newtonsoft.Json"]));

        var violation = Assert.Single(violations);
        Assert.Contains("Newtonsoft.Json", violation, StringComparison.Ordinal);
        Assert.Contains("neither a framework assembly nor part of the SDK assembly set", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkReferencesAreAllowed()
    {
        var violations = CollectViolationsInTempDirectory(
            ["ThousandLi.GateProbe.Root"],
            ("ThousandLi.GateProbe.Root", ["System.Runtime", "Microsoft.Extensions.Logging.Abstractions"]));

        Assert.Empty(violations);
    }

    [Fact]
    public void ReferenceCyclesTerminate()
    {
        var violations = CollectViolationsInTempDirectory(
            ["ThousandLi.GateProbe.Root"],
            ("ThousandLi.GateProbe.Root", ["ThousandLi.GateProbe.Middle"]),
            ("ThousandLi.GateProbe.Middle", ["ThousandLi.GateProbe.Root"]));

        Assert.Empty(violations);
    }

    [Fact]
    public void MissingRootAssemblyFailsFast()
    {
        var exception = Assert.Throws<FileNotFoundException>(() =>
            PublicAssemblyDependencyGraphTests.CollectReferenceClosureViolations(
                ["ThousandLi.GateProbe.Absent"],
                _ => null));

        Assert.Contains("ThousandLi.GateProbe.Absent", exception.Message, StringComparison.Ordinal);
    }

    private static List<string> CollectViolationsInTempDirectory(
        string[] rootAssemblyNames,
        params (string AssemblyName, string[] References)[] assemblies)
    {
        var directory = Directory.CreateTempSubdirectory("thousandli-gate-probe-");
        try
        {
            var paths = assemblies.ToDictionary(
                item => item.AssemblyName,
                item => WriteSyntheticAssembly(directory, item.AssemblyName, item.References),
                StringComparer.Ordinal);
            return PublicAssemblyDependencyGraphTests.CollectReferenceClosureViolations(
                rootAssemblyNames,
                name => paths.TryGetValue(name, out var path) ? path : null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string WriteSyntheticAssembly(
        DirectoryInfo directory,
        string assemblyName,
        params string[] referencedAssemblies)
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
        foreach (var referencedAssembly in referencedAssemblies)
        {
            metadata.AddAssemblyReference(
                name: metadata.GetOrAddString(referencedAssembly),
                version: new Version(1, 0, 0, 0),
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
        var path = Path.Combine(directory.FullName, assemblyName + ".dll");
        File.WriteAllBytes(path, blob.ToArray());
        return path;
    }
}
