using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.DevHost;

/// <summary>
/// A trusted locally loaded Expert Package: its manifest, its declared contract binding, and a
/// factory for fresh (unbound) expert instances. Expert instances are created per invocation and
/// bound to an execution context by the local composition; the factory never reuses instances.
/// </summary>
public sealed class LoadedExpertPackage : IDisposable
{
    private readonly PackageLoadContext _loadContext;
    private int _disposeState;

    internal LoadedExpertPackage(
        ExpertPackageManifest manifest,
        ExpertPackageShape shape,
        Func<ExpertBase> expertFactory,
        PackageLoadContext loadContext)
    {
        Manifest = manifest;
        Shape = shape;
        ExpertFactory = expertFactory;
        _loadContext = loadContext;
    }

    public ExpertPackageManifest Manifest { get; }

    /// <summary>The entry assembly shape: contract id, abstract anchor type, concrete expert type.</summary>
    public ExpertPackageShape Shape { get; }

    /// <summary>Cached delegate over the concrete expert's public parameterless constructor.</summary>
    public Func<ExpertBase> ExpertFactory { get; }

    /// <summary>Idempotent: unload is a one-shot operation and repeat calls are no-ops.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _loadContext.Unload();
    }
}

/// <summary>
/// The shared assembly list for Expert assembly load contexts. Shared assemblies always resolve
/// to the DevHost-provided version, ignore stray copies inside the artifact, and are never
/// distributed inside Expert Package artifacts.
/// </summary>
internal static class ExpertSharedAssemblies
{
    public static IReadOnlyDictionary<string, Assembly> Base { get; } =
        new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            ["ThousandLi.Contracts"] = typeof(IGameBackend).Assembly,
            ["ThousandLi.ExpertAuthoring"] = typeof(OpenAiCompatibleBasicAi).Assembly,
            ["Microsoft.Extensions.Logging.Abstractions"] = typeof(ILogger).Assembly
        };

    public static IReadOnlyDictionary<string, Assembly> WithContractAssemblies(
        IReadOnlyDictionary<string, Assembly>? contractAssemblies)
    {
        if (contractAssemblies is null || contractAssemblies.Count == 0)
            return Base;
        var merged = new Dictionary<string, Assembly>(Base);
        foreach (var (name, assembly) in contractAssemblies)
            merged.TryAdd(name, assembly);
        return merged;
    }
}

/// <summary>
/// Fails fast when an Expert Package entry assembly references a shared assembly with a version
/// newer than (or a different major line than) the DevHost-provided one. Lower versions within the
/// same major line are allowed (backward compatible).
/// </summary>
internal static class SharedAssemblyVersionGuard
{
    public static void Validate(string entryAssemblyPath, IReadOnlyDictionary<string, Assembly> sharedAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentNullException.ThrowIfNull(sharedAssemblies);
        var problems = new List<string>();
        using (var stream = File.OpenRead(entryAssemblyPath))
        using (var peReader = new PEReader(stream))
        {
            var metadataReader = peReader.GetMetadataReader();
            foreach (var handle in metadataReader.AssemblyReferences)
            {
                var reference = metadataReader.GetAssemblyReference(handle);
                var name = metadataReader.GetString(reference.Name);
                if (!sharedAssemblies.TryGetValue(name, out var provided))
                    continue;
                var required = reference.Version;
                var available = provided.GetName().Version ?? new Version();
                if (required.Major != available.Major || required.Minor > available.Minor)
                {
                    problems.Add(
                        $"the entry assembly references shared assembly '{name}' version {required.Major}.{required.Minor}, " +
                        $"but the DevHost provides {available.Major}.{available.Minor}; " +
                        "rebuild the package against the DevHost-provided shared assemblies.");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Expert Package shared assembly version check failed:" +
                Environment.NewLine + string.Join(Environment.NewLine, problems.Order(StringComparer.Ordinal)));
        }
    }
}

public static class ExpertPackageLoader
{
    public static LoadedExpertPackage Load(
        string artifactDirectory,
        IReadOnlyDictionary<string, Assembly>? sharedContractAssemblies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        var root = Path.GetFullPath(artifactDirectory);
        var manifest = ExpertPackageManifest.Load(root);
        var assemblyPath = GamePackageLoader.ResolveArtifactPath(root, manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Package entry assembly does not exist.", assemblyPath);

        var sharedAssemblies = ExpertSharedAssemblies.WithContractAssemblies(sharedContractAssemblies);
        SharedAssemblyVersionGuard.Validate(assemblyPath, sharedAssemblies);

        var loadContext = new PackageLoadContext(assemblyPath, sharedAssemblies);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var shape = ExpertPackageShapeValidator.Validate(assembly);
            return new LoadedExpertPackage(
                manifest,
                shape,
                ExpertPackageShapeValidator.CreateFactory(shape),
                loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }
}
