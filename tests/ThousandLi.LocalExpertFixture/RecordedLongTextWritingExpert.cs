using JetBrains.Annotations;
using System.Text;
using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertFixture.RecordedLongTextWritingExpert))]

namespace ThousandLi.LocalExpertFixture;

/// <summary>
/// Test-fixture long-text-writing expert. Executes through the category anchor's streaming entry
/// (which enforces the bind-once and execute-once lifecycle), streams narrative JSON string chunks
/// from the injected runtime BasicAi as 'chunk' semantic events (via the structured invoker's wire
/// callbacks), and returns the accumulated text plus a per-invocation instance id (the id differs
/// across invocations, proving the per-invocation instance lifecycle). Doubles as a local
/// two-layer settings probe: the POCO defaults are the schema-default layer; a local
/// override file wired through the executor options replaces individual values.
/// </summary>
// Deserialized through System.Text.Json reflection; members are set by the serializer.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class RecordedLongTextWritingSettings
{
    public string Greeting { get; init; } = "hi";
}

public sealed class RecordedLongTextWritingExpert : AbstractLongTextWritingExpert
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
        var settings = await RuntimeContext.GetExpertSettingsAsync<RecordedLongTextWritingSettings>(cancellationToken)
            .ConfigureAwait(false);
        var request = new BasicAiRequest(
            RuntimeContext.BasicAi.AvailableModels[0].ModelId,
            [BasicAiMessage.User($"world: {WorldSettings}{Environment.NewLine}input: {PlayerInput}")],
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

        // The structured invoker aggregates the completion frame from the primary output, so the
        // settings probe and the per-invocation instance id travel as turn metadata.
        return new ExpertCompletionResult(metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["greeting"] = settings.Greeting,
            ["instanceId"] = Guid.NewGuid().ToString("N")
        });
    }
}
