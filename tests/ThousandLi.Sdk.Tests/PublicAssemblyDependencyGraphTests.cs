using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ThousandLi.Sdk.Tests;

public sealed class PublicAssemblyDependencyGraphTests
{
    private static readonly string[] CheckedPublicAssemblies =
    [
        "ThousandLi.Contracts",
        "ThousandLi.GameAuthoring",
        "ThousandLi.GameHelper",
        "ThousandLi.Testing",
        "ThousandLi.DevHost",
        "ThousandLi.ExpertContracts",
        "ThousandLi.ExpertAuthoring",
        "ThousandLi.RemoteExperts"
    ];

    /// <summary>
    /// Deliberate public third-party dependencies. GameHelper ships Castle.Core inside game Package
    /// Artifacts for typed session-state proxy write-back; it is not part of the SDK runtime set.
    /// </summary>
    private static readonly string[] AllowedThirdPartyAssemblies = ["Castle.Core"];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "ThousandLi.Host",
        "ThousandLi.AccessControl",
        "ThousandLi.AuditLog",
        "ThousandLi.Observability",
        "ThousandLi.Official",
        "ThousandLi.Identity",
        "Microsoft.EntityFrameworkCore"
    ];

    private static readonly string[] FrameworkAssemblyPrefixes =
    [
        "System",
        "Microsoft",
        "mscorlib",
        "netstandard",
        "WindowsBase"
    ];

    [Fact]
    public void PublicAssemblyReferenceClosureContainsNoForbiddenAssemblies()
    {
        var violations = CollectReferenceClosureViolations(CheckedPublicAssemblies, LocateAssembly);

        Assert.True(violations.Count == 0,
            "Public assembly dependency graph violations:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryPublicSourceProjectIsCoveredByTheGate()
    {
        var sourceDirectory = Path.Combine(TestSupport.FindRepositoryRoot(), "src");
        var projectNames = Directory.EnumerateDirectories(sourceDirectory)
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(projectNames.Length > 0, $"No public source projects found under '{sourceDirectory}'.");
        var uncovered = projectNames.Where(name => !CheckedPublicAssemblies.Contains(name, StringComparer.Ordinal)).ToArray();
        Assert.True(uncovered.Length == 0,
            "Public source projects missing from the dependency gate roots: " +
            string.Join(", ", uncovered));
    }

    internal static List<string> CollectReferenceClosureViolations(
        IEnumerable<string> rootAssemblyNames,
        Func<string, string?> locateAssembly)
    {
        var violations = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(rootAssemblyNames);
        while (pending.Count > 0)
        {
            var assemblyName = pending.Dequeue();
            if (!visited.Add(assemblyName)) continue;
            var assemblyPath = locateAssembly(assemblyName)
                ?? throw new FileNotFoundException(
                    $"Checked public assembly '{assemblyName}' was not found in the build outputs.");
            foreach (var referenceName in ReadAssemblyReferences(assemblyPath))
            {
                if (HasPrefix(referenceName, ForbiddenAssemblyPrefixes))
                {
                    violations.Add($"{assemblyName} references forbidden assembly '{referenceName}'.");
                    continue;
                }

                if (HasPrefix(referenceName, FrameworkAssemblyPrefixes))
                    continue;

                if (AllowedThirdPartyAssemblies.Contains(referenceName, StringComparer.Ordinal))
                    continue;

                if (referenceName.StartsWith("ThousandLi.", StringComparison.Ordinal) &&
                    locateAssembly(referenceName) is not null)
                {
                    pending.Enqueue(referenceName);
                    continue;
                }

                violations.Add(
                    $"{assemblyName} references '{referenceName}', " +
                    "which is neither a framework assembly nor part of the SDK assembly set.");
            }
        }
        return violations;
    }

    private static bool HasPrefix(string value, string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));

    private static string? LocateAssembly(string assemblyName) =>
        ProbeDirectories()
            .Select(directory => Path.Combine(directory, assemblyName + ".dll"))
            .FirstOrDefault(File.Exists);

    private static IEnumerable<string> ProbeDirectories()
    {
        yield return AppContext.BaseDirectory;
        var sourceDirectory = Path.Combine(TestSupport.FindRepositoryRoot(), "src");
        if (!Directory.Exists(sourceDirectory)) yield break;
        foreach (var projectDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var binDirectory = Path.Combine(projectDirectory, "bin");
            if (!Directory.Exists(binDirectory)) continue;
            foreach (var configurationDirectory in Directory.EnumerateDirectories(binDirectory))
            {
                foreach (var frameworkDirectory in Directory.EnumerateDirectories(configurationDirectory))
                    yield return frameworkDirectory;
            }
        }
    }

    private static List<string> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var names = new List<string>();
        names.AddRange(metadataReader.AssemblyReferences.Select(handle =>
            metadataReader.GetString(metadataReader.GetAssemblyReference(handle).Name)));
        return names;
    }
}
