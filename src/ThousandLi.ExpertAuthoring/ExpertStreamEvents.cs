namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The common public stream event model for local expert invocations. Every
/// <see cref="ILocalBasicAi.StreamAsync"/> call yields exactly one variant: plain-text deltas
/// (<see cref="ExpertTextDeltaEvent"/>) or structured JSON stream events
/// (<see cref="ExpertJsonStreamEvent"/>), selected by the request response mode.
/// This shape is the reuse baseline for the remote semantic stream decoder; changes to it are
/// cross-slice contract changes.
/// </summary>
public abstract record ExpertStreamEvent;

/// <summary>An incremental plain-text delta. Whitespace-only deltas are valid narrative content.</summary>
public sealed record ExpertTextDeltaEvent : ExpertStreamEvent
{
    public ExpertTextDeltaEvent(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
            throw new ArgumentException("Text delta cannot be empty.", nameof(delta));
        Delta = delta;
    }

    public string Delta { get; }
}

/// <summary>
/// Base type of the JSON stream event subset. <see cref="Path"/> uses JSON Pointer syntax
/// (RFC 6901): the root value is <c>""</c>, members append <c>/name</c>, and array elements append
/// <c>/index</c>. Structural events on the root value therefore carry an empty path.
/// </summary>
public abstract record ExpertJsonStreamEvent : ExpertStreamEvent
{
    protected ExpertJsonStreamEvent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length != 0 && path[0] != '/')
            throw new ArgumentException("JSON stream event paths must be empty (root) or start with '/'.", nameof(path));
        Path = path;
    }

    public string Path { get; }
}

public sealed record ExpertJsonObjectStartedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonObjectStartedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonObjectCompletedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonObjectCompletedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonArrayStartedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonArrayStartedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonArrayCompletedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonArrayCompletedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonPropertyNameEvent : ExpertJsonStreamEvent
{
    public ExpertJsonPropertyNameEvent(string path, string name) : base(path)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>
    /// The unescaped property name as it appears in the JSON document. Empty and whitespace-only
    /// names are valid JSON and therefore representable; only a null name is rejected.
    /// </summary>
    public string Name { get; }
}

public sealed record ExpertJsonStringStartedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonStringStartedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonStringChunkEvent : ExpertJsonStreamEvent
{
    public ExpertJsonStringChunkEvent(string path, string value) : base(path)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>A decoded fragment of the streamed string. May be whitespace-only.</summary>
    public string Value { get; }
}

public sealed record ExpertJsonStringCompletedEvent : ExpertJsonStreamEvent
{
    public ExpertJsonStringCompletedEvent(string path) : base(path)
    {
    }
}

public sealed record ExpertJsonNumberValueEvent : ExpertJsonStreamEvent
{
    public ExpertJsonNumberValueEvent(string path, string rawValue) : base(path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawValue);
        RawValue = rawValue;
    }

    /// <summary>The number exactly as it appeared in the stream.</summary>
    public string RawValue { get; }
}

public sealed record ExpertJsonBooleanValueEvent : ExpertJsonStreamEvent
{
    public ExpertJsonBooleanValueEvent(string path, bool value) : base(path) => Value = value;

    public bool Value { get; }
}

public sealed record ExpertJsonNullValueEvent : ExpertJsonStreamEvent
{
    public ExpertJsonNullValueEvent(string path) : base(path)
    {
    }
}
