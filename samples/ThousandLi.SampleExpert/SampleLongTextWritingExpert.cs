using System.Text;
using ThousandLi.Contracts;

namespace ThousandLi.SampleExpert;

/// <summary>
/// Sample concrete long-text-writing expert for the official
/// <c>thousandli.expert/long-text-writing</c> category contract. It streams the model's narrative
/// JSON string chunks from the locally configured gateway model to the configured text primary
/// output — mirroring how a real long-text-writing expert works.
/// </summary>
public sealed class SampleLongTextWritingExpert : AbstractLongTextWritingExpert
{
    private readonly StringBuilder _text = new();

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    /// <inheritdoc />
    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    private async Task<ExpertCompletionResult> StreamOnceAsync(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
        _text.Clear();
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
        }

        if (ConfiguredPrimaryOutput is TextPrimaryOutput { OnCompleted: { } onCompleted })
            await onCompleted(new TextCompletedEvent(_text.ToString()), cancellationToken).ConfigureAwait(false);
        return new ExpertCompletionResult();
    }
}
