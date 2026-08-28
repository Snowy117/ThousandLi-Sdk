using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper.Tests;

public sealed class VariableUpdateFeatureGameCallbackTests
{
    [Fact]
    public async Task OnPatchProposed_AppliesProposalToManagedSessionState()
    {
        var state = new GameState(Json(
            """
            {
              "_gameHelper": {
                "sessionVariables": {
                  "name": "Alice",
                  "stats": { "level": 3 },
                  "tags": [],
                  "flags": {}
                }
              }
            }
            """));
        var contract = SessionStateContract.Create(typeof(TestSessionState));
        VariableUpdateApplyResult? applyResult = null;
        var feature = new VariableUpdateFeature((proposal, _) =>
        {
            applyResult = SessionStateExtensions.ApplyVariableUpdateToManagedRoot(
                state,
                contract,
                proposal.Operations);
            return ValueTask.CompletedTask;
        });
        await feature.OnPatchProposed(new VariableUpdatePatchProposal(
            [new VariableUpdateReplace("/stats/level", JsonValue.Create(4))]), CancellationToken.None);

        Assert.NotNull(applyResult);
        Assert.Equal(0, applyResult.FailedOperationCount);
        Assert.Equal(4, state.Snapshot.GetProperty("_gameHelper")
            .GetProperty("sessionVariables")
            .GetProperty("stats")
            .GetProperty("level")
            .GetInt32());

        // The typed view over the same managed root must observe the applied patch.
        var context = Support.BuildActionContext(state);
        var hydrated = context.GetSessionState<TestSessionState>();
        Assert.Equal("Alice", hydrated.Name);
        Assert.Equal(4, hydrated.Stats.Level);
        Assert.Empty(hydrated.Tags);
        Assert.Empty(hydrated.Flags);
    }

    [Fact]
    public async Task WithVariableUpdate_InjectsAiProjectionAndSchemaAndAppliesOnlyValidatorApprovedOperations()
    {
        var state = new GameState(SessionStateExtensions.MaterializeSessionState<AutomaticVariables>(Support.Player));
        var context = Support.BuildActionContext(state);
        var expert = new CapturingLongTextWritingExpert();

        var configured = expert.WithVariableUpdate<AutomaticVariables>(
            context,
            operation => !string.Equals(operation.Path, "/score", StringComparison.Ordinal));

        Assert.Same(expert, configured);
        Assert.NotNull(expert.CurrentStateForTest);
        Assert.Equal("""{"score":1,"title":"initial"}""", expert.CurrentStateForTest);
        Assert.Equal(SessionStateContract.Create(typeof(AutomaticVariables)).StateSchema, expert.StateSchemaForTest);

        await expert.VariableUpdateFeatureForTest.OnPatchProposed(
            new VariableUpdatePatchProposal(
            [
                new VariableUpdateReplace("/score", JsonValue.Create(99)),
                new VariableUpdateReplace("/title", JsonValue.Create("updated")),
            ]),
            CancellationToken.None);

        var variables = state.Snapshot.GetProperty("_gameHelper").GetProperty("sessionVariables");
        Assert.Equal(1, variables.GetProperty("score").GetInt32());
        Assert.Equal("updated", variables.GetProperty("title").GetString());
        Assert.Equal("secret", variables.GetProperty("privateNote").GetString());

        var refreshed = context.GetSessionState<AutomaticVariables>();
        Assert.Equal(1, refreshed.Score);
        Assert.Equal("updated", refreshed.Title);
        Assert.Equal("secret", refreshed.PrivateNote);

        refreshed.Score = 2;
        refreshed.Title = "locally-updated";
        refreshed.PrivateNote = "locally-secret";
        Assert.Equal(2, refreshed.Score);
        Assert.Equal("locally-updated", refreshed.Title);
        Assert.Equal("locally-secret", refreshed.PrivateNote);
    }

    [Fact]
    public void ApplyVariableUpdateToManagedRoot_SkipsInvalidCommandAndCommitsLaterValidCommand()
    {
        var state = new GameState(Json(
            """
            {
              "_gameHelper": {
                "sessionVariables": {
                  "name": "Alice",
                  "stats": { "level": 3 },
                  "tags": [],
                  "flags": {}
                }
              }
            }
            """));
        var contract = SessionStateContract.Create(typeof(TestSessionState));
        var result = SessionStateExtensions.ApplyVariableUpdateToManagedRoot(state, contract,
        [
            new VariableUpdateReplace("/missing", JsonValue.Create(1)),
            new VariableUpdateReplace("/stats/level", JsonValue.Create(4)),
        ]);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, static warning => warning.Contains("/missing", StringComparison.Ordinal));
        Assert.Equal(4, state.Snapshot.GetProperty("_gameHelper")
            .GetProperty("sessionVariables")
            .GetProperty("stats")
            .GetProperty("level")
            .GetInt32());
        var applied = Assert.IsType<VariableUpdateReplace>(Assert.Single(result.AppliedPatch));
        Assert.Equal("/stats/level", applied.Path);
    }

    [SessionStateRoot]
    public class AutomaticVariables
    {
        [AiStateMember(0, "分数")] public virtual int Score { get; set; } = 1;

        [AiStateMember(1, "标题")] public virtual string Title { get; set; } = "initial";

        [SessionStateMember] public virtual string PrivateNote { get; set; } = "secret";
    }

    // ReSharper disable AutoPropertyCanBeMadeGetOnly.Global, UnusedAutoPropertyAccessor.Global — SessionState 契约形状，由 Castle 跟踪代理读写
    [SessionStateRoot]
    public class TestSessionState
    {
        [AiStateMember(0, "名称")] public virtual string Name { get; set; } = "Alice";

        [AiStateMember(1, "属性")] public virtual StatsVariables Stats { get; set; } = new();

        [AiStateMember(2, "标签")] public virtual TrackedList<string> Tags { get; set; } = [];

        [AiStateMember(3, "旗标")] public virtual TrackedDictionary<int> Flags { get; set; } = [];
    }
    // ReSharper restore AutoPropertyCanBeMadeGetOnly.Global, UnusedAutoPropertyAccessor.Global

    // ReSharper disable ClassWithVirtualMembersNeverInherited.Global, UnusedAutoPropertyAccessor.Global — 由 Castle 运行时代理继承并反射读写
    public class StatsVariables
    {
        [AiStateMember(0, "等级")] public virtual int Level { get; set; }
    }
    // ReSharper restore ClassWithVirtualMembersNeverInherited.Global, UnusedAutoPropertyAccessor.Global

    // ReSharper disable once ClassWithVirtualMembersNeverInherited.Local — 测试替身仅捕获 fluent 配置，不被继承
    private sealed class CapturingLongTextWritingExpert : AbstractLongTextWritingExpert
    {
        public string? CurrentStateForTest => CurrentState;

        public string? StateSchemaForTest => StateSchema;

        public VariableUpdateFeature VariableUpdateFeatureForTest =>
            Assert.IsType<VariableUpdateFeature>(Assert.Single(ConfiguredFeatures));

        public override Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExpertCompletionResult());
        }

        public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExpertCompletionResult());
        }
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
