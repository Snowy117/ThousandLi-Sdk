using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.GameHelper;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

[GameSettings]
public sealed class DogfoodSettings
{
    [GameSettingsMember("时间标签", "短描述", "长描述")]
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global — STJ 从设置存储反序列化时需要 setter
    public bool EnableTimeTags { get; init; } = true;
}

[SessionStateRoot]
public class DogfoodSessionVariables
{
    [SessionStateMember] public virtual string? PlayerName { get; set; }
    [SessionStateMember] public virtual long Visits { get; set; }
}

/// <summary>
/// Xuezhixia 风格 DevHost 狗粮后端：一次 action 内同时使用历史桶、GameSettings、
/// GameHelper 类型化 SessionState 与长文本专家 facade（假专家），并提交状态。
/// </summary>
public sealed class DogfoodBackend : IGameBackend
{
    private const string WorldSettings = "雪之下测试世界";
    private const string PlayerInput = "继续前进";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<string> _options = [];

    /// <summary>最近一次 / 累计 action 中假专家回报的行动选项（供测试断言）。</summary>
    public IReadOnlyList<string> LastOptions => _options;

    public ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TestSupport.Json("""{"turn":0,"lastNarrative":"","timeTagsEnabled":false}"""));

    public async ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default)
    {
        var bucket = EnsureBucket(context);
        bucket.AddMessages(null, null, ChatMessage.User(PlayerInput));

        var settings = await context.GetGameSettingsAsync<DogfoodSettings>(cancellationToken);

        var variables = context.GetSessionState<DogfoodSessionVariables>();
        variables.Visits += 1;
        variables.PlayerName = context.PlayerProfile.PlayerName;

        var narrative = new StringBuilder();
        var expert = context.Experts.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings(WorldSettings)
            .WithPlayerInput(PlayerInput)
            .WithPlayerPersona(context.PlayerProfile.Persona)
            .WithCurrentState(context.State.Snapshot.GetRawText())
            .WithStateSchema("{}")
            .WithHistoryBuckets(bucket)
            .WithFeatures(
                new TimeTagsFeature(onStartTag: null, onEndTag: null),
                new ActionOptionsFeature(6, (option, _) =>
                {
                    _options.Add(option.Text);
                    return ValueTask.CompletedTask;
                }))
            .WithPrimaryOutput(new TextPrimaryOutput(
                async (delta, token) =>
                {
                    narrative.Append(delta.Delta);
                    await context.Frontend.WriteAsync(
                        new FrontendEvent("narrative", TestSupport.Json($$"""{"text":"{{delta.Delta}}"}""")),
                        token);
                },
                onCompleted: async (completedEvent, token) =>
                {
                    await context.Frontend.WriteAsync(
                        new FrontendEvent(
                            "narrativeCompleted",
                            TestSupport.Json($$"""{"text":"{{completedEvent.Text}}"}""")),
                        token);
                }));
        var result = await expert.StreamAsync(cancellationToken);

        bucket.AddMessages(null, result.Metadata, ChatMessage.Assistant(narrative.ToString()));

        var turn = context.State.Get(new JsonPointer("/turn")).GetInt32() + 1;
        context.State.Replace(new JsonPointer("/turn"), TestSupport.Json(turn.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        context.State.Replace(new JsonPointer("/lastNarrative"),
            TestSupport.Json(JsonSerializer.Serialize(narrative.ToString())));
        context.State.Replace(new JsonPointer("/timeTagsEnabled"), TestSupport.Json(settings.EnableTimeTags ? "true" : "false"));
    }

    public ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        FrontendRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var turns = context.Buckets["main"].GetRawTurns();
        var variables = context.GetSessionState<DogfoodSessionVariables>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            turn = context.State.Get(new JsonPointer("/turn")).GetInt32(),
            bucketTurns = turns.Count,
            lastTurnMetadata = turns.Count > 0
                ? turns[^1].Metadata?.GetValueOrDefault("historyTurns")
                : null,
            visits = variables.Visits,
            playerName = variables.PlayerName
        }, WebJsonOptions);
        return ValueTask.FromResult(new FrontendRequestResult(payload));
    }

    private static IHistoryBucket EnsureBucket(ActionContext context)
    {
        try
        {
            return context.Buckets["main"];
        }
        catch (KeyNotFoundException)
        {
            context.Buckets.Create("main", "主叙事历史");
            return context.Buckets["main"];
        }
    }
}

public sealed class DevHostDogfoodingTests
{
    private static BoundPlayerProfile Player { get; } =
        new(new PlayerId("player-1"), "Creator", "Curious explorer");

    private static ScriptedLongTextWritingScenario DefaultScenario() => new(
        "default",
        TestSupport.Json("""
            {
              "narrative": "A New Path Opens.",
              "actionOptions": ["Continue", "Flee"],
              "timeTagStart": "2026",
              "timeTagEnd": "2027",
              "afterThinking": "thought",
              "afterFormat": "formatted"
            }
            """),
        metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["extraKey"] = "extra" },
        reasoning: "reasoned");

    [Fact]
    public async Task DogfoodBackendStreamsScriptedEventsAndCommitsThroughDevHostEndToEnd()
    {
        var buckets = new InMemoryHistoryBucketSet();
        var settingsStore = new InMemoryGameSettingsStore();
        await settingsStore.SetAsync(TestSupport.Json("""{"enableTimeTags":false}"""), TestSupport.CancellationToken);
        var facade = new ScriptedLongTextWritingExpertFacade([DefaultScenario()]);
        var backend = new DogfoodBackend();
        var runtime = await LocalGameRuntime.CreateAsync(
            "tests_game@1.0.0",
            backend,
            Player,
            new ThrowingExpertExecutor(),
            new InMemoryLocalSessionStore(),
            new SessionId("dogfood-1"),
            expertFacade: facade,
            buckets: buckets,
            gameSettingsStore: settingsStore,
            cancellationToken: TestSupport.CancellationToken);

        var events = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action("""{"choice":"advance"}"""), TestSupport.CancellationToken));

        // NDJSON 流：Started → 主输出 chunk 前端事件 + narrativeCompleted → Committed。
        Assert.Equal(ActionRuntimeEventKind.Started, events[0].Kind);
        Assert.Equal(ActionRuntimeEventKind.Committed, events[^1].Kind);
        var frontends = events.Where(runtimeEvent => runtimeEvent.Kind == ActionRuntimeEventKind.FrontendEvent).ToArray();
        Assert.Equal(6, frontends.Length);
        Assert.Equal("narrative", frontends[0].FrontendEvent!.EventType);
        Assert.Equal("narrativeCompleted", frontends[^1].FrontendEvent!.EventType);

        var committed = runtime.CommittedState;
        Assert.Equal(1, committed.GetProperty("turn").GetInt32());
        Assert.Equal("A New Path Opens.", committed.GetProperty("lastNarrative").GetString());
        Assert.False(committed.GetProperty("timeTagsEnabled").GetBoolean());
        var variables = committed.GetProperty("_gameHelper").GetProperty("sessionVariables");
        Assert.Equal(1, variables.GetProperty("visits").GetInt32());
        Assert.Equal("Creator", variables.GetProperty("playerName").GetString());
        Assert.Equal(["Continue", "Flee"], backend.LastOptions);

        // 提交后的 bucket 账本：user 回合 + 携带专家 metadata 的 assistant 回合。
        var turns = buckets["main"].GetRawTurns();
        Assert.Equal(2, turns.Count);
        Assert.Equal(ChatMessageRole.Assistant, turns[1].Messages[0].Role);
        Assert.Equal("A New Path Opens.", turns[1].Messages[0].Content);
        Assert.Equal("1", turns[1].Metadata!["historyTurns"]);
        Assert.Equal("thought", turns[1].Metadata!["afterThinking"]);
        Assert.Equal("extra", turns[1].Metadata!["extraKey"]);

        // 前端请求读到已提交 bucket 快照 + 类型化 SessionState 快照。
        var response = await runtime.HandleFrontendRequestAsync(
            new FrontendRequestEnvelope(TestSupport.PlayerId, TestSupport.Json("""{"request":"state"}""")),
            TestSupport.CancellationToken);
        Assert.Equal(2, response.Payload.GetProperty("bucketTurns").GetInt32());
        Assert.Equal(1, response.Payload.GetProperty("turn").GetInt32());
        Assert.Equal("1", response.Payload.GetProperty("lastTurnMetadata").GetString());
        Assert.Equal(1, response.Payload.GetProperty("visits").GetInt32());
        Assert.Equal("Creator", response.Payload.GetProperty("playerName").GetString());

        // 第二次 action 复用同一会话与桶，序号与状态继续推进。
        var second = await TestSupport.CollectAsync(runtime.HandleActionAsync(
            TestSupport.Action(), TestSupport.CancellationToken));
        Assert.Equal(ActionRuntimeEventKind.Committed, second[^1].Kind);
        Assert.Equal(2, runtime.CommittedState.GetProperty("turn").GetInt32());
        Assert.Equal(4, buckets["main"].GetRawTurns().Count);
        Assert.Equal(2, runtime.CommittedState.GetProperty("_gameHelper").GetProperty("sessionVariables").GetProperty("visits").GetInt32());
    }
}
