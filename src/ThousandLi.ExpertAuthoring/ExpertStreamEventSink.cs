using JetBrains.Annotations;
using System.Text;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Per-invocation sink that receives raw stream events forwarded from a local BasicAi stream and
/// maps them to semantic callbacks. A sink holds accumulation buffers and must be constructed fresh
/// for every invocation.
/// </summary>
public interface IExpertStreamEventSink
{
    ValueTask OnEventAsync(ExpertStreamEvent streamEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional sink base class. When constructed with declared root properties it drops JSON events
/// whose root property segment is not declared (defense-in-depth isolation) and provides string
/// accumulation and path matching helpers. Structural events on the root value are always forwarded.
/// </summary>
public abstract class ExpertStreamEventSinkBase : IExpertStreamEventSink
{
    private readonly HashSet<string>? _declaredRootProperties;
    private readonly Dictionary<string, StringBuilder> _accumulating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _completedStrings = new(StringComparer.Ordinal);

    protected ExpertStreamEventSinkBase(IEnumerable<string>? declaredRootProperties = null)
    {
        if (declaredRootProperties is null)
            return;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in declaredRootProperties)
        {
            if (property is null)
                throw new ArgumentException("Declared root properties cannot contain null entries.", nameof(declaredRootProperties));
            ArgumentException.ThrowIfNullOrWhiteSpace(property);
            properties.Add(property);
        }

        _declaredRootProperties = properties;
    }

    public async ValueTask OnEventAsync(ExpertStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        switch (streamEvent)
        {
            case ExpertTextDeltaEvent textDelta:
                await OnTextDeltaAsync(textDelta, cancellationToken).ConfigureAwait(false);
                break;
            case ExpertJsonStreamEvent jsonEvent:
                Accumulate(jsonEvent);
                if (IsDeclaredRootSegment(jsonEvent.Path))
                    await OnJsonEventAsync(jsonEvent, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unknown stream event type '{streamEvent.GetType().FullName}'.", nameof(streamEvent));
        }
    }

    protected virtual ValueTask OnTextDeltaAsync(ExpertTextDeltaEvent delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    protected virtual ValueTask OnJsonEventAsync(ExpertJsonStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Path equality against a JSON Pointer literal (root is the empty string).</summary>
    [UsedImplicitly]
    protected static bool MatchPathExact(string path, string expected) =>
        string.Equals(path, expected, StringComparison.Ordinal);

    /// <summary>Segment-aware prefix match: <c>/a</c> matches <c>/a</c> and <c>/a/b</c> but not <c>/ab</c>.</summary>
    protected static bool MatchPathPrefix(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return path.Length == prefix.Length || prefix.EndsWith('/') || path[prefix.Length] == '/';
    }

    /// <summary>Completed string fields accumulated from started/chunk/completed event triples.</summary>
    protected IReadOnlyDictionary<string, string> CompletedStringFields => _completedStrings;

    protected bool TryGetStringField(string path, out string value) => _completedStrings.TryGetValue(path, out value!);

    private void Accumulate(ExpertJsonStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case ExpertJsonStringStartedEvent:
                _accumulating[streamEvent.Path] = new StringBuilder();
                break;
            case ExpertJsonStringChunkEvent chunk:
                if (_accumulating.TryGetValue(streamEvent.Path, out var builder))
                    builder.Append(chunk.Value);
                break;
            case ExpertJsonStringCompletedEvent:
                if (_accumulating.Remove(streamEvent.Path, out var completed))
                    _completedStrings[streamEvent.Path] = completed.ToString();
                break;
        }
    }

    private bool IsDeclaredRootSegment(string path)
    {
        if (_declaredRootProperties is null)
            return true;
        if (path.Length == 0)
            return true;
        var segment = path[1..];
        var separator = segment.IndexOf('/');
        if (separator >= 0)
            segment = segment[..separator];
        return _declaredRootProperties.Contains(UnescapePointerToken(segment));
    }

    /// <summary>Declared root properties are compared by their raw JSON names, while paths carry
    /// RFC 6901 tokens, so the segment must be unescaped (~1 → /, then ~0 → ~) before matching.</summary>
    private static string UnescapePointerToken(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
