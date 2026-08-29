using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class RuntimeLongTextWritingExpertBaseTests
{
    [Fact]
    public void RuntimeContext_ThrowsWhenUnbound()
    {
        var expert = new TestExpert(null);

        Assert.Throws<InvalidOperationException>(() => _ = expert.ExposedRuntimeContext);
    }

    [Fact]
    public void Bind_MakesContextAvailable()
    {
        var context = CreateContext();
        var expert = new TestExpert(null);
        expert.Bind(context);

        Assert.Same(context, expert.ExposedRuntimeContext);
    }

    [Fact]
    public void Bind_Null_ThrowsArgumentNullException()
    {
        var expert = new TestExpert(null);

        Assert.Throws<ArgumentNullException>(() => expert.Bind(null!));
    }

    [Fact]
    public void Bind_Twice_ThrowsInvalidOperationException()
    {
        var expert = new TestExpert(null);
        expert.Bind(CreateContext());

        var exception = Assert.Throws<InvalidOperationException>(() => expert.Bind(CreateContext()));
        Assert.Contains("already bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Participant_BasicAi_ComesFromBoundContext()
    {
        var basicAi = new StubBasicAi();
        var expert = new TestExpert(null);
        expert.Bind(new LocalExpertExecutionContext(
            basicAi,
            new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona"),
            NullLogger.Instance));

        Assert.Same(basicAi, ((IExpertExecutionParticipant)expert).BasicAi);
    }

    [Fact]
    public void Participant_BasicAi_ThrowsWhenUnbound()
    {
        var expert = new TestExpert(null);

        Assert.Throws<InvalidOperationException>(() => _ = ((IExpertExecutionParticipant)expert).BasicAi);
    }

    [Fact]
    public void Participant_MetadataFieldNames_DefaultToEmpty()
    {
        var expert = new TestExpert(null);

        Assert.Empty(((IExpertExecutionParticipant)expert).MetadataFieldNames);
    }

    [Fact]
    public void Participant_MetadataFieldNames_ExposeDeclaredOverride()
    {
        var fields = new HashSet<string>(["afterThinking"], StringComparer.Ordinal);
        var expert = new TestExpert(fields);

        IExpertExecutionParticipant participant = expert;
        Assert.Equal(["afterThinking"], participant.MetadataFieldNames);
    }

    [Fact]
    public void Participant_ReasoningHandler_ReflectsWithReasoningHandlerRegistration()
    {
        var expert = new TestExpert(null);
        IExpertExecutionParticipant participant = expert;
        Assert.Null(participant.ReasoningHandler);

        var handler = static (ReasoningDeltaEvent delta, CancellationToken _) => ValueTask.CompletedTask;
        expert.WithReasoningHandler(handler);

        Assert.Same(handler, participant.ReasoningHandler);
    }

    [Fact]
    public async Task UnboundExecutionFailsFast()
    {
        var expert = new ProbeExpert();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => expert.StreamAsync(TestSupport.CancellationToken));
        Assert.Contains("has not been bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FluentMethodsReturnTheSameInstance()
    {
        var expert = new ProbeExpert();
        var feature = new TimeTagsFeature(null, null);
        var bucket = new StubHistoryBucket("bucket");
        var output = new TextPrimaryOutput((_, _) => ValueTask.CompletedTask);

        var configured = expert
            .WithPrimaryOutput(output)
            .WithFeatures(feature)
            .WithHistoryBuckets(bucket);

        Assert.Same(expert, configured);
        Assert.Equal([feature], expert.DeclaredFeatures);
        var storedBucket = Assert.Single(expert.DeclaredBuckets);
        Assert.Equal("bucket", storedBucket.Description);
        Assert.Equal("narrative", expert.DeclaredOutput!.PropertyName);
    }

    [Fact]
    public async Task ExecutingAnInstanceTwiceIsRejected()
    {
        var expert = CreateBoundProbe();
        var first = await expert.CompleteAsync(TestSupport.CancellationToken);
        Assert.Equal("complete", first.Metadata!["mode"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => expert.CompleteAsync(TestSupport.CancellationToken));
        Assert.Contains("already been executed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamThenCompleteOnTheSameInstanceIsRejected()
    {
        var expert = CreateBoundProbe();
        await expert.StreamAsync(TestSupport.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => expert.CompleteAsync(TestSupport.CancellationToken));

        Assert.Contains("already been executed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationAfterExecutionDoesNotResetTheExecutionGuard()
    {
        var expert = CreateBoundProbe();
        await expert.StreamAsync(TestSupport.CancellationToken);

        expert.WithFeatures(new TimeTagsFeature(null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => expert.StreamAsync(TestSupport.CancellationToken));
    }

    [Fact]
    public async Task ExecutionGuardSurvivesLatePrimaryOutputConfiguration()
    {
        var expert = CreateBoundProbe();
        await expert.CompleteAsync(TestSupport.CancellationToken);

        expert.WithPrimaryOutput(new TextPrimaryOutput((_, _) => ValueTask.CompletedTask));

        await Assert.ThrowsAsync<InvalidOperationException>(() => expert.CompleteAsync(TestSupport.CancellationToken));
    }

    [Fact]
    public async Task PerInvocationInstancesAreIndependent()
    {
        var context = CreateContext();
        var first = new ProbeExpert();
        first.Bind(context);
        var second = new ProbeExpert();
        second.Bind(context);

        var firstResult = await first.CompleteAsync(TestSupport.CancellationToken);
        var secondResult = await second.CompleteAsync(TestSupport.CancellationToken);

        Assert.Equal("complete", firstResult.Metadata!["mode"]);
        Assert.Equal("complete", secondResult.Metadata!["mode"]);
        Assert.NotSame(firstResult, secondResult);
    }

    [Fact]
    public async Task PrimaryOutputCallbackExceptionsPropagate()
    {
        var expert = CreateBoundProbe();
        expert.WithPrimaryOutput(new TextPrimaryOutput(
            (_, _) => ValueTask.FromException(new InvalidOperationException("callback boom"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => expert.StreamAsync(TestSupport.CancellationToken));
        Assert.Contains("callback boom", exception.Message, StringComparison.Ordinal);
    }

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class SettingsDoc
    {
        [JsonPropertyName("greeting")]
        public string Greeting { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; init; }
    }

    [Fact]
    public async Task SettingsFlowThroughTheRestrictedContext()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello","count":1}""");
        var overrides = TestSupport.Json("""{"count":5}""");
        var context = CreateContext(schemaDefaults: LocalExpertSettingsResolver.Merge(defaults, overrides));
        SettingsDoc? observed = null;
        var expert = new ProbeExpert
        {
            OnStream = async (self, token) => observed = await self.BoundContext.GetExpertSettingsAsync<SettingsDoc>(token),
        };
        expert.Bind(context);

        await expert.StreamAsync(TestSupport.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal("hello", observed.Greeting);
        Assert.Equal(5, observed.Count);
    }

    [Fact]
    public void HistoryBucketsAreReadableThroughConfiguration()
    {
        var expert = CreateBoundProbe();
        expert.WithHistoryBuckets(new StubHistoryBucket("main"));
        var bucket = expert.DeclaredBuckets[0];

        Assert.Equal("main", bucket.Description);
        var turns = bucket.GetRawTurns();
        var turn = Assert.Single(turns);
        var message = Assert.Single(turn.Messages);
        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.Equal("hello", message.Content);
        var view = Assert.IsType<HistoryProjectionRawTurn>(Assert.Single(bucket.GetCompressedView()));
        Assert.Same(turn, view.Turn);
    }

    [Fact]
    public void NullConfigurationEntriesAreRejected()
    {
        var expert = CreateBoundProbe();
        Assert.Throws<ArgumentNullException>(() => expert.WithPrimaryOutput(null!));
        Assert.Throws<ArgumentNullException>(() => expert.WithFeatures(null!));
        Assert.Throws<ArgumentNullException>(() => expert.WithHistoryBuckets(null!));
        Assert.Throws<ArgumentException>(() => expert.WithFeatures((ILongTextWritingFeature)null!));
        Assert.Throws<ArgumentException>(() => expert.WithHistoryBuckets((IHistoryBucket)null!));
    }

    [Fact]
    public async Task CancellationPropagatesThroughExecution()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var expert = new ProbeExpert
        {
            OnStream = (_, token) => Task.FromCanceled(token),
        };
        expert.Bind(CreateContext());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => expert.StreamAsync(cancellationSource.Token));
    }

    [Fact]
    public void FeatureCategoryConstraintIsCompileTimeEnforced()
    {
        Assert.True(typeof(TimeTagsFeature).IsAssignableTo(typeof(ILongTextWritingFeature)));
        Assert.True(typeof(ILongTextWritingFeature).IsAssignableTo(typeof(IExpertFeature)));
        Assert.False(typeof(StubHistoryBucket).IsAssignableTo(typeof(ILongTextWritingFeature)));
    }

    [Fact]
    public void RestrictedContextExposesExactlyFourCapabilities()
    {
        var propertyNames = typeof(IExpertExecutionContext).GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();
        var methodNames = typeof(IExpertExecutionContext)
            .GetMethods()
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.Equal(["BasicAi", "Logger", "PlayerProfile"], propertyNames);
        Assert.Equal(["GetExpertSettingsAsync"], methodNames);
    }

    private static LocalExpertExecutionContext CreateContext(
        IRuntimeBasicAi? basicAi = null,
        JsonElement? schemaDefaults = null) =>
        new(
            basicAi ?? new StubBasicAi(),
            new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona"),
            NullLogger.Instance,
            schemaDefaults);

    private static ProbeExpert CreateBoundProbe(IExpertExecutionContext? context = null)
    {
        var expert = new ProbeExpert();
        expert.Bind(context ?? CreateContext());
        return expert;
    }

    private sealed class StubHistoryBucket(string description) : IHistoryBucket
    {
        private readonly HistoryTurn _turn = new([ChatMessage.User("hello")], null, 0);

        public string Description => description;

        public void AddMessages(
            string? digest,
            IReadOnlyDictionary<string, string>? metadata,
            params ChatMessage[] messages) =>
            throw new NotSupportedException("Stub history buckets are read-only.");

        public IReadOnlyList<HistoryProjectionEntry> GetCompressedView(CompressedViewOptions? options = null) =>
            [.. GetRawTurns().Select(turn => new HistoryProjectionRawTurn(turn))];

        public IReadOnlyList<HistoryTurn> GetRawTurns() => [_turn];
    }

    private sealed class ProbeExpert : RuntimeLongTextWritingExpertBase
    {
        public Func<ProbeExpert, CancellationToken, Task>? OnStream { get; init; }

        public IExpertPrimaryOutput? DeclaredOutput => ConfiguredPrimaryOutput;

        public IReadOnlyList<ILongTextWritingFeature> DeclaredFeatures => ConfiguredFeatures;

        public IReadOnlyList<IHistoryBucket> DeclaredBuckets => ConfiguredHistoryBuckets;

        public IExpertExecutionContext BoundContext => RuntimeContext;

        protected override async Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
        {
            if (DeclaredOutput is TextPrimaryOutput textOutput)
                await textOutput.OnDelta(new TextDeltaEvent("chunk"), cancellationToken);
            if (OnStream is not null)
                await OnStream(this, cancellationToken);
            return new ExpertCompletionResult(new Dictionary<string, string> { ["mode"] = "stream" });
        }

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
            Task.FromResult(new ExpertCompletionResult(new Dictionary<string, string> { ["mode"] = "complete" }));
    }

    private sealed class TestExpert(IReadOnlySet<string>? metadataFields) : RuntimeLongTextWritingExpertBase
    {
        public IExpertExecutionContext ExposedRuntimeContext => RuntimeContext;

        protected override IReadOnlySet<string> MetadataFieldNames =>
            metadataFields ?? base.MetadataFieldNames;

        protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The base seam tests never execute the expert.");

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The base seam tests never execute the expert.");
    }

    private sealed class StubBasicAi : IRuntimeBasicAi
    {
        public IReadOnlyList<BasicAiModelDescriptor> AvailableModels => [];

        public IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The stub BasicAi is never invoked.");

        public Task<BasicAiCompletionResult> CompleteAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The stub BasicAi is never invoked.");
    }
}
