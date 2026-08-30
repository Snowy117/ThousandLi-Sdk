using System.Reflection;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.RemoteExperts;

namespace ThousandLi.DevHost;

/// <summary>
/// The registry plus the explicitly loaded trusted contract assemblies. The assemblies are loaded
/// once into the default context and shared with every Expert assembly load context so contract
/// type identities stay unified.
/// </summary>
internal sealed record ExpertContractComposition(
    ExpertContractRegistry Registry,
    IReadOnlyDictionary<string, Assembly> ContractAssemblies);

/// <summary>
/// The remote expert execution trio composed by the DevHost composition root: the raw client (used
/// by the Playground to list the platform catalog), the invocation runner (the Playground
/// invoke/replay tool face), and the game-facing remote facade (typed fluent proxy experts).
/// </summary>
public sealed record RemoteExpertComposition(
    RemoteExpertClient Client,
    RemoteInvocationRunner Runner,
    RemoteExpertFacade Facade);

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

    public static LocalExpertComposition CreateLocalExpertComposition(
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
            CreateEnvironmentTokenProvider(options.GatewayApiKeyEnvironmentVariable));
        var compositionOptions = new LocalExpertCompositionOptions(
            new OpenAiCompatibleBasicAi(gatewayClient, gatewayOptions),
            player)
        {
            SettingsOverrideFile = ResolveSettingsOverrideFile(options)
        };
        loadedPackages.AddRange(options.ExpertArtifactDirectories.Select(
            directory => ExpertPackageLoader.Load(directory, contracts.ContractAssemblies)));
        return new LocalExpertComposition(
            contracts.Registry,
            loadedPackages,
            options.ExpertBindings.Count == 0 ? null : options.ExpertBindings,
            compositionOptions);
    }

    /// <summary>
    /// Composes the remote expert execution trio from a DevHost already validated for the remote
    /// expert mode. The caller owns the <see cref="HttpClient" /> lifecycle and must disable its
    /// default request timeout for the long SSE event stream.
    /// </summary>
    public static RemoteExpertComposition CreateRemoteInvocationRunner(
        DevHostOptions options,
        HttpClient platformClient,
        BoundPlayerProfile? player = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(platformClient);
        if (string.IsNullOrWhiteSpace(options.RemoteEndpoint))
            throw new ArgumentException(
                "Remote expert execution requires '--remote-endpoint <url>' pointing at the ThousandLi platform host.");

        var client = new RemoteExpertClient(
            platformClient,
            new RemoteExpertClientOptions(options.RemoteEndpoint, CreateEnvironmentTokenProvider(options.RemoteTokenEnvironmentVariable)));
        var runnerOptions = new RemoteInvocationRunnerOptions(options.RemoteBindings);
        var runner = new RemoteInvocationRunner(client, runnerOptions);
        return new RemoteExpertComposition(
            client,
            runner,
            new RemoteExpertFacade(client, runnerOptions, player));
    }

    public static void ValidateExpertModeOptions(DevHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Experts != DevHostOptions.LocalExecutorName)
        {
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
                    $"'--experts {DevHostOptions.LocalExecutorName}'.");
        }

        if (options.Experts == DevHostOptions.RemoteExecutorName)
            return;
        var remoteOnlyArguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RemoteEndpoint))
            remoteOnlyArguments.Add("--remote-endpoint");
        if (options.RemoteBindings.Count > 0)
            remoteOnlyArguments.Add("--remote-binding");
        if (!string.IsNullOrWhiteSpace(options.RemoteTokenEnvironmentVariable) &&
            options.RemoteTokenEnvironmentVariable != DevHostOptions.DefaultRemoteTokenEnvironmentVariable)
            remoteOnlyArguments.Add("--remote-token-env");
        if (remoteOnlyArguments.Count > 0)
            throw new ArgumentException(
                $"Arguments {string.Join(", ", remoteOnlyArguments.Select(flag => $"'{flag}'"))} require " +
                $"'--experts {DevHostOptions.RemoteExecutorName}'.");
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
    /// Credentials enter only as delegates over configured environment variables; the resolved
    /// values are never stored, logged, or exposed to package code or frontend JS.
    /// </summary>
    private static Func<string?>? CreateEnvironmentTokenProvider(string? environmentVariable) =>
        string.IsNullOrWhiteSpace(environmentVariable)
            ? null
            : () => Environment.GetEnvironmentVariable(environmentVariable);
}
