namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The public stream event model for local expert invocations. Plain-text streaming
/// (<see cref="ILocalBasicAi.StreamTextAsync"/>) yields <see cref="ExpertTextDeltaEvent"/> values;
/// JSON streaming (<see cref="ILocalBasicAi.StreamJsonAsync"/>) yields the published
/// <see cref="ThousandLi.Contracts.JsonStreamEvent"/> family directly.
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
