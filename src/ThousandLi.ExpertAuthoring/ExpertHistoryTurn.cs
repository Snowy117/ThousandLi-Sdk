using System.Collections.ObjectModel;

namespace ThousandLi.ExpertAuthoring;

/// <summary>One turn of history consumed read-only by an expert.</summary>
public sealed record ExpertHistoryTurn
{
    public ExpertHistoryTurn(string role, string content, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);
        Role = role;
        Content = content;
        Metadata = metadata is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    public string Role { get; }

    public string Content { get; }

    public IReadOnlyDictionary<string, string>? Metadata { get; }
}
