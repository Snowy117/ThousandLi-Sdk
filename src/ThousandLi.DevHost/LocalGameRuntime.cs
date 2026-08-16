using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

public sealed class LocalGameRuntime
{
    public static BranchId MainBranchId { get; } = new("main");

    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly IGameBackend _backend;
    private readonly BoundPlayerProfile _playerProfile;
    private readonly IExpertExecutor _experts;
    private readonly IExpertFacade _expertFacade;
    private readonly IHistoryBucketSet _buckets;
    private readonly ILocalSessionStore _store;
    private readonly IGameSettingsStore _gameSettingsStore = new InMemoryGameSettingsStore();
    private LocalSessionDocument _session;

    private LocalGameRuntime(
        IGameBackend backend,
        BoundPlayerProfile playerProfile,
        IExpertExecutor experts,
        IExpertFacade expertFacade,
        IHistoryBucketSet buckets,
        ILocalSessionStore store,
        LocalSessionDocument session)
    {
        _backend = backend;
        _playerProfile = playerProfile;
        _experts = experts;
        _expertFacade = expertFacade;
        _buckets = buckets;
        _store = store;
        _session = session;
    }

    public SessionId SessionId => _session.SessionId;
    public JsonElement CommittedState => _session.CommittedState.Clone();
    public IReadOnlyList<CommittedActionRecord> CommittedActions => [.. _session.CommittedActions];

    public static async ValueTask<LocalGameRuntime> CreateAsync(
        string packageId,
        IGameBackend backend,
        BoundPlayerProfile playerProfile,
        IExpertExecutor experts,
        ILocalSessionStore store,
        SessionId sessionId,
        IExpertFacade? expertFacade = null,
        IHistoryBucketSet? buckets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(playerProfile);
        ArgumentNullException.ThrowIfNull(experts);
        ArgumentNullException.ThrowIfNull(store);

        var existing = await store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.PackageId, packageId, StringComparison.Ordinal))
            {
                throw new LocalDataException(
                    $"Session '{sessionId}' belongs to package '{existing.PackageId}', not '{packageId}'.");
            }
            return new LocalGameRuntime(
                backend, playerProfile, experts, expertFacade ?? DisabledExpertFacade.Instance,
                buckets ?? new InMemoryHistoryBucketSet(), store, existing);
        }

        var initialState = await backend.CreateInitialStateAsync(playerProfile, cancellationToken).ConfigureAwait(false);
        if (initialState.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("GameBackend initial state root must be a JSON object.");
        var session = new LocalSessionDocument(
            packageId,
            sessionId,
            MainBranchId,
            headActionId: null,
            initialState,
            committedActions: []);
        await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return new LocalGameRuntime(
            backend, playerProfile, experts, expertFacade ?? DisabledExpertFacade.Instance,
            buckets ?? new InMemoryHistoryBucketSet(), store, session);
    }

    public IAsyncEnumerable<ActionRuntimeEvent> HandleActionAsync(
        PlayerActionEnvelope action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return EnumerateActionAsync(action, cancellationToken);
    }

    public async ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = _session.Copy();
        var context = new FrontendRequestContext(
            captured.SessionId,
            captured.BranchId,
            captured.HeadActionId,
            _playerProfile,
            new ReadOnlyGameState(captured.CommittedState),
            new LocalActionHistory(captured.CommittedActions),
            _buckets,
            _gameSettingsStore);
        return await _backend.HandleFrontendRequestAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ActionRuntimeEvent> EnumerateActionAsync(
        PlayerActionEnvelope action,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ActionRuntimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var execution = ExecuteActionAsync(action, channel.Writer, executionCancellation.Token);
        await foreach (var runtimeEvent in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return runtimeEvent;
            if (cancellationToken.IsCancellationRequested && runtimeEvent.Kind is not ActionRuntimeEventKind.Aborted)
                await executionCancellation.CancelAsync().ConfigureAwait(false);
        }
        await execution.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ExecuteActionAsync(
        PlayerActionEnvelope action,
        ChannelWriter<ActionRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        var gateHeld = false;
        try
        {
            await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            var baseSession = _session.Copy();
            var actionId = new ActionId($"action-{baseSession.NextActionSequence:D8}");
            var actionRunId = new ActionRunId($"run-{baseSession.NextRunSequence:D8}");
            var state = new GameState(baseSession.CommittedState);
            var frontend = new StreamingFrontendEventSink(
                baseSession.SessionId, baseSession.BranchId, actionId, actionRunId, writer);
            var context = new ActionContext(
                baseSession.SessionId,
                baseSession.BranchId,
                actionId,
                actionRunId,
                _playerProfile,
                state,
                frontend,
                new LocalActionHistory(baseSession.CommittedActions),
                _expertFacade,
                _experts,
                _buckets);

            await writer.WriteAsync(
                ActionRuntimeEvent.Started(baseSession.SessionId, baseSession.BranchId, actionId, actionRunId),
                CancellationToken.None).ConfigureAwait(false);
            await RunBackendAndCommitAsync(
                action, baseSession, context, frontend, actionId, actionRunId, writer, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
            if (gateHeld) _actionGate.Release();
        }
    }

    private async Task RunBackendAndCommitAsync(
        PlayerActionEnvelope action,
        LocalSessionDocument baseSession,
        ActionContext context,
        StreamingFrontendEventSink frontend,
        ActionId actionId,
        ActionRunId actionRunId,
        ChannelWriter<ActionRuntimeEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await _backend.HandleActionAsync(action, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var committed = CreateCommittedSession(baseSession, context, frontend, action, actionId, actionRunId);
            await _store.SaveAsync(committed, cancellationToken).ConfigureAwait(false);
            _session = committed;
            await writer.WriteAsync(
                ActionRuntimeEvent.Committed(baseSession.SessionId, baseSession.BranchId, actionId, actionRunId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await WriteAbortedAsync(baseSession, actionId, actionRunId, ActionTerminalStatus.Aborted, exception, writer)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await WriteAbortedAsync(baseSession, actionId, actionRunId, ActionTerminalStatus.Failed, exception, writer)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static LocalSessionDocument CreateCommittedSession(
        LocalSessionDocument baseSession,
        ActionContext context,
        StreamingFrontendEventSink frontend,
        PlayerActionEnvelope action,
        ActionId actionId,
        ActionRunId actionRunId)
    {
        var nextState = context.State.Snapshot;
        var record = new CommittedActionRecord(
            actionId, actionRunId, action, baseSession.CommittedState, nextState, context.State.Changes, frontend.Events);
        return new LocalSessionDocument(
            baseSession.PackageId,
            baseSession.SessionId,
            baseSession.BranchId,
            actionId,
            nextState,
            [.. baseSession.CommittedActions, record],
            checked(baseSession.NextActionSequence + 1),
            checked(baseSession.NextRunSequence + 1));
    }

    private static ValueTask WriteAbortedAsync(
        LocalSessionDocument baseSession,
        ActionId actionId,
        ActionRunId actionRunId,
        ActionTerminalStatus status,
        Exception exception,
        ChannelWriter<ActionRuntimeEvent> writer) =>
        writer.WriteAsync(
            ActionRuntimeEvent.Aborted(
                baseSession.SessionId, baseSession.BranchId, actionId, actionRunId, status, exception.Message),
            CancellationToken.None);

    private sealed class StreamingFrontendEventSink(
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId,
        ChannelWriter<ActionRuntimeEvent> writer) : IFrontendEventSink
    {
        private readonly List<FrontendEvent> _events = [];

        public IReadOnlyList<FrontendEvent> Events => [.. _events];

        public async ValueTask WriteAsync(FrontendEvent frontendEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frontendEvent);
            var cloned = new FrontendEvent(frontendEvent.EventType, frontendEvent.Payload);
            _events.Add(cloned);
            await writer.WriteAsync(
                ActionRuntimeEvent.FromFrontendEvent(sessionId, branchId, actionId, actionRunId, cloned),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LocalActionHistory(IReadOnlyList<CommittedActionRecord> actions) : IActionHistory
    {
        public ValueTask<IReadOnlyList<PlayerActionEnvelope>> GetRecentPlayerActionsAsync(
            int count,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PlayerActionEnvelope> result =
                [.. actions.TakeLast(count).Reverse().Select(record => record.Action)];
            return ValueTask.FromResult(result);
        }
    }
}
