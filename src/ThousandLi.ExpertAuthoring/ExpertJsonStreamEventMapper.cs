using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Maps the authoring JSON stream event family onto the <see cref="JsonStreamEvent"/> contracts
/// family. Shared by every runtime-facing BasicAi path that must surface parser events through the
/// published stream contracts.
/// </summary>
internal static class ExpertJsonStreamEventMapper
{
    internal static JsonStreamEvent ToContractsEvent(ExpertJsonStreamEvent streamEvent) =>
        streamEvent switch
        {
            ExpertJsonObjectStartedEvent started => JsonStreamEvent.ObjectStarted(started.Path),
            ExpertJsonObjectCompletedEvent completed => JsonStreamEvent.ObjectCompleted(completed.Path),
            ExpertJsonArrayStartedEvent started => JsonStreamEvent.ArrayStarted(started.Path),
            ExpertJsonArrayCompletedEvent completed => JsonStreamEvent.ArrayCompleted(completed.Path),
            ExpertJsonPropertyNameEvent propertyName => JsonStreamEvent.PropertyName(propertyName.Path, propertyName.Name),
            ExpertJsonStringStartedEvent started => JsonStreamEvent.StringStarted(started.Path),
            ExpertJsonStringChunkEvent chunk => JsonStreamEvent.StringChunk(chunk.Path, chunk.Value),
            ExpertJsonStringCompletedEvent completed => JsonStreamEvent.StringCompleted(completed.Path),
            ExpertJsonNumberValueEvent number => JsonStreamEvent.NumberValue(number.Path, number.RawValue),
            ExpertJsonBooleanValueEvent boolean => JsonStreamEvent.BooleanValue(boolean.Path, boolean.Value),
            ExpertJsonNullValueEvent @null => JsonStreamEvent.NullValue(@null.Path),
            _ => throw new InvalidOperationException($"Unknown stream event type '{streamEvent.GetType().FullName}'.")
        };

    /// <summary>
    /// Replays a complete model JSON document through the stream parser as contracts events. Used
    /// by non-streaming execution paths so sinks observe the same event grammar as streaming ones.
    /// </summary>
    internal static IEnumerable<JsonStreamEvent> ParseElementEvents(JsonElement json)
    {
        var parser = new ExpertJsonStreamParser();
        foreach (var streamEvent in parser.Feed(json.GetRawText()))
            yield return ToContractsEvent(streamEvent);
        foreach (var streamEvent in parser.Complete())
            yield return ToContractsEvent(streamEvent);
    }
}
