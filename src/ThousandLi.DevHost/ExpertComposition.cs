using System.Reflection;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts;

namespace ThousandLi.DevHost;

/// <summary>
/// The registry plus the explicitly loaded trusted contract assemblies. The assemblies are loaded
/// once into the default context and shared with every Expert assembly load context so contract
/// type identities stay unified.
/// </summary>
internal sealed record ExpertContractComposition(
    ExpertContractRegistry Registry,
    IReadOnlyDictionary<string, Assembly> ContractAssemblies);

internal static class ExpertComposition
{
    private const string SettingsOverrideFileName = "expert-settings.json";

    public static ExpertContractComposition BuildContractRegistry(IReadOnlyList<string> contractAssemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(contractAssemblyPaths);
        var officialAssembly = typeof(ExpertContractAttribute).Assembly;
        var officialAssemblyName = officialAssembly.GetName().Name ?? officialAssembly.FullName ?? officialAssembly.ToString();
        var contractAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [officialAssemblyName] = officialAssembly
        };
        var assemblies = new List<Assembly> { officialAssembly };
        foreach (var path in contractAssemblyPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Contract assembly does not exist.", fullPath);
            var assembly = Assembly.LoadFrom(fullPath);
            assemblies.Add(assembly);
            var name = assembly.GetName().Name ?? assembly.FullName ?? assembly.ToString();
            contractAssemblies.TryAdd(name, assembly);
        }

        return new ExpertContractComposition(new ExpertContractRegistry(assemblies), contractAssemblies);
    }

    public static LocalExpertExecutor CreateLocalExecutor(
        DevHostOptions options,
        ExpertContractComposition contracts,
        BoundPlayerProfile player,
        HttpClient gatewayClient,
        List<LoadedExpertPackage> loadedPackages)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(gatewayClient);
        ArgumentNullException.ThrowIfNull(loadedPackages);
        if (options.ExpertArtifactDirectories.Count == 0)
            throw new ArgumentException(
                "Local expert execution requires at least one '--expert-artifact <directory>' entry.");
        if (string.IsNullOrWhiteSpace(options.GatewayEndpoint))
            throw new ArgumentException(
                "Local expert execution requires '--gateway-endpoint <url>' pointing at an OpenAI-compatible gateway.");
        if (options.GatewayModels.Count == 0)
            throw new ArgumentException(
                "Local expert execution requires at least one '--gateway-model <id>' entry.");

        var gatewayOptions = new OpenAiCompatibleBasicAiOptions(
            options.GatewayEndpoint,
            options.GatewayModels,
            CreateApiKeyProvider(options));
        var executorOptions = new LocalExpertExecutorOptions(
            new OpenAiCompatibleBasicAi(gatewayClient, gatewayOptions),
            player)
        {
            SettingsOverrideFile = ResolveSettingsOverrideFile(options)
        };
        loadedPackages.AddRange(options.ExpertArtifactDirectories.Select(
            directory => ExpertPackageLoader.Load(directory, contracts.ContractAssemblies)));
        return new LocalExpertExecutor(
            contracts.Registry,
            loadedPackages,
            options.ExpertBindings.Count == 0 ? null : options.ExpertBindings,
            executorOptions);
    }

    public static void ValidateExecutorOptions(DevHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExpertExecutor == DevHostOptions.LocalExecutorName)
            return;
        var localOnlyArguments = new List<string>();
        if (options.ExpertArtifactDirectories.Count > 0)
            localOnlyArguments.Add("--expert-artifact");
        if (options.ExpertBindings.Count > 0)
            localOnlyArguments.Add("--expert-binding");
        if (!string.IsNullOrWhiteSpace(options.GatewayEndpoint))
            localOnlyArguments.Add("--gateway-endpoint");
        if (options.GatewayModels.Count > 0)
            localOnlyArguments.Add("--gateway-model");
        if (!string.IsNullOrWhiteSpace(options.GatewayApiKeyEnvironmentVariable) &&
            options.GatewayApiKeyEnvironmentVariable != DevHostOptions.DefaultGatewayApiKeyEnvironmentVariable)
            localOnlyArguments.Add("--gateway-api-key-env");
        if (localOnlyArguments.Count > 0)
            throw new ArgumentException(
                $"Arguments {string.Join(", ", localOnlyArguments.Select(flag => $"'{flag}'"))} require " +
                $"'--expert-executor {DevHostOptions.LocalExecutorName}'.");
    }

    /// <summary>
    /// The single local user settings override file in the DevHost data root; <c>--ephemeral</c>
    /// keeps settings in memory (schema defaults only). Absent files simply resolve to defaults.
    /// </summary>
    private static FileInfo? ResolveSettingsOverrideFile(DevHostOptions options) =>
        options.Ephemeral
            ? null
            : new FileInfo(Path.Combine(ResolveDataRoot(options.DataRoot), SettingsOverrideFileName));

    internal static string ResolveDataRoot(string? dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
            return Path.GetFullPath(dataRoot);
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDirectory, "ThousandLi", "DevHost", "v1");
    }

    /// <summary>
    /// The gateway credential enters only as a delegate over the configured environment variable;
    /// the resolved key is never stored, logged, or exposed to package code or frontend JS.
    /// </summary>
    private static Func<string?>? CreateApiKeyProvider(DevHostOptions options) =>
        string.IsNullOrWhiteSpace(options.GatewayApiKeyEnvironmentVariable)
            ? null
            : () => Environment.GetEnvironmentVariable(options.GatewayApiKeyEnvironmentVariable);
}
