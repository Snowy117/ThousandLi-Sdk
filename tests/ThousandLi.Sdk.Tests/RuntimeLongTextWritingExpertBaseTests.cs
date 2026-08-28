using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ExpertCompletionResult = ThousandLi.Contracts.ExpertCompletionResult;

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

        Assert.Throws<InvalidOperationException>(() => expert.Bind(CreateContext()));
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

    private static LocalExpertExecutionContext CreateContext() =>
        new(new StubBasicAi(), new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona"), NullLogger.Instance);

    private sealed class TestExpert(IReadOnlySet<string>? metadataFields) : RuntimeLongTextWritingExpertBase
    {
        public IExpertExecutionContext ExposedRuntimeContext => RuntimeContext;

        protected override IReadOnlySet<string> MetadataFieldNames =>
            metadataFields ?? base.MetadataFieldNames;

        public override Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The base seam tests never execute the expert.");

        public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
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
