using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

internal static class TestSupport
{
    public static PlayerId PlayerId { get; } = new("player-1");
    public static SessionId SessionId { get; } = new("session-1");
    private static BoundPlayerProfile Player { get; } = new(PlayerId, "Creator", "Curious explorer");
    public static ExpertContractDescriptor Contract { get; } =
        new("tests/narrator", new ContractVersion(1, 0), "tests-narrator-v1");
    public static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static PlayerActionEnvelope Action(string json = "{}") => new(PlayerId, Json(json), "client-action-1");

    public static FakeExpertExecutor FakeExpert() => new([
        new FakeExpertScenario(
            "default",
            Contract,
            [new ExpertSemanticEvent("chunk", Json("{\"text\":\"hello\"}"))],
            Json("{\"text\":\"complete\"}"))
    ]);

    public static ValueTask<LocalGameRuntime> CreateRuntimeAsync(
        IGameBackend backend,
        ILocalSessionStore? store = null,
        IExpertExecutor? experts = null,
        SessionId? sessionId = null) =>
        LocalGameRuntime.CreateAsync(
            "tests_game@1.0.0",
            backend,
            Player,
            experts ?? FakeExpert(),
            store ?? new InMemoryLocalSessionStore(),
            sessionId ?? SessionId,
            cancellationToken: CancellationToken);

    public static async Task<IReadOnlyList<ActionRuntimeEvent>> CollectAsync(
        IAsyncEnumerable<ActionRuntimeEvent> source)
    {
        var events = new List<ActionRuntimeEvent>();
        await foreach (var runtimeEvent in source.WithCancellation(CancellationToken))
            events.Add(runtimeEvent);
        return events;
    }
}

internal sealed class DelegateBackend(
    Func<PlayerActionEnvelope, ActionContext, CancellationToken, ValueTask>? handleAction = null,
    Func<FrontendRequestEnvelope, FrontendRequestContext, CancellationToken, ValueTask<FrontendRequestResult>>?
        handleFrontendRequest = null) : IGameBackend
{
    private readonly Func<FrontendRequestEnvelope, FrontendRequestContext, CancellationToken, ValueTask<FrontendRequestResult>>
        _handleFrontendRequest = handleFrontendRequest ?? ((_, context, _) =>
            ValueTask.FromResult(new FrontendRequestResult(context.State.Snapshot)));

    private readonly Func<PlayerActionEnvelope, ActionContext, CancellationToken, ValueTask> _handleAction =
        handleAction ?? ((_, _, _) => ValueTask.CompletedTask);

    public ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TestSupport.Json("{\"turn\":0}"));

    public ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default) =>
        _handleAction(action, context, cancellationToken);

    public ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        FrontendRequestContext context,
        CancellationToken cancellationToken = default) =>
        _handleFrontendRequest(request, context, cancellationToken);
}

internal sealed class RecordingSemanticSink : IExpertSemanticEventSink
{
    public List<ExpertSemanticEvent> Events { get; } = [];

    public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(new ExpertSemanticEvent(semanticEvent.EventType, semanticEvent.Payload));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailingSaveStore(ILocalSessionStore inner, int failingSaveNumber) : ILocalSessionStore
{
    private int _saveCount;

    public ValueTask<LocalSessionDocument?> LoadAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(LocalSessionDocument session, CancellationToken cancellationToken = default)
    {
        return Interlocked.Increment(ref _saveCount) == failingSaveNumber
            ? ValueTask.FromException(new IOException("simulated save failure"))
            : inner.SaveAsync(session, cancellationToken);
    }

    public ValueTask ResetAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
        inner.ResetAsync(sessionId, cancellationToken);
}
