using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.GameAuthoring;

namespace ThousandLi.SampleGame;

public sealed class SampleGameBackend : IGameBackend
{
    public ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playerProfile);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
        {
            turn = 0,
            lastNarrative = "The journey is ready."
        }));
    }

    public async ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var currentTurn = context.State.Get(new JsonPointer("/turn")).GetInt32();
        var input = JsonSerializer.SerializeToElement(new
        {
            turn = currentTurn + 1,
            player = context.PlayerProfile.PlayerName,
            action = action.Payload
        });
        var semanticEvents = new DelegateExpertSemanticEventSink(async (semanticEvent, token) =>
            await context.Frontend.WriteAsync(
                semanticEvent.EventType,
                semanticEvent.Payload,
                token).ConfigureAwait(false));
        var result = await context.ExpertExecutor.ExecuteAsync(
            new ExpertInvocationRequest(AbstractNarratorExpert.Descriptor, "advance", input),
            semanticEvents,
            cancellationToken).ConfigureAwait(false);
        context.State.Replace("/turn", currentTurn + 1);
        context.State.Replace("/lastNarrative", result.Output.GetProperty("text").GetString());
    }

    public ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        FrontendRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FrontendRequestResult(context.State.Snapshot));
    }
}
