namespace ThousandLi.DevHost;

[JetBrains.Annotations.PublicAPI]
public sealed record DevHostOptions
{
    public required string ArtifactDirectory { get; init; }
    public required string WorkspaceId { get; init; }
    public required string FrontendUrl { get; init; }
    public required string SessionId { get; init; }
    public int Port { get; init; } = 5180;
    public bool Ephemeral { get; init; }
    public bool Reset { get; init; }
    public string? DataRoot { get; init; }
    public string? FakeScenariosPath { get; init; }

    public static DevHostOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
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
            values[argument] = args[++index];
        }

        return new DevHostOptions
        {
            ArtifactDirectory = Required(values, "--artifact"),
            WorkspaceId = values.GetValueOrDefault("--workspace", "default"),
            FrontendUrl = values.GetValueOrDefault("--frontend-url", "/game/"),
            SessionId = values.GetValueOrDefault("--session", "dev-session"),
            Port = int.Parse(values.GetValueOrDefault("--port", "5180"), System.Globalization.CultureInfo.InvariantCulture),
            Ephemeral = flags.Contains("--ephemeral"),
            Reset = flags.Contains("--reset"),
            DataRoot = values.GetValueOrDefault("--data-root"),
            FakeScenariosPath = values.GetValueOrDefault("--fake-scenarios")
        };
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument '{name}' is missing.");
}
