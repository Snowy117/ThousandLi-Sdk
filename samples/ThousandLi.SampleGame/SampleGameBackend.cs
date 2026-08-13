using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.GameAuthoring;

namespace ThousandLi.SampleGame;

public sealed class SampleGameBackend : IGameBackend
{
    public static ExpertContractDescriptor NarratorContract { get; } =
        new("thousandli.sample/narrator", new ContractVersion(1, 0), "sample-narrator-v1");

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
        var result = await context.Experts.ExecuteAsync(
            new ExpertInvocationRequest(NarratorContract, "advance", input),
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
