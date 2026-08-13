using System.Collections.ObjectModel;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

public interface IFrontendEventSink
{
    ValueTask WriteAsync(FrontendEvent frontendEvent, CancellationToken cancellationToken = default);
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IActionHistory
{
    ValueTask<IReadOnlyList<PlayerActionEnvelope>> GetRecentPlayerActionsAsync(
        int count,
        CancellationToken cancellationToken = default);
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ActionContext(
    SessionId sessionId,
    BranchId branchId,
    ActionId actionId,
    ActionRunId actionRunId,
    BoundPlayerProfile playerProfile,
    GameState state,
    IFrontendEventSink frontend,
    IActionHistory history,
    IExpertExecutor experts)
{
    public SessionId SessionId { get; } = sessionId;
    public BranchId BranchId { get; } = branchId;
    public ActionId ActionId { get; } = actionId;
    public ActionRunId ActionRunId { get; } = actionRunId;
    public BoundPlayerProfile PlayerProfile { get; } = playerProfile ?? throw new ArgumentNullException(nameof(playerProfile));
    public GameState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
    public IFrontendEventSink Frontend { get; } = frontend ?? throw new ArgumentNullException(nameof(frontend));
    public IActionHistory History { get; } = history ?? throw new ArgumentNullException(nameof(history));
    public IExpertExecutor Experts { get; } = experts ?? throw new ArgumentNullException(nameof(experts));
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class FrontendRequestContext(
    SessionId sessionId,
    BranchId branchId,
    ActionId? headActionId,
    BoundPlayerProfile playerProfile,
    ReadOnlyGameState state,
    IActionHistory history)
{
    public SessionId SessionId { get; } = sessionId;
    public BranchId BranchId { get; } = branchId;
    public ActionId? HeadActionId { get; } = headActionId;
    public BoundPlayerProfile PlayerProfile { get; } = playerProfile ?? throw new ArgumentNullException(nameof(playerProfile));
    public ReadOnlyGameState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
    public IActionHistory History { get; } = history ?? throw new ArgumentNullException(nameof(history));
}

public interface IGameBackend
{
    ValueTask<JsonElement> CreateInitialStateAsync(
        BoundPlayerProfile playerProfile,
        CancellationToken cancellationToken = default);

    ValueTask HandleActionAsync(
        PlayerActionEnvelope action,
        ActionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<FrontendRequestResult> HandleFrontendRequestAsync(
        FrontendRequestEnvelope request,
        FrontendRequestContext context,
        CancellationToken cancellationToken = default);
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GamePackageEntryPointAttribute(Type entryPointType) : Attribute
{
    public Type EntryPointType { get; } = entryPointType ?? throw new ArgumentNullException(nameof(entryPointType));
}

public enum ActionRuntimeEventKind
{
    Started,
    FrontendEvent,
    Committed,
    Aborted
}

public enum ActionTerminalStatus
{
    Committed,
    Aborted,
    Failed
}

public sealed record ActionRuntimeEvent
{
    private ActionRuntimeEvent(
        ActionRuntimeEventKind kind,
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId,
        FrontendEvent? frontendEvent,
        ActionTerminalStatus? terminalStatus,
        string? errorMessage)
    {
        Kind = kind;
        SessionId = sessionId;
        BranchId = branchId;
        ActionId = actionId;
        ActionRunId = actionRunId;
        FrontendEvent = frontendEvent;
        TerminalStatus = terminalStatus;
        ErrorMessage = errorMessage;
    }

    public ActionRuntimeEventKind Kind { get; }
    public SessionId SessionId { get; }
    public BranchId BranchId { get; }
    public ActionId ActionId { get; }
    public ActionRunId ActionRunId { get; }
    public FrontendEvent? FrontendEvent { get; }
    public ActionTerminalStatus? TerminalStatus { get; }
    public string? ErrorMessage { get; }

    public static ActionRuntimeEvent Started(
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId) =>
        new(ActionRuntimeEventKind.Started, sessionId, branchId, actionId, actionRunId, null, null, null);

    public static ActionRuntimeEvent FromFrontendEvent(
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId,
        FrontendEvent frontendEvent) =>
        new(ActionRuntimeEventKind.FrontendEvent, sessionId, branchId, actionId, actionRunId,
            frontendEvent ?? throw new ArgumentNullException(nameof(frontendEvent)), null, null);

    public static ActionRuntimeEvent Committed(
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId) =>
        new(ActionRuntimeEventKind.Committed, sessionId, branchId, actionId, actionRunId, null,
            ActionTerminalStatus.Committed, null);

    public static ActionRuntimeEvent Aborted(
        SessionId sessionId,
        BranchId branchId,
        ActionId actionId,
        ActionRunId actionRunId,
        ActionTerminalStatus status,
        string? errorMessage)
    {
        if (status is not ActionTerminalStatus.Aborted and not ActionTerminalStatus.Failed)
            throw new ArgumentException("Aborted events require Aborted or Failed status.", nameof(status));
        return new ActionRuntimeEvent(ActionRuntimeEventKind.Aborted, sessionId, branchId, actionId, actionRunId,
            null, status, errorMessage);
    }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CommittedActionRecord
{
    public CommittedActionRecord(
        ActionId actionId,
        ActionRunId actionRunId,
        PlayerActionEnvelope action,
        JsonElement previousState,
        JsonElement committedState,
        IReadOnlyList<StateChange> changes,
        IReadOnlyList<FrontendEvent> frontendEvents)
    {
        ActionId = actionId;
        ActionRunId = actionRunId;
        Action = action ?? throw new ArgumentNullException(nameof(action));
        JsonContractGuard.ThrowIfUndefined(previousState, nameof(previousState));
        JsonContractGuard.ThrowIfUndefined(committedState, nameof(committedState));
        PreviousState = previousState.Clone();
        CommittedState = committedState.Clone();
        Changes = new ReadOnlyCollection<StateChange>([.. changes ?? throw new ArgumentNullException(nameof(changes))]);
        FrontendEvents = new ReadOnlyCollection<FrontendEvent>([.. frontendEvents ?? throw new ArgumentNullException(nameof(frontendEvents))]);
    }

    public ActionId ActionId { get; }
    public ActionRunId ActionRunId { get; }
    public PlayerActionEnvelope Action { get; }
    public JsonElement PreviousState { get; }
    public JsonElement CommittedState { get; }
    public IReadOnlyList<StateChange> Changes { get; }
    public IReadOnlyList<FrontendEvent> FrontendEvents { get; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IRuntimeIdSource
{
    SessionId NewSessionId();
    ActionId NewActionId();
    ActionRunId NewActionRunId();
}
