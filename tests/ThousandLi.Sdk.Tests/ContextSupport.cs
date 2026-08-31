using ThousandLi.Contracts;
using ThousandLi.Testing;
using InMemoryHistoryBucketSet = ThousandLi.DevHost.InMemoryHistoryBucketSet;

namespace ThousandLi.Sdk.Tests;

internal static class ContextSupport
{
    private static BoundPlayerProfile Player { get; } = new(new PlayerId("test-player"), "Creator", "Curious explorer");

    public static ActionContext BuildActionContext(
        GameState state,
        IHistoryBucketSet? buckets = null,
        Func<Type, CancellationToken, Task<object>>? getGameSettings = null)
        => new(
            new SessionId("session-1"),
            new BranchId("main"),
            new ActionId("action-1"),
            new ActionRunId("run-1"),
            Player,
            state,
            new NoopFrontendEventSink(),
            new EmptyActionHistory(),
            new FakeExpertFacade(),
            buckets ?? new InMemoryHistoryBucketSet(),
            getGameSettings);

    private sealed class NoopFrontendEventSink : IFrontendEventSink
    {
        public ValueTask WriteAsync(FrontendEvent frontendEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class EmptyActionHistory : IActionHistory
    {
        public ValueTask<IReadOnlyList<PlayerActionEnvelope>> GetRecentPlayerActionsAsync(
            int count,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PlayerActionEnvelope>>([]);
    }

    private sealed class FakeExpertFacade : IExpertFacade
    {
        public TAbstract Use<TAbstract>() where TAbstract : ExpertBase
        {
            var expert = new RecordingLongTextWritingExpert();
            expert.Bind(FakeExpertExecutionContext.Instance);
            return (TAbstract)(ExpertBase)expert;
        }
    }
}

internal sealed class RecordingLongTextWritingExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
        return Task.FromResult(new ExpertCompletionResult(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["afterFormat"] = "done" },
            reasoning: "reasoned"));
    }

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken)
        => StreamAsyncCore(cancellationToken);
}
