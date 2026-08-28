using System.Text;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Per-invocation sink that receives <see cref="JsonStreamEvent"/> values forwarded from a
/// schema-driven BasicAi stream and maps them to semantic callbacks. A sink holds accumulation
/// buffers and must be constructed fresh for every invocation.
/// </summary>
public interface IJsonExpertStreamEventSink
{
    /// <summary>Consumes one raw JSON stream event forwarded by the execution flow.</summary>
    ValueTask OnEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional sink base class over the <see cref="JsonStreamEvent"/> model. Constructed with the
/// expert's declared root properties, it drops events whose first path segment is undeclared
/// (whitelist isolation) and provides path matching, string-field accumulation, and array boundary
/// helpers. Experts override <see cref="OnDeclaredEventAsync"/> to translate declared events into
/// semantic callbacks; experts implementing <see cref="IJsonExpertStreamEventSink"/> directly must
/// provide their own filtering.
/// </summary>
public abstract class JsonExpertStreamEventSinkBase : IJsonExpertStreamEventSink
{
    private const char PathSeparator = '/';
    private readonly HashSet<string> _declaredProperties;

    protected JsonExpertStreamEventSinkBase(IEnumerable<string> declaredProperties)
    {
        ArgumentNullException.ThrowIfNull(declaredProperties);
        _declaredProperties = new HashSet<string>(declaredProperties, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whitelist default: reads the first path segment (the root property name) of the event and
    /// drops the event when no segment exists or the segment is undeclared; declared events are
    /// forwarded to <see cref="OnDeclaredEventAsync"/>. Events on the object root (empty path) are
    /// dropped as well.
    /// </summary>
    public ValueTask OnEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        var rootSegment = ReadRootSegment(streamEvent.Path);
        if (rootSegment is null || !_declaredProperties.Contains(rootSegment))
            return ValueTask.CompletedTask;

        return OnDeclaredEventAsync(streamEvent, cancellationToken);
    }

    /// <summary>Expert override: handles one whitelisted event and translates it into semantic callbacks.</summary>
    protected abstract ValueTask OnDeclaredEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the first segment of the event path (between the first and the next
    /// <c>/</c>). Returns null when the path is empty or does not start with <c>/</c>.
    /// </summary>
    private static string? ReadRootSegment(string path)
    {
        if (path.Length is 0 || path[0] is not PathSeparator)
            return null;

        var end = path.IndexOf(PathSeparator, startIndex: 1);
        return end < 0 ? path[1..] : path[1..end];
    }

    /// <summary>
    /// Determines whether the event path equals <c>/propertyName</c> or starts with it
    /// (<c>/propertyName/...</c>).
    /// </summary>
    protected static bool MatchPathPrefix(JsonStreamEvent streamEvent, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        var primaryPath = PathSeparator + propertyName;
        var path = streamEvent.Path;
        return string.Equals(path, primaryPath, StringComparison.Ordinal) ||
               path.StartsWith(primaryPath + PathSeparator, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether the event path equals exactly <c>/propertyName</c> (no sub-path).
    /// </summary>
    protected static bool MatchPathExact(JsonStreamEvent streamEvent, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return string.Equals(streamEvent.Path, PathSeparator + propertyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Accumulates string chunks of the exact path <c>/propertyName</c> into
    /// <paramref name="buffer"/>; returns the completed string and clears the buffer on
    /// <see cref="JsonStreamStringCompletedEvent"/>, and null otherwise. The buffer is left
    /// untouched when the event path does not match.
    /// </summary>
    /// <returns>The complete field value when it completes; null while incomplete or unmatched.</returns>
    protected static string? AccumulateStringField(JsonStreamEvent streamEvent, string propertyName, StringBuilder buffer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(buffer);

        if (!MatchPathExact(streamEvent, propertyName))
            return null;

        switch (streamEvent)
        {
            case JsonStreamStringChunkEvent chunk:
                buffer.Append(chunk.Value);
                return null;
            case JsonStreamStringCompletedEvent:
                var completed = buffer.ToString();
                buffer.Clear();
                return completed;
            default:
                return null;
        }
    }

    /// <summary>
    /// Tracks array boundary events of the exact path <c>/propertyName</c>.
    /// </summary>
    protected static ExpertStreamArrayBoundary TrackArrayBoundary(JsonStreamEvent streamEvent, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        var path = PathSeparator + propertyName;
        return streamEvent switch
        {
            JsonStreamArrayStartedEvent started when string.Equals(started.Path, path, StringComparison.Ordinal)
                => ExpertStreamArrayBoundary.Started,
            JsonStreamArrayCompletedEvent completed when string.Equals(completed.Path, path, StringComparison.Ordinal)
                => ExpertStreamArrayBoundary.Completed,
            _ => ExpertStreamArrayBoundary.None,
        };
    }
}

/// <summary>The array boundary tracking result of <see cref="JsonExpertStreamEventSinkBase.TrackArrayBoundary"/>.</summary>
public enum ExpertStreamArrayBoundary
{
    /// <summary>Not a boundary event of that array.</summary>
    None = 0,

    /// <summary>The array started.</summary>
    Started = 1,

    /// <summary>The array completed.</summary>
    Completed = 2
}
