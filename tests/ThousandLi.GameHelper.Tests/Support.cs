using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.GameHelper.Tests;

internal static class Support
{
    public static BoundPlayerProfile Player { get; } = new(new PlayerId("test-player"), "Creator", "Curious explorer");

    public static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static ActionContext BuildActionContext(GameState state, IHistoryBucketSet? buckets = null)
        => new(
            new SessionId("session-1"),
            new BranchId("main"),
            new ActionId("action-1"),
            new ActionRunId("run-1"),
            Player,
            state,
            new NoopFrontendEventSink(),
            new EmptyActionHistory(),
            DisabledExpertFacade.Instance,
            new ThrowingExecutor(),
            buckets ?? new InMemoryHistoryBucketSet());

    public static FrontendRequestContext BuildFrontendRequestContext(
        ReadOnlyGameState state,
        IGameSettingsStore? store = null,
        IHistoryBucketSet? buckets = null)
        => new(
            new SessionId("session-1"),
            new BranchId("main"),
            headActionId: null,
            Player,
            state,
            new EmptyActionHistory(),
            buckets ?? new InMemoryHistoryBucketSet(),
            store);

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

    private sealed class ThrowingExecutor : IExpertExecutor
    {
        public ValueTask<ExpertInvocationResult> ExecuteAsync(
            ExpertInvocationRequest request,
            IExpertSemanticEventSink events,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("GameHelper tests do not invoke experts.");
    }
}
