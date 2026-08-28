using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed partial class ExpertVariableUpdateExecutionTests
{
    private static readonly IReadOnlySet<string> SEmptyFields = new HashSet<string>(StringComparer.Ordinal);

    private static readonly BasicAiRequest SMainRequest = new(
        "main-model",
        [BasicAiMessage.User("main")],
        AiJsonSchema.Object());

    private static readonly ExpertVariableUpdateContext SContext = new(
        "world-rules",
        "previous-state",
        "state-schema",
        "update-model");

    [Fact]
    public async Task CompleteAsync_WithFeatureBuildsRequestAndDeliversWholeProposal()
    {
        var basicAi = new CannedBasicAi(completions:
        [
            Completion(
                """{"narrative":"story"}""",
                reasoning: "provider reasoning"),
            Completion(
                """{"variableUpdates":[{"op":"replace","path":"/name","value":"new"},{"op":"delta","path":"/coins","value":2},{"op":"insert","path":"/items/-","value":"key"},{"op":"remove","path":"/temporary"}]}"""),
        ]);
        var expert = CreateBoundExpert(basicAi);
        VariableUpdatePatchProposal? proposal = null;
        var callbackCount = 0;
        var feature = new VariableUpdateFeature((value, _) =>
        {
            callbackCount++;
            proposal = value;
            return ValueTask.CompletedTask;
        });

        var result = await ExpertVariableUpdateExecution.CompleteAsync(
            expert, SMainRequest, new RecordingSink(), feature, SContext, TestSupport.CancellationToken);

        Assert.Equal("provider reasoning", result.Reasoning);
        Assert.Equal(2, basicAi.CompletionRequests.Count);
        var request = basicAi.CompletionRequests[1];
        Assert.Equal("update-model", request.ModelId);
        var targetSchema = Assert.IsType<AiObjectSchema>(request.TargetSchema);
        var property = Assert.Single(targetSchema.Properties);
        Assert.Equal("variableUpdates", property.Name);
        Assert.False(property.Optional);
        Assert.IsType<AiArraySchema>(property.Schema);

        var prompt = Assert.Single(request.Messages).Content;
        AssertSectionsInOrder(prompt);
        Assert.Contains("world-rules", prompt, StringComparison.Ordinal);
        Assert.Contains("previous-state", prompt, StringComparison.Ordinal);
        Assert.Contains("state-schema", prompt, StringComparison.Ordinal);
        Assert.Contains("provider reasoning\n{\"narrative\":\"story\"}", prompt, StringComparison.Ordinal);
        Assert.Contains("replace、delta、insert、remove", prompt, StringComparison.Ordinal);
        Assert.Contains("以 _ 开头", prompt, StringComparison.Ordinal);

        var actualProposal = Assert.IsType<VariableUpdatePatchProposal>(proposal);
        Assert.Equal(1, callbackCount);
        Assert.Collection(
            actualProposal.Operations,
            operation =>
            {
                var replace = Assert.IsType<VariableUpdateReplace>(operation);
                Assert.Equal("/name", replace.Path);
                var value = Assert.IsType<System.Text.Json.Nodes.JsonValue>(replace.Value, exactMatch: false);
                Assert.Equal("new", value.GetValue<string>());
            },
            operation => Assert.Equal(2m, Assert.IsType<VariableUpdateDelta>(operation).Value),
            operation => Assert.Equal("/items/-", Assert.IsType<VariableUpdateInsert>(operation).Path),
            operation => Assert.Equal("/temporary", Assert.IsType<VariableUpdateRemove>(operation).Path));
    }

    [Fact]
    public async Task StreamAsync_WithFeatureRecordsMainJsonAndForwardsEvents()
    {
        var mainEvents = MainStreamEvents("streamed story", "stream reasoning");
        var basicAi = new CannedBasicAi(
            streams: [mainEvents],
            completions: [Completion("""{"variableUpdates":[{"op":"remove","path":"/old"}]}""")]);
        var sink = new RecordingSink();
        var proposals = new List<VariableUpdatePatchProposal>();

        var result = await ExpertVariableUpdateExecution.StreamAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            sink,
            new VariableUpdateFeature((proposal, _) =>
            {
                proposals.Add(proposal);
                return ValueTask.CompletedTask;
            }),
            SContext,
            TestSupport.CancellationToken);

        Assert.Equal("stream reasoning", result.Reasoning);
        Assert.Equal(["streamed story"], sink.GetChunks("/narrative"));
        Assert.Single(basicAi.StreamRequests);
        var updateRequest = Assert.Single(basicAi.CompletionRequests);
        Assert.Equal("update-model", updateRequest.ModelId);
        var prompt = Assert.Single(updateRequest.Messages).Content;
        Assert.Contains("stream reasoning\n{\"narrative\":\"streamed story\"}", prompt, StringComparison.Ordinal);
        Assert.Equal("/old", Assert.IsType<VariableUpdateRemove>(Assert.Single(Assert.Single(proposals).Operations)).Path);
    }

    [Fact]
    public async Task CompleteAsync_NullFeatureDoesNotInspectConfiguredFeaturesOrValidateContextAndPreservesResult()
    {
        var basicAi = new CannedBasicAi(completions:
        [
            Completion(
                """{"narrative":"story","summary":"kept metadata"}""",
                reasoning: "kept reasoning"),
        ]);
        var configuredCallbackCount = 0;
        var expert = CreateBoundExpert(basicAi, new HashSet<string>(["summary"], StringComparer.Ordinal));
        _ = expert.WithFeatures(new VariableUpdateFeature((_, _) =>
        {
            configuredCallbackCount++;
            return ValueTask.CompletedTask;
        }));

        var result = await ExpertVariableUpdateExecution.CompleteAsync(
            expert,
            SMainRequest,
            new RecordingSink(),
            feature: null,
            context: null,
            TestSupport.CancellationToken);

        Assert.Single(basicAi.CompletionRequests);
        Assert.Equal(0, configuredCallbackCount);
        var metadata = Assert.IsType<IReadOnlyDictionary<string, string>>(result.Metadata, exactMatch: false);
        Assert.Equal("kept metadata", metadata["summary"]);
        Assert.Equal("kept reasoning", result.Reasoning);
    }

    [Fact]
    public async Task StreamAsync_NullFeaturePerformsOnlyMainStream()
    {
        var basicAi = new CannedBasicAi(
            streams: [MainStreamEvents("story", "kept reasoning", "kept metadata")]);
        var sink = new RecordingSink();

        var result = await ExpertVariableUpdateExecution.StreamAsync(
            CreateBoundExpert(basicAi, new HashSet<string>(["summary"], StringComparer.Ordinal)),
            SMainRequest,
            sink,
            feature: null,
            context: null,
            TestSupport.CancellationToken);

        Assert.Single(basicAi.StreamRequests);
        Assert.Empty(basicAi.CompletionRequests);
        Assert.Equal(["story"], sink.GetChunks("/narrative"));
        var metadata = Assert.IsType<IReadOnlyDictionary<string, string>>(result.Metadata, exactMatch: false);
        Assert.Equal("kept metadata", metadata["summary"]);
        Assert.Equal("kept reasoning", result.Reasoning);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"variableUpdates\":{}}")]
    public async Task CompleteAsync_MissingOrNonArrayRootDeliversEmptyProposal(string updateJson)
    {
        var basicAi = new CannedBasicAi(completions:
        [
            Completion("""{"narrative":"story"}"""),
            Completion(updateJson),
        ]);
        VariableUpdatePatchProposal? proposal = null;

        await ExpertVariableUpdateExecution.CompleteAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            new RecordingSink(),
            new VariableUpdateFeature((value, _) =>
            {
                proposal = value;
                return ValueTask.CompletedTask;
            }),
            SContext,
            TestSupport.CancellationToken);

        Assert.Empty(Assert.IsType<VariableUpdatePatchProposal>(proposal).Operations);
    }

    [Fact]
    public async Task CompleteAsync_MalformedOperationPropagatesBeforeCallback()
    {
        var basicAi = new CannedBasicAi(completions:
        [
            Completion("""{"narrative":"story"}"""),
            Completion("""{"variableUpdates":[{"op":"unknown","path":"/value"}]}"""),
        ]);
        var callbackCount = 0;

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => ExpertVariableUpdateExecution.CompleteAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            new RecordingSink(),
            new VariableUpdateFeature((_, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            }),
            SContext,
            TestSupport.CancellationToken));

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task CompleteAsync_CallbackFailurePropagates()
    {
        var basicAi = new CannedBasicAi(completions:
        [
            Completion("""{"narrative":"story"}"""),
            Completion("""{"variableUpdates":[]}"""),
        ]);
        var expected = new InvalidOperationException("callback failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExpertVariableUpdateExecution.CompleteAsync(
                CreateBoundExpert(basicAi),
                SMainRequest,
                new RecordingSink(),
                new VariableUpdateFeature((_, _) => ValueTask.FromException(expected)),
                SContext,
                TestSupport.CancellationToken));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CompleteAsync_SecondModelFailurePropagates()
    {
        var expected = new InvalidOperationException("second call failed");
        var basicAi = new CannedBasicAi(
            completions: [Completion("""{"narrative":"story"}""")],
            completionFailure: expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExpertVariableUpdateExecution.CompleteAsync(
                CreateBoundExpert(basicAi),
                SMainRequest,
                new RecordingSink(),
                new VariableUpdateFeature((_, _) => ValueTask.CompletedTask),
                SContext,
                TestSupport.CancellationToken));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CompleteAsync_SecondModelCancellationPropagates()
    {
        var expected = new OperationCanceledException("second call cancelled");
        var basicAi = new CannedBasicAi(
            completions: [Completion("""{"narrative":"story"}""")],
            completionFailure: expected);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExpertVariableUpdateExecution.CompleteAsync(
                CreateBoundExpert(basicAi),
                SMainRequest,
                new RecordingSink(),
                new VariableUpdateFeature((_, _) => ValueTask.CompletedTask),
                SContext,
                TestSupport.CancellationToken));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CompleteAsync_CancellationPropagatesToMainCall()
    {
        var basicAi = new CannedBasicAi();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExpertVariableUpdateExecution.CompleteAsync(
                CreateBoundExpert(basicAi),
                SMainRequest,
                new RecordingSink(),
                new VariableUpdateFeature((_, _) => ValueTask.CompletedTask),
                SContext,
                cancellationTokenSource.Token));

        Assert.Empty(basicAi.CompletionRequests);
    }

    [Theory]
    [InlineData(" ", "previous-state", "state-schema")]
    [InlineData("world-rules", " ", "state-schema")]
    [InlineData("world-rules", "previous-state", " ")]
    public async Task CompleteAsync_WithFeatureRejectsEmptyContextBeforeModelCall(
        string worldSettings,
        string previousState,
        string stateSchema)
    {
        var basicAi = new CannedBasicAi();
        var feature = new VariableUpdateFeature((_, _) => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => ExpertVariableUpdateExecution.CompleteAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            new RecordingSink(),
            feature,
            new ExpertVariableUpdateContext(worldSettings, previousState, stateSchema, "update-model"),
            TestSupport.CancellationToken));

        Assert.Empty(basicAi.CompletionRequests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CompleteAsync_WithFeatureRejectsBlankVariableUpdateModelIdBeforeModelCall(string? modelId)
    {
        var basicAi = new CannedBasicAi();
        var feature = new VariableUpdateFeature((_, _) => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => ExpertVariableUpdateExecution.CompleteAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            new RecordingSink(),
            feature,
            new ExpertVariableUpdateContext("world-rules", "previous-state", "state-schema", modelId!),
            TestSupport.CancellationToken));

        Assert.Empty(basicAi.CompletionRequests);
    }

    [Fact]
    public async Task CompleteAsync_WithFeatureRequiresContextBeforeModelCall()
    {
        var basicAi = new CannedBasicAi();

        await Assert.ThrowsAsync<ArgumentNullException>(() => ExpertVariableUpdateExecution.CompleteAsync(
            CreateBoundExpert(basicAi),
            SMainRequest,
            new RecordingSink(),
            new VariableUpdateFeature((_, _) => ValueTask.CompletedTask),
            context: null,
            TestSupport.CancellationToken));

        Assert.Empty(basicAi.CompletionRequests);
    }
}
