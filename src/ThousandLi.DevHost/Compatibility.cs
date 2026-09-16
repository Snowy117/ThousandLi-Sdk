using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

public sealed class CompatibilityException : InvalidOperationException
{
    public CompatibilityException(string message)
        : base(message)
    {
    }

    public CompatibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class PackageManifestParser
{
    public static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new CompatibilityException($"package.json property '{propertyName}' is required and must be a string.");
        }
        return property.GetString()!;
    }

    public static string? OptionalString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new CompatibilityException($"package.json property '{propertyName}' must be a string.");
        return string.IsNullOrWhiteSpace(property.GetString()) ? null : property.GetString();
    }

    public static IReadOnlyList<string> OptionalStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return [];
        if (property.ValueKind != JsonValueKind.Array)
            throw new CompatibilityException($"package.json property '{propertyName}' must be an array.");
        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new CompatibilityException($"package.json property '{propertyName}' must contain only non-blank strings.");
            values.Add(item.GetString()!);
        }
        return values.Distinct(StringComparer.Ordinal).Count() == values.Count
            ? values
            : throw new CompatibilityException(
                $"package.json property '{propertyName}' must not contain duplicate values.");
    }

    public static void ValidateSlug(string value, string propertyName)
    {
        if (value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new CompatibilityException(
                $"package.json property '{propertyName}' may contain only lowercase a-z, digits 0-9, and '-'.");
        }
    }

    public static void ValidateRelativePath(string value, string propertyName)
    {
        if (Path.IsPathRooted(value) || value.Contains('\\', StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new CompatibilityException(
                $"package.json property '{propertyName}' must be a safe artifact-relative path using '/'.");
        }
    }
}

public sealed record GamePackageManifest
{
    private GamePackageManifest(
        string packageId,
        string entryAssembly,
        string? frontendRoot,
        GamePackageCompatibility compatibility)
    {
        PackageId = packageId;
        EntryAssembly = entryAssembly;
        FrontendRoot = frontendRoot;
        Compatibility = compatibility;
    }

    public string PackageId { get; }
    public string EntryAssembly { get; }
    public string? FrontendRoot { get; }
    public GamePackageCompatibility Compatibility { get; }

    public static GamePackageManifest Load(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        var manifestPath = Path.Combine(Path.GetFullPath(artifactDirectory), "package.json");
        return File.Exists(manifestPath)
            ? Parse(File.ReadAllText(manifestPath))
            : throw new FileNotFoundException("Package Artifact does not contain package.json.", manifestPath);
    }

    public static GamePackageManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new CompatibilityException($"package.json is not valid JSON: {exception.Message}", exception);
        }
    }

    [JetBrains.Annotations.PublicAPI]
    public static GamePackageManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new CompatibilityException("package.json root must be a JSON object.");

        var authorId = PackageManifestParser.RequiredString(root, "authorId");
        var packageName = PackageManifestParser.RequiredString(root, "packageName");
        var packageVersion = PackageManifestParser.RequiredString(root, "packageVersion");
        if (!string.Equals(PackageManifestParser.RequiredString(root, "packageKind"), "GamePackage", StringComparison.Ordinal))
            throw new CompatibilityException("Slice 1 DevHost can load only GamePackage artifacts.");
        PackageManifestParser.ValidateSlug(authorId, "authorId");
        PackageManifestParser.ValidateSlug(packageName, "packageName");

        var entryAssembly = PackageManifestParser.RequiredString(root, "entryAssembly");
        var frontendRoot = PackageManifestParser.OptionalString(root, "frontendRoot");
        PackageManifestParser.ValidateRelativePath(entryAssembly, "entryAssembly");
        if (frontendRoot is not null) PackageManifestParser.ValidateRelativePath(frontendRoot, "frontendRoot");

        if (!root.TryGetProperty("compatibility", out var compatibility) || compatibility.ValueKind != JsonValueKind.Object)
            throw new CompatibilityException("package.json property 'compatibility' is required and must be an object.");

        var runtime = ReadVersion(compatibility, "runtime");
        PackageVersion? frontend = frontendRoot is null ? null : ReadVersion(compatibility, "frontend");
        return new GamePackageManifest(
            $"{authorId}_{packageName}@{packageVersion}",
            entryAssembly,
            frontendRoot,
            new GamePackageCompatibility(runtime, frontend));
    }

    private static PackageVersion ReadVersion(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            throw new CompatibilityException($"package.json compatibility property '{propertyName}' must be an object.");
        return new PackageVersion(RequiredInt(property, "major"), RequiredInt(property, "minor", allowZero: true));
    }

    private static int RequiredInt(JsonElement parent, string propertyName, bool allowZero = false)
    {
        if (!parent.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value) ||
            value < (allowZero ? 0 : 1))
        {
            throw new CompatibilityException($"package.json property '{propertyName}' must be a valid integer.");
        }
        return value;
    }
}

/// <summary>
/// Parsed <c>package.json</c> of an Expert Package Artifact. Model availability is not declared
/// here: the local gateway configuration exposed through <c>IRuntimeBasicAi.AvailableModels</c> is
/// the authoritative model list, and <see cref="OpenAiModels"/> is advisory metadata only.
/// </summary>
public sealed record ExpertPackageManifest
{
    private ExpertPackageManifest(string packageId, string entryAssembly, IReadOnlyList<string> openAiModels)
    {
        PackageId = packageId;
        EntryAssembly = entryAssembly;
        OpenAiModels = openAiModels;
    }

    public string PackageId { get; }
    public string EntryAssembly { get; }
    public IReadOnlyList<string> OpenAiModels { get; }

    public static ExpertPackageManifest Load(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        var manifestPath = Path.Combine(Path.GetFullPath(artifactDirectory), "package.json");
        return File.Exists(manifestPath)
            ? Parse(File.ReadAllText(manifestPath))
            : throw new FileNotFoundException("Package Artifact does not contain package.json.", manifestPath);
    }

    public static ExpertPackageManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new CompatibilityException($"package.json is not valid JSON: {exception.Message}", exception);
        }
    }

    [JetBrains.Annotations.PublicAPI]
    public static ExpertPackageManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new CompatibilityException("package.json root must be a JSON object.");

        var authorId = PackageManifestParser.RequiredString(root, "authorId");
        var packageName = PackageManifestParser.RequiredString(root, "packageName");
        var packageVersion = PackageManifestParser.RequiredString(root, "packageVersion");
        if (!string.Equals(PackageManifestParser.RequiredString(root, "packageKind"), "ExpertPackage", StringComparison.Ordinal))
            throw new CompatibilityException("The Expert Package loader accepts only packageKind 'ExpertPackage' artifacts.");
        PackageManifestParser.ValidateSlug(authorId, "authorId");
        PackageManifestParser.ValidateSlug(packageName, "packageName");

        var entryAssembly = PackageManifestParser.RequiredString(root, "entryAssembly");
        PackageManifestParser.ValidateRelativePath(entryAssembly, "entryAssembly");

        return new ExpertPackageManifest(
            $"{authorId}_{packageName}@{packageVersion}",
            entryAssembly,
            PackageManifestParser.OptionalStringArray(root, "openAiModels"));
    }
}

public sealed record DevHostCompatibility(PackageVersion Runtime, PackageVersion Frontend);

public static class CompatibilityValidator
{
    public static void Validate(GamePackageManifest manifest, DevHostCompatibility available)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(available);
        ValidateVersion("runtime", manifest.Compatibility.Runtime, available.Runtime);
        if (manifest.Compatibility.Frontend is { } requiredFrontend)
            ValidateVersion("frontend", requiredFrontend, available.Frontend);
    }

    private static void ValidateVersion(string contractName, PackageVersion required, PackageVersion available)
    {
        if (!available.Supports(required))
            throw new CompatibilityException($"Package requires {contractName} contract {required}, but DevHost provides {available}.");
    }
}
