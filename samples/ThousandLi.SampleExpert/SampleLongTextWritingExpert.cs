using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.SampleExpert;

/// <summary>
/// Sample concrete long-text-writing expert for the official
/// <c>thousandli.expert/long-text-writing</c> category contract. It streams the model's narrative
/// JSON string chunks from the locally configured gateway model to the configured text primary
/// output and (on protocol invocations) as <c>chunk</c> semantic events, returning the accumulated
/// narration — mirroring how a real long-text-writing expert works.
/// </summary>
public sealed class SampleLongTextWritingExpert : AbstractLongTextWritingExpert, IInvocableExpert
{
    private IExpertSemanticEventSink? _events;
    private readonly StringBuilder _text = new();

    /// <summary>
    /// Protocol-facing entry (Playground / recording): projects the category input JSON onto the
    /// fluent configuration, executes one streaming pass, and returns the accumulated narration.
    /// </summary>
    public async Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        _text.Clear();
        WithWorldSettings(RequiredInputString(input, "worldSettings"));
        WithPlayerInput(RequiredInputString(input, "playerInput"));
        if (input.TryGetProperty("playerPersona", out var persona) &&
            persona.ValueKind == JsonValueKind.String && persona.GetString() is { Length: > 0 } personaText)
            WithPlayerPersona(personaText);
        await StreamAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { narrative = _text.ToString() });
    }

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    private async Task<ExpertCompletionResult> StreamOnceAsync(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
        var request = new BasicAiRequest(
            RuntimeContext.BasicAi.AvailableModels[0].ModelId,
            [BasicAiMessage.User(
                $"World: {WorldSettings}{Environment.NewLine}Player input: {PlayerInput}{Environment.NewLine}" +
                "Narrate the outcome as a single narrative JSON string.")],
            AiJsonSchema.Object(AiJsonSchema.Required("narrative", AiJsonSchema.String())));
        await foreach (var streamEvent in RuntimeContext.BasicAi
                           .StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (streamEvent is not BasicAiJsonStreamEvent
                {
                    Event: JsonStreamStringChunkEvent { Path: "/narrative" } chunk
                })
                continue;
            _text.Append(chunk.Value);
            if (ConfiguredPrimaryOutput is TextPrimaryOutput textOutput)
            {
                await textOutput.OnDelta(new TextDeltaEvent(chunk.Value), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_events is not null)
            {
                await _events.WriteAsync(
                    new ExpertSemanticEvent("chunk", JsonSerializer.SerializeToElement(new { text = chunk.Value })),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (ConfiguredPrimaryOutput is TextPrimaryOutput { OnCompleted: { } onCompleted })
            await onCompleted(new TextCompletedEvent(_text.ToString()), cancellationToken).ConfigureAwait(false);
        return new ExpertCompletionResult();
    }

    /// <summary>
    /// Binds one contract-required input property with an actionable diagnostic instead of a bare
    /// <see cref="KeyNotFoundException" /> when the invocation input is incomplete.
    /// </summary>
    private static string RequiredInputString(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : throw new ArgumentException(
                $"The invocation input is missing the required property '{name}' for contract " +
                $"'{Descriptor.Id}'. Required input shape: worldSettings:string, playerInput:string.");
}
