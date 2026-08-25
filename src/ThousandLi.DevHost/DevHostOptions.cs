using System.Collections.ObjectModel;

namespace ThousandLi.DevHost;

[JetBrains.Annotations.PublicAPI]
public sealed record DevHostOptions
{
    public const string FakeExecutorName = "fake";
    public const string LocalExecutorName = "local";
    public const string DefaultGatewayApiKeyEnvironmentVariable = "THOUSANDLI_GATEWAY_API_KEY";

    private static readonly IReadOnlyDictionary<string, string> EmptyBindings =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    public required string ArtifactDirectory { get; init; }
    public required string WorkspaceId { get; init; }
    public required string FrontendUrl { get; init; }
    public required string SessionId { get; init; }
    public int Port { get; init; } = 5180;
    public bool Ephemeral { get; init; }
    public bool Reset { get; init; }
    public string? DataRoot { get; init; }
    public string? FakeScenariosPath { get; init; }

    /// <summary>Expert executor selection: 'fake' (default) or 'local' (explicit opt-in).</summary>
    public string ExpertExecutor { get; init; } = FakeExecutorName;

    /// <summary>Artifact directories of trusted local Expert Packages to load with the local executor.</summary>
    public IReadOnlyList<string> ExpertArtifactDirectories { get; init; } = [];

    /// <summary>Explicit contract assembly paths registered in addition to the official contract assembly.</summary>
    public IReadOnlyList<string> ContractAssemblies { get; init; } = [];

    /// <summary>Explicit contractId → expertPackageId bindings for the local executor.</summary>
    public IReadOnlyDictionary<string, string> ExpertBindings { get; init; } = EmptyBindings;

    /// <summary>The OpenAI-compatible gateway endpoint used by the local expert executor.</summary>
    public string? GatewayEndpoint { get; init; }

    /// <summary>The models configured on the local gateway; the authoritative list experts read at runtime.</summary>
    public IReadOnlyList<string> GatewayModels { get; init; } = [];

    /// <summary>
    /// The environment variable the composition root reads the gateway API key from. The key value
    /// itself never enters options; it is resolved per request through a delegate.
    /// </summary>
    public string GatewayApiKeyEnvironmentVariable { get; init; } = DefaultGatewayApiKeyEnvironmentVariable;

    public static DevHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--ephemeral" or "--reset")
            {
                flags.Add(argument);
                continue;
            }
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Unknown or incomplete argument '{argument}'.");
            var value = args[++index];
            if (argument is "--expert-artifact" or "--contract-assembly" or "--gateway-model" or "--expert-binding")
                CollectRepeated(repeated, argument, value);
            else
                values[argument] = value;
        }

        var expertExecutor = values.GetValueOrDefault("--expert-executor", FakeExecutorName);
        if (expertExecutor is not (FakeExecutorName or LocalExecutorName))
            throw new ArgumentException(
                $"Argument '--expert-executor' must be '{FakeExecutorName}' or '{LocalExecutorName}', but was '{expertExecutor}'.");

        return new DevHostOptions
        {
            ArtifactDirectory = Required(values, "--artifact"),
            WorkspaceId = values.GetValueOrDefault("--workspace", "default"),
            FrontendUrl = values.GetValueOrDefault("--frontend-url", "/game/index.html"),
            SessionId = values.GetValueOrDefault("--session", "dev-session"),
            Port = int.Parse(values.GetValueOrDefault("--port", "5180"), System.Globalization.CultureInfo.InvariantCulture),
            Ephemeral = flags.Contains("--ephemeral"),
            Reset = flags.Contains("--reset"),
            DataRoot = values.GetValueOrDefault("--data-root"),
            FakeScenariosPath = values.GetValueOrDefault("--fake-scenarios"),
            ExpertExecutor = expertExecutor,
            ExpertArtifactDirectories = [.. repeated.GetValueOrDefault("--expert-artifact", [])],
            ContractAssemblies = [.. repeated.GetValueOrDefault("--contract-assembly", [])],
            ExpertBindings = ParseBindings(repeated.GetValueOrDefault("--expert-binding", [])),
            GatewayEndpoint = values.GetValueOrDefault("--gateway-endpoint"),
            GatewayModels = [.. repeated.GetValueOrDefault("--gateway-model", [])],
            GatewayApiKeyEnvironmentVariable = values.GetValueOrDefault(
                "--gateway-api-key-env", DefaultGatewayApiKeyEnvironmentVariable)
        };
    }

    private static void CollectRepeated(Dictionary<string, List<string>> repeated, string argument, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Argument '{argument}' requires a non-blank value.");
        if (!repeated.TryGetValue(argument, out var list))
            repeated[argument] = list = [];
        list.Add(value);
    }

    private static Dictionary<string, string> ParseBindings(List<string> entries)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
                throw new ArgumentException(
                    $"Argument '--expert-binding' expects '<contractId>=<expertPackageId>', but was '{entry}'.");
            var contractId = entry[..separator];
            var packageId = entry[(separator + 1)..];
            if (!bindings.TryAdd(contractId, packageId))
                throw new ArgumentException($"Duplicate '--expert-binding' for contract '{contractId}'.");
        }
        return bindings;
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument '{name}' is missing.");
}
