using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.SampleGame;
using ThousandLi.Testing;
using ExpertCompletionResult = ThousandLi.Contracts.ExpertCompletionResult;
using TextPrimaryOutput = ThousandLi.Contracts.TextPrimaryOutput;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// SampleGame 变量更新管线端到端（Slice 4 批次 ⑥）：reflect 动作经类型化专家门面
/// 与 <c>WithVariableUpdate</c> 接线，由 RecordedBasicAi 录制的第二遍 completion 流驱动
/// 补丁回写托管根、缓存失效重水合；advance 动作回归保护结构化执行端口不被改动。
/// </summary>
public sealed class SampleGameVariableUpdateTests
{
    private const string MainModelId = "sample-narrator-model";
    private const string VariableUpdateModelId = "sample-variable-model";
    private const string MainNarrative = "The road quietly bends toward the hills.";
    private const string ManagedRootPath = "/_gameHelper/sessionVariables";

    [Fact]
    public async Task InitialStateMaterializesManagedSessionStateRoot()
    {
        var state = await CreateInitialStateAsync();

        Assert.Equal(0, state.Get(new JsonPointer("/turn")).GetInt32());
        Assert.Equal("The journey is ready.", state.Get(new JsonPointer("/lastNarrative")).GetString());
        Assert.Equal(0, state.Get(new JsonPointer(ManagedRootPath + "/reflectCount")).GetInt32());
        Assert.Equal(10, state.Get(new JsonPointer(ManagedRootPath + "/courage")).GetInt32());
        Assert.Equal(30, state.Get(new JsonPointer(ManagedRootPath + "/trust")).GetInt32());
    }

    [Fact]
    public async Task AdvanceActionKeepsUsingTheNarratorExecutorPort()
    {
        var backend = new SampleGameBackend();
        var executor = new FakeExpertExecutor([
            new FakeExpertScenario(
                "advance",
                AbstractNarratorExpert.Descriptor,
                [new ExpertSemanticEvent("chunk", TestSupport.Json("""{"text":"A new path opens."}"""))],
                TestSupport.Json("""{"text":"A new path opens."}"""))
        ]);
        var frontend = new CollectingFrontendEventSink();
        var context = ActionContextTestFactory.Create(
            state: await CreateInitialStateAsync(backend),
            frontend: frontend,
            expertExecutor: executor);

        await backend.HandleActionAsync(
            TestSupport.Action("""{"choice":"advance"}"""), context, TestSupport.CancellationToken);

        var invocation = Assert.Single(executor.Invocations);
        Assert.Equal(AbstractNarratorExpert.Descriptor.Id, invocation.Request.Contract.Id);
        Assert.Equal("advance", invocation.Request.ScenarioId);
        Assert.Equal(1, context.State.Get(new JsonPointer("/turn")).GetInt32());
        Assert.Equal("A new path opens.", context.State.Get(new JsonPointer("/lastNarrative")).GetString());
        Assert.Equal(10, context.State.Get(new JsonPointer(ManagedRootPath + "/courage")).GetInt32());
        Assert.Equal(30, context.State.Get(new JsonPointer(ManagedRootPath + "/trust")).GetInt32());
        Assert.Equal(0, context.State.Get(new JsonPointer(ManagedRootPath + "/reflectCount")).GetInt32());
        Assert.Contains(frontend.Events, frontendEvent => frontendEvent.EventType == "chunk");
    }

    [Fact]
    public async Task ReflectActionAppliesSecondPassVariableUpdatesToManagedRoot()
    {
        var backend = new SampleGameBackend();
        var basicAi = CreateRecordedVariableUpdatePipeline();
        var facade = new FakeExpertFacade();
        facade.Register<AbstractLongTextWritingExpert>(() =>
        {
            var expert = new SampleReflectingLongTextExpert();
            expert.Bind(new LocalExpertExecutionContext(
                basicAi,
                new BoundPlayerProfile(new PlayerId("test-player"), "TestPlayer", "Test persona"),
                NullLogger.Instance));
            return expert;
        });
        var frontend = new CollectingFrontendEventSink();
        var context = ActionContextTestFactory.Create(
            state: await CreateInitialStateAsync(backend),
            frontend: frontend,
            experts: facade);

        await backend.HandleActionAsync(
            TestSupport.Action("""{"type":"reflect"}"""), context, TestSupport.CancellationToken);

        // delta 生效、replace 生效、指向仅持久化成员的命令被回滚跳过。
        Assert.Equal(15, context.State.Get(new JsonPointer(ManagedRootPath + "/courage")).GetInt32());
        Assert.Equal(80, context.State.Get(new JsonPointer(ManagedRootPath + "/trust")).GetInt32());
        // 补丁的 replace /reflectCount 999 被拒绝；缓存失效后游戏侧重水合写入的簿记生效。
        Assert.Equal(1, context.State.Get(new JsonPointer(ManagedRootPath + "/reflectCount")).GetInt32());
        // reflect 不推进旅程回合，只更新叙事。
        Assert.Equal(0, context.State.Get(new JsonPointer("/turn")).GetInt32());
        Assert.Equal(MainNarrative, context.State.Get(new JsonPointer("/lastNarrative")).GetString());

        // 主输出以流式前端事件透出。
        var deltas = frontend.Events
            .Where(frontendEvent => frontendEvent.EventType == "narrativeDelta")
            .Select(frontendEvent => frontendEvent.Payload.GetProperty("text").GetString())
            .ToArray();
        Assert.NotEmpty(deltas);
        Assert.Equal(MainNarrative, string.Concat(deltas));

        // 第二遍：一次主调用 + 一次变量更新完成调用，模型与 schema 正确。
        Assert.Equal(2, basicAi.RuntimeInvocations.Count);
        Assert.Equal(MainModelId, basicAi.RuntimeInvocations[0].ModelId);
        var updateInvocation = basicAi.RuntimeInvocations[1];
        Assert.Equal(VariableUpdateModelId, updateInvocation.ModelId);
        Assert.Contains("variableUpdates", updateInvocation.TargetSchema.ToJson().GetRawText(), StringComparison.Ordinal);

        // 提示词由 GameHelper 注入：AI 可见投影（courage/trust）而非存储根，
        // 持久化成员 reflectCount 不出现在状态投影中。
        var prompt = Assert.Single(updateInvocation.Messages).Content;
        AssertSectionsInOrder(prompt);
        Assert.Contains("\"courage\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"trust\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("reflectCount", prompt, StringComparison.Ordinal);
        Assert.Contains(MainNarrative, prompt, StringComparison.Ordinal);
    }

    private static async Task<GameState> CreateInitialStateAsync(SampleGameBackend? backend = null)
    {
        backend ??= new SampleGameBackend();
        var initialState = await backend.CreateInitialStateAsync(
            new BoundPlayerProfile(new PlayerId("test-player"), "TestPlayer", "Test persona"),
            TestSupport.CancellationToken);
        return new GameState(initialState);
    }

    private static RecordedBasicAi CreateRecordedVariableUpdatePipeline()
    {
        const string updateCompletionJson = """
            {"variableUpdates":[
                {"op":"delta","path":"/courage","value":5},
                {"op":"replace","path":"/reflectCount","value":999},
                {"op":"replace","path":"/trust","value":80}]}
            """;
        return new RecordedBasicAi(
            [MainModelId, VariableUpdateModelId],
            [],
            [
                new RecordedRuntimeBasicAiInteraction(
                    MainModelId,
                    streamEvents:
                    [
                        new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.PropertyName("/narrative", "narrative")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.StringStarted("/narrative")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "The road ")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "quietly bends toward the hills.")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/narrative")),
                        new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectCompleted("")),
                    ]),
                new RecordedRuntimeBasicAiInteraction(
                    VariableUpdateModelId,
                    completion: new BasicAiCompletionResult(TestSupport.Json(updateCompletionJson))),
            ]);
    }

    private static void AssertSectionsInOrder(string prompt)
    {
        var basic = prompt.IndexOf("<BasicInformation>", StringComparison.Ordinal);
        var previous = prompt.IndexOf("<PreviousState>", StringComparison.Ordinal);
        var information = prompt.IndexOf("<NewInformation>", StringComparison.Ordinal);
        var schema = prompt.IndexOf("<StateSchema>", StringComparison.Ordinal);
        var task = prompt.IndexOf("<Task>", StringComparison.Ordinal);
        Assert.True(basic >= 0 && basic < previous && previous < information && information < schema && schema < task);
    }

    /// <summary>
    /// 测试组合根提供的具体长文本写作专家（派生 <see cref="RuntimeLongTextWritingExpertBase" />）：
    /// StreamAsync 内构建主调用 request+sink，并把 Game 传入的 VariableUpdateFeature
    /// 显式交给共享的 ExpertVariableUpdateExecution 第二遍。平台语义中具体专家来自独立
    /// Expert 包；测试与本地组合根负责实例化与绑定。
    /// </summary>
    private sealed class SampleReflectingLongTextExpert : RuntimeLongTextWritingExpertBase
    {
        public override async Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default)
        {
            ValidateCategoryInputs();
            var textOutput = (TextPrimaryOutput)(ConfiguredPrimaryOutput
                ?? throw new InvalidOperationException("The reflecting expert requires a text primary output."));
            var request = new BasicAiRequest(
                MainModelId,
                [BasicAiMessage.User("Advance the sample narrative.")],
                AiJsonSchema.Object(AiJsonSchema.Required("narrative", AiJsonSchema.String())));
            var sink = new NarrativeSink(textOutput);

            var variableUpdate = ConfiguredFeatures.OfType<VariableUpdateFeature>().FirstOrDefault();
            if (variableUpdate is null)
            {
                return await ExpertExecution.StreamOnceAsync(this, request, sink, cancellationToken)
                    .ConfigureAwait(false);
            }

            var updateContext = new ExpertVariableUpdateContext(
                RequireConfigured(WorldSettings, "world settings"),
                RequireConfigured(CurrentState, "current state"),
                RequireConfigured(StateSchema, "state schema"),
                VariableUpdateModelId);
            return await ExpertVariableUpdateExecution
                .StreamAsync(this, request, sink, variableUpdate, updateContext, cancellationToken)
                .ConfigureAwait(false);
        }

        public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The reflecting expert only exercises streaming.");

        private static string RequireConfigured(string? value, string name)
            => value ?? throw new InvalidOperationException($"The reflecting expert requires {name}.");
    }

    /// <summary>把主输出属性的字符串增量转发给 TextPrimaryOutput callback 的白名单 sink。</summary>
    private sealed class NarrativeSink(TextPrimaryOutput primaryOutput)
        : JsonExpertStreamEventSinkBase([primaryOutput.PropertyName])
    {
        protected override async ValueTask OnDeclaredEventAsync(
            JsonStreamEvent streamEvent,
            CancellationToken cancellationToken)
        {
            if (streamEvent is JsonStreamStringChunkEvent chunk &&
                MatchPathExact(streamEvent, primaryOutput.PropertyName))
            {
                await primaryOutput.OnDelta(new TextDeltaEvent(chunk.Value), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
