using JetBrains.Annotations;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertFixture.RecordedLongTextWritingExpert))]

namespace ThousandLi.LocalExpertFixture;

/// <summary>
/// Test-fixture long-text-writing expert. Binds the structured invocation input, executes through
/// the category anchor's streaming entry (which enforces the bind-once and execute-once lifecycle),
/// streams narrative JSON string chunks from the injected runtime BasicAi as 'chunk' semantic
/// events, and returns the accumulated text plus a per-invocation instance id (the id differs
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

public sealed class RecordedLongTextWritingExpert : AbstractLongTextWritingExpert, IInvocableExpert
{
    private JsonElement _input;
    private IExpertSemanticEventSink _events = null!;
    private readonly StringBuilder _text = new();

    public async Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        _input = input.Clone();
        _events = events;
        _text.Clear();
        WithWorldSettings(RequiredString("worldSettings"));
        WithPlayerInput(RequiredString("playerInput"));
        var settings = await RuntimeContext.GetExpertSettingsAsync<RecordedLongTextWritingSettings>(cancellationToken)
            .ConfigureAwait(false);
        var greeting = settings.Greeting;
        await StreamAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            text = _text.ToString(),
            greeting,
            instanceId = Guid.NewGuid().ToString("N")
        });
    }

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    private async Task<ExpertCompletionResult> StreamOnceAsync(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
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
            if (_events is not null)
            {
                await _events.WriteAsync(
                    new ExpertSemanticEvent("chunk", JsonSerializer.SerializeToElement(new { text = chunk.Value })),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new ExpertCompletionResult();
    }

    private string RequiredString(string name) =>
        _input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ArgumentException(
                $"The invocation input is missing the required property '{name}'. " +
                "Required input shape: worldSettings:string, playerInput:string.");
}
