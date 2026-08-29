using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts.Narration;

namespace ThousandLi.TemplateName;

/// <summary>
/// Concrete narrator expert bound to the official <c>thousandli.expert/narrator</c> contract
/// through <see cref="AbstractNarratorExpert" />. It streams plain-text deltas from the locally
/// configured gateway model as <c>chunk</c> semantic events and returns the accumulated narration
/// as the contract output <c>{ text }</c>.
/// </summary>
public sealed class NarratorExpert : AbstractNarratorExpert
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
        await StreamAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { text = _text.ToString() });
    }

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        StreamOnceAsync(cancellationToken);

    private async Task<ExpertCompletionResult> StreamOnceAsync(CancellationToken cancellationToken)
    {
        var turn = RequiredInputProperty("turn").GetInt32();
        var player = RequiredInputProperty("player").GetString();
        var action = RequiredInputProperty("action").GetRawText();
        var request = new LocalBasicAiRequest(
            RuntimeContext.BasicAi.AvailableModels[0],
            [new LocalBasicAiMessage("user", $"Turn {turn} for {player}: narrate the outcome of the action {action}.")]);
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

    /// <summary>
    /// Binds one contract-required input property with an actionable diagnostic instead of a bare
    /// <see cref="KeyNotFoundException" /> when the invocation input is incomplete.
    /// </summary>
    private JsonElement RequiredInputProperty(string name) =>
        _input.TryGetProperty(name, out var value)
            ? value
            : throw new ArgumentException(
                $"The invocation input is missing the required property '{name}' for contract " +
                $"'{AbstractNarratorExpert.Descriptor.Id}'. Required input shape: turn:int, player:string, action:*.");
}
