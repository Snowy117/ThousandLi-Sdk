using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
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
        var narrative = new StringBuilder();
        await context.Experts
            .Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("A quiet journey along a long road.")
            .WithPlayerInput($"turn {turn}: {action.Payload.GetRawText()}")
            .WithPrimaryOutput(new TextPrimaryOutput((delta, token) =>
            {
                narrative.Append(delta.Delta);
                return context.Frontend.WriteAsync(
                    "narrativeDelta", JsonSerializer.SerializeToElement(new { text = delta.Delta }), token);
            }))
            .StreamAsync(cancellationToken).ConfigureAwait(false);
        context.State.Replace("/turn", turn);
        context.State.Replace("/lastNarrative", narrative.ToString());
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
