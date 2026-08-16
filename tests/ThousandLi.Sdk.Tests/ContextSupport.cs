using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

internal static class ContextSupport
{
    public static BoundPlayerProfile Player { get; } = new(new PlayerId("test-player"), "Creator", "Curious explorer");

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
            new ThrowingExpertExecutor(),
            buckets ?? new InMemoryHistoryBucketSet(),
            getGameSettings);

    public static FrontendRequestContext BuildFrontendRequestContext(
        ReadOnlyGameState state,
        IHistoryBucketSet buckets,
        IGameSettingsStore? store = null,
        UserId? userId = null,
        string gamePackageId = "tests_game@1.0.0")
        => new(
            new SessionId("session-1"),
            new BranchId("main"),
            headActionId: null,
            Player,
            state,
            new EmptyActionHistory(),
            buckets,
            userId ?? new UserId("test-user"),
            store,
            gamePackageId);

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
        public TAbstract Use<TAbstract>() where TAbstract : AbstractLongTextWritingExpert
            => (TAbstract)(AbstractLongTextWritingExpert)new RecordingLongTextWritingExpert();
    }
}

internal sealed class RecordingLongTextWritingExpert : AbstractLongTextWritingExpert
{
    public override Task<ExpertCompletionResult> StreamAsync(CancellationToken cancellationToken = default)
    {
        ValidateCategoryInputs();
        return Task.FromResult(new ExpertCompletionResult(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["afterFormat"] = "done" },
            reasoning: "reasoned"));
    }

    public override Task<ExpertCompletionResult> CompleteAsync(CancellationToken cancellationToken = default)
        => StreamAsync(cancellationToken);
}
