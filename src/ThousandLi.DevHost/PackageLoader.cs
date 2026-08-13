using System.Reflection;
using System.Runtime.Loader;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

public sealed class LoadedGamePackage : IDisposable
{
    private readonly PackageLoadContext _loadContext;

    internal LoadedGamePackage(
        string artifactDirectory,
        GamePackageManifest manifest,
        IGameBackend backend,
        PackageLoadContext loadContext)
    {
        ArtifactDirectory = artifactDirectory;
        Manifest = manifest;
        Backend = backend;
        _loadContext = loadContext;
    }

    public string ArtifactDirectory { get; }
    public GamePackageManifest Manifest { get; }
    public IGameBackend Backend { get; }

    public void Dispose() => _loadContext.Unload();
}

internal sealed class PackageLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, typeof(IGameBackend).Assembly.GetName().Name, StringComparison.Ordinal))
            return typeof(IGameBackend).Assembly;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

public static class GamePackageLoader
{
    public static LoadedGamePackage Load(
        string artifactDirectory,
        IReadOnlyList<ExpertContractDescriptor> fakeExpertContracts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(fakeExpertContracts);
        var root = Path.GetFullPath(artifactDirectory);
        var manifest = GamePackageManifest.Load(root);
        CompatibilityValidator.Validate(
            manifest,
            new DevHostCompatibility(SdkContracts.Runtime, SdkContracts.Frontend, fakeExpertContracts));
        var assemblyPath = ResolveArtifactPath(root, manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Package entry assembly does not exist.", assemblyPath);

        var loadContext = new PackageLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var attributes = assembly.GetCustomAttributes<GamePackageEntryPointAttribute>().ToArray();
            if (attributes.Length != 1)
                throw new InvalidOperationException("Game Package Assembly must declare exactly one GamePackageEntryPoint attribute.");
            var entryType = attributes[0].EntryPointType;
            if (entryType.Assembly != assembly)
                throw new InvalidOperationException("Game entry point must be declared in the Package entry assembly.");
            if (!typeof(IGameBackend).IsAssignableFrom(entryType) || entryType.IsAbstract)
                throw new InvalidOperationException($"Game entry point '{entryType.FullName}' must be a concrete IGameBackend.");
            if (entryType.GetConstructor(Type.EmptyTypes) is not { IsPublic: true })
                throw new InvalidOperationException($"Game entry point '{entryType.FullName}' requires a public parameterless constructor.");
            var backend = (IGameBackend)Activator.CreateInstance(entryType)!;
            return new LoadedGamePackage(root, manifest, backend, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    internal static string ResolveArtifactPath(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Package path escapes the artifact directory.");
        RejectSymbolicLinks(root, candidate);
        return candidate;
    }

    private static void RejectSymbolicLinks(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Package paths must not traverse symbolic links.");
        }
    }

}
