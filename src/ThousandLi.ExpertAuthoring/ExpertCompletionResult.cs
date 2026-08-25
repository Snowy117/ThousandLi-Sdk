using System.Collections.ObjectModel;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The result artifact of an expert invocation. Does not expose raw model output; carries only
/// expert-extracted metadata.
/// </summary>
public sealed record ExpertCompletionResult
{
    public ExpertCompletionResult(IReadOnlyDictionary<string, string>? metadata = null)
    {
        Metadata = metadata is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, string>? Metadata { get; }
}
