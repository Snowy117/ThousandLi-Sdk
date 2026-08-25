using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.GameAuthoring;

namespace ThousandLi.TemplateName;

public sealed class GameBackend : IGameBackend
{
    public ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playerProfile);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { turn = 0, lastNarrative = "Ready." }));
    }

    public async ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default)
    {
        var turn = context.State.Get(new JsonPointer("/turn")).GetInt32() + 1;
        var sink = new DelegateExpertSemanticEventSink(async (semanticEvent, token) =>
            await context.Frontend.WriteAsync(semanticEvent.EventType, semanticEvent.Payload, token).ConfigureAwait(false));
        var result = await context.Experts.ExecuteAsync(
            new ExpertInvocationRequest(
                AbstractNarratorExpert.Descriptor,
                "advance",
                JsonSerializer.SerializeToElement(
                    new { turn, player = context.PlayerProfile.PlayerName, action = action.Payload })),
            sink,
            cancellationToken).ConfigureAwait(false);
        context.State.Replace("/turn", turn);
        context.State.Replace("/lastNarrative", result.Output.GetProperty("text").GetString());
    }

    public ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        FrontendRequestContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FrontendRequestResult(context.State.Snapshot));
    }
}
