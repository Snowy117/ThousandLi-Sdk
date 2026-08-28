using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.GameAuthoring;
using ThousandLi.GameHelper;

namespace ThousandLi.SampleGame;

/// <summary>
/// Sample 游戏 backend。<c>advance</c> 演示结构化 <see cref="IExpertExecutor" /> 端口
/// （ExpertContracts 场景），<c>reflect</c> 演示类型化 <see cref="AbstractLongTextWritingExpert" />
/// 门面加 GameHelper 托管变量更新管线。
/// </summary>
public sealed class SampleGameBackend : IGameBackend
{
    private const string WorldSettings =
        "A quiet journey along a long road. The protagonist travels with a reliable guide.";

    public ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playerProfile);
        cancellationToken.ThrowIfCancellationRequested();
        // GameHelper 是权威变量存储：托管根由 MaterializeSessionState 物化，
        // 游戏自有的展示字段保留在状态根，与托管根不重复。
        var managedRoot = SessionStateExtensions.MaterializeSessionState<SampleGameVariables>(playerProfile);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        (StringComparer.Ordinal)
        {
            ["turn"] = 0,
            ["lastNarrative"] = "The journey is ready.",
            ["_gameHelper"] = managedRoot.GetProperty("_gameHelper"),
        }));
    }

    public async ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        if (IsReflectAction(action))
        {
            await HandleReflectAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await HandleAdvanceAsync(action, context, cancellationToken).ConfigureAwait(false);
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

    private static bool IsReflectAction(PlayerActionEnvelope action) =>
        action.Payload.ValueKind == JsonValueKind.Object &&
        action.Payload.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "reflect", StringComparison.Ordinal);

    private static async ValueTask HandleAdvanceAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken)
    {
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

    private static async ValueTask HandleReflectAsync(
        ActionContext context,
        CancellationToken cancellationToken)
    {
        // 读取（必要时挂载）托管根：typed 读取为提示词提供当前变量值。
        var variables = context.GetSessionState<SampleGameVariables>();
        var narrative = new StringBuilder();

        // 一行接线变量更新：GameHelper 注入 AI 可见当前状态与 schema，
        // 并在专家第二遍提出补丁后回写托管根、使 typed-root 缓存失效。
        await context.Experts
            .Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings(WorldSettings)
            .WithPlayerInput($"reflect on the journey so far (courage {variables.Courage}, trust {variables.Trust})")
            .WithPrimaryOutput(new TextPrimaryOutput((delta, token) =>
            {
                narrative.Append(delta.Delta);
                return context.Frontend.WriteAsync("narrativeDelta", new { text = delta.Delta }, token);
            }))
            .WithVariableUpdate<SampleGameVariables>(context)
            .StreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // 补丁应用后缓存已失效：这里重新水合的是补丁后状态，游戏侧簿记在补丁后进行。
        var patched = context.GetSessionState<SampleGameVariables>();
        patched.ReflectCount += 1;
        context.State.Replace("/lastNarrative", narrative.ToString());
    }
}
