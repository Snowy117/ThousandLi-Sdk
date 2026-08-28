using JetBrains.Annotations;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts.Narration;
using ExpertCompletionResult = ThousandLi.ExpertAuthoring.ExpertCompletionResult;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractNarratorExpert),
    typeof(ThousandLi.LocalExpertFixture.RecordedNarratorExpert))]

namespace ThousandLi.LocalExpertFixture;

/// <summary>
/// Test-fixture narrator expert. Binds the structured invocation input, executes through the
/// authoring base's streaming entry (which enforces the bind-once and execute-once lifecycle),
/// streams plain-text deltas from the injected local BasicAi as 'chunk' semantic events, and
/// returns the accumulated text plus a per-invocation instance id (the id differs across
/// invocations, proving the per-invocation instance lifecycle).
/// </summary>
/// <summary>
/// Local two-layer settings probe: the POCO defaults are the schema-default layer; a local
/// override file wired through the executor options replaces individual values.
/// </summary>
// Deserialized through System.Text.Json reflection; members are set by the serializer.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class RecordedNarratorSettings
{
    public string Greeting { get; init; } = "hi";
}

public sealed class RecordedNarratorExpert : AbstractNarratorExpert
{
    private JsonElement _input;
    private IExpertSemanticEventSink _events = null!;
    private readonly StringBuilder _text = new();

    public override async Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        _input = input.Clone();
        _events = events;
        _text.Clear();
        var settings = await RuntimeContext.GetExpertSettingsAsync<RecordedNarratorSettings>(cancellationToken)
            .ConfigureAwait(false);
        await StreamAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            text = _text.ToString(),
            greeting = settings.Greeting,
            instanceId = Guid.NewGuid().ToString("N")
        });
    }

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    private async Task<ExpertCompletionResult> StreamOnceAsync(CancellationToken cancellationToken)
    {
        var turn = _input.GetProperty("turn").GetInt32();
        var player = _input.GetProperty("player").GetString();
        var request = new LocalBasicAiRequest(
            RuntimeContext.BasicAi.AvailableModels[0],
            [new LocalBasicAiMessage("user", $"turn {turn}: {player}")]);
        await foreach (var streamEvent in RuntimeContext.BasicAi
                           .StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (streamEvent is not ExpertTextDeltaEvent delta)
                continue;
            _text.Append(delta.Delta);
            await _events.WriteAsync(
                new ExpertSemanticEvent("chunk", JsonSerializer.SerializeToElement(new { text = delta.Delta })),
                cancellationToken).ConfigureAwait(false);
        }

        return new ExpertCompletionResult();
    }
}
