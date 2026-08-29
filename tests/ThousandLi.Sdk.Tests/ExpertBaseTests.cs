using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertBaseTests
{
    private sealed class NarrationFeature : IExpertFeature;

    private sealed class OtherFeature : IExpertFeature;

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

    private sealed class ProbeExpert : ExpertBase<NarrationFeature, ProbeExpert>
    {
        public Func<ProbeExpert, CancellationToken, Task>? OnStream { get; set; }

        public IExpertPrimaryOutput? DeclaredOutput => PrimaryOutput;

        public IReadOnlyList<NarrationFeature> DeclaredFeatures => ConfiguredFeatures;

        public IReadOnlyList<IHistoryBucket> DeclaredBuckets => HistoryBuckets;

        public IExpertRuntimeContext BoundContext => RuntimeContext;

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

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class SettingsDoc
    {
        [JsonPropertyName("greeting")]
        public string Greeting { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; init; }
    }

    private static LocalExpertRuntimeContext CreateContext(
        ILocalBasicAi? basicAi = null,
        JsonElement? schemaDefaults = null,
        FileInfo? overrideFile = null) =>
        new(
            basicAi ?? new RecordedBasicAi(["stub-model"], []),
            new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious"),
            NullLogger.Instance,
            schemaDefaults,
            overrideFile);

    private static ProbeExpert CreateBound(IExpertRuntimeContext? context = null)
    {
        var expert = new ProbeExpert();
        expert.Bind(context ?? CreateContext());
        return expert;
    }

    [Fact]
    public void FluentMethodsReturnTheSameInstance()
    {
        var expert = new ProbeExpert();
        var feature = new NarrationFeature();
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
    public async Task UnboundExecutionFailsFast()
    {
        var expert = new ProbeExpert();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => expert.StreamAsync(TestSupport.CancellationToken));
        Assert.Contains("has not been bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RebindingAnInstanceIsRejected()
    {
        var expert = CreateBound();
        var exception = Assert.Throws<InvalidOperationException>(
            () => expert.Bind(CreateContext()));
        Assert.Contains("already bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutingAnInstanceTwiceIsRejected()
    {
        var expert = CreateBound();
        var first = await expert.CompleteAsync(TestSupport.CancellationToken);
        Assert.Equal("complete", first.Metadata!["mode"]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => expert.CompleteAsync(TestSupport.CancellationToken));
        Assert.Contains("already been executed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamThenCompleteOnTheSameInstanceIsRejected()
    {
        var expert = CreateBound();
        await expert.StreamAsync(TestSupport.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => expert.CompleteAsync(TestSupport.CancellationToken));

        Assert.Contains("already been executed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindRejectsANullContext()
    {
        var expert = new ProbeExpert();
        Assert.Throws<ArgumentNullException>(() => expert.Bind(null!));
    }

    [Fact]
    public async Task ConfigurationIsFrozenAfterExecution()
    {
        var expert = CreateBound();
        await expert.StreamAsync(TestSupport.CancellationToken);
        var exception = Assert.Throws<InvalidOperationException>(
            () => expert.WithFeatures(new NarrationFeature()));
        Assert.Contains("cannot change after", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryConfigurationBuilderFreezesAfterExecution()
    {
        var expert = CreateBound();
        await expert.CompleteAsync(TestSupport.CancellationToken);

        Assert.Throws<InvalidOperationException>(
            () => expert.WithPrimaryOutput(new TextPrimaryOutput((_, _) => ValueTask.CompletedTask)));
        Assert.Throws<InvalidOperationException>(
            () => expert.WithFeatures(new NarrationFeature()));
        Assert.Throws<InvalidOperationException>(
            () => expert.WithHistoryBuckets(new StubHistoryBucket("late")));
    }

    [Fact]
    public async Task PerInvocationInstancesAreIndependent()
    {
        var context = CreateContext();
        var first = CreateBound(context);
        var second = CreateBound(context);

        var firstResult = await first.CompleteAsync(TestSupport.CancellationToken);
        var secondResult = await second.CompleteAsync(TestSupport.CancellationToken);

        Assert.Equal("complete", firstResult.Metadata!["mode"]);
        Assert.Equal("complete", secondResult.Metadata!["mode"]);
        Assert.NotSame(firstResult, secondResult);
    }

    [Fact]
    public async Task PrimaryOutputCallbackExceptionsPropagate()
    {
        var expert = CreateBound();
        expert.WithPrimaryOutput(new TextPrimaryOutput(
            (_, _) => ValueTask.FromException(new InvalidOperationException("callback boom"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => expert.StreamAsync(TestSupport.CancellationToken));
        Assert.Contains("callback boom", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsFlowThroughTheRestrictedContext()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello","count":1}""");
        var overrides = TestSupport.Json("""{"count":5}""");
        var context = CreateContext(schemaDefaults: LocalExpertSettingsResolver.Merge(defaults, overrides));
        SettingsDoc? observed = null;
        var expert = CreateBound(context);
        expert.OnStream = async (self, token) => observed = await self.BoundContext.GetExpertSettingsAsync<SettingsDoc>(token);

        await expert.StreamAsync(TestSupport.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal("hello", observed.Greeting);
        Assert.Equal(5, observed.Count);
    }

    [Fact]
    public void HistoryBucketsAreReadableThroughConfiguration()
    {
        var expert = CreateBound();
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
        var expert = CreateBound();
        Assert.Throws<ArgumentNullException>(() => expert.WithPrimaryOutput(null!));
        Assert.Throws<ArgumentNullException>(() => expert.WithFeatures(null!));
        Assert.Throws<ArgumentNullException>(() => expert.WithHistoryBuckets(null!));
        Assert.Throws<ArgumentException>(() => expert.WithFeatures((NarrationFeature)null!));
        Assert.Throws<ArgumentException>(() => expert.WithHistoryBuckets((IHistoryBucket)null!));
    }

    [Fact]
    public async Task CancellationPropagatesThroughExecution()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var expert = CreateBound();
        expert.OnStream = (_, token) => Task.FromCanceled(token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => expert.StreamAsync(cancellationSource.Token));
    }

    [Fact]
    public void FeatureCategoryConstraintIsCompileTimeEnforced()
    {
        Assert.False(typeof(OtherFeature).IsAssignableTo(typeof(NarrationFeature)));
        Assert.True(typeof(NarrationFeature).IsAssignableTo(typeof(IExpertFeature)));
    }

    [Fact]
    public void RestrictedContextExposesExactlyFourCapabilities()
    {
        var propertyNames = typeof(IExpertRuntimeContext).GetProperties().Select(property => property.Name).Order().ToArray();
        var methodNames = typeof(IExpertRuntimeContext)
            .GetMethods()
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.Equal(["BasicAi", "Logger", "PlayerProfile"], propertyNames);
        Assert.Equal(["GetExpertSettingsAsync"], methodNames);
    }
}
