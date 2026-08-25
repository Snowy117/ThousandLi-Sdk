using JetBrains.Annotations;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Declares the primary output root property of an expert invocation. The primary output is always
/// one root property of the model-facing JSON; the typed Game-facing API differs between text and
/// raw JSON streaming.
/// </summary>
public interface IExpertPrimaryOutput
{
    string PropertyName { get; }
}

/// <summary>
/// A text primary output: streamed as <see cref="ExpertTextDeltaEvent"/> values to the delta callback.
/// </summary>
public sealed record TextPrimaryOutput : IExpertPrimaryOutput
{
    public TextPrimaryOutput(
        Func<ExpertTextDeltaEvent, CancellationToken, ValueTask> onDelta,
        string propertyName = "narrative")
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        OnDelta = onDelta;
        PropertyName = propertyName;
    }

    public Func<ExpertTextDeltaEvent, CancellationToken, ValueTask> OnDelta { get; }

    public string PropertyName { get; }
}

/// <summary>
/// A JSON primary output: raw <see cref="ExpertJsonStreamEvent"/> values scoped to the primary
/// output property are delivered to the callback. The callback is invoked by expert-authored
/// sinks rather than platform code, so it is part of the published authoring surface.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonPrimaryOutput : IExpertPrimaryOutput
{
    public JsonPrimaryOutput(
        string propertyName,
        Func<ExpertJsonStreamEvent, CancellationToken, ValueTask> onJsonEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(onJsonEvent);
        PropertyName = propertyName;
        OnJsonEvent = onJsonEvent;
    }

    public string PropertyName { get; }

    public Func<ExpertJsonStreamEvent, CancellationToken, ValueTask> OnJsonEvent { get; }
}
