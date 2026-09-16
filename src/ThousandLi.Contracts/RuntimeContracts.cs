using System.Collections.ObjectModel;
using System.Text.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    IExpertFacade experts,
    IHistoryBucketSet buckets,
    Func<Type, CancellationToken, Task<object>>? getGameSettings = null,
    IGameSettingsStore? gameSettingsStore = null,
    ILogger? logger = null)
{
    private static readonly JsonSerializerOptions GameSettingsJsonOptions = new(JsonSerializerDefaults.Web);

    public SessionId SessionId { get; } = sessionId;
    public BranchId BranchId { get; } = branchId;
    public ActionId ActionId { get; } = actionId;
    public ActionRunId ActionRunId { get; } = actionRunId;
    public BoundPlayerProfile PlayerProfile { get; } = playerProfile ?? throw new ArgumentNullException(nameof(playerProfile));
    public GameState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
    public IFrontendEventSink Frontend { get; } = frontend ?? throw new ArgumentNullException(nameof(frontend));
    public IActionHistory History { get; } = history ?? throw new ArgumentNullException(nameof(history));

    /// <summary>类型化专家 facade（生产 Host / DevHost 各模式注入）。</summary>
    public IExpertFacade Experts { get; } = experts ?? throw new ArgumentNullException(nameof(experts));

    /// <summary>历史消息桶集合。</summary>
    public IHistoryBucketSet Buckets { get; } = buckets ?? throw new ArgumentNullException(nameof(buckets));

    /// <summary>
    /// 游戏设置持久化存储端口；运行时未注入时为 null。存储契约无身份参数——
    /// SDK 契约面不含用户/游戏包身份概念（Player≠User），身份由平台在构造 store 实现时绑定。
    /// 未注入时 <see cref="GetGameSettingsAsync{TSettings}" /> 回退到默认实例。
    /// </summary>
    public IGameSettingsStore? GameSettingsStore { get; } = gameSettingsStore;

    /// <summary>请求内日志器；未注入时使用 <see cref="NullLogger" />。</summary>
    public ILogger Logger { get; } = logger ?? NullLogger.Instance;

    /// <summary>
    /// 读取当前会话下解析后的游戏设置实例。运行时注入解析器时走注入路径；
    /// 否则在有 <see cref="GameSettingsStore" /> 时从存储读取（无存储时返回默认实例）；
    /// 否则回退到默认实例（与平台默认行为一致）。
    /// </summary>
    public async Task<TSettings> GetGameSettingsAsync<TSettings>(CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        if (getGameSettings is not null)
            return (TSettings)await getGameSettings(typeof(TSettings), cancellationToken).ConfigureAwait(false);
        if (GameSettingsStore is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await GameSettingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            if (stored is { } element)
                return JsonSerializer.Deserialize<TSettings>(element.GetRawText(), GameSettingsJsonOptions) ?? new TSettings();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new TSettings();
    }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class FrontendRequestContext(
    SessionId sessionId,
    BranchId branchId,
    ActionId? headActionId,
    BoundPlayerProfile playerProfile,
    ReadOnlyGameState state,
    IActionHistory history,
    IHistoryBucketSet buckets,
    IGameSettingsStore? gameSettingsStore = null,
    ILogger? logger = null)
{
    public SessionId SessionId { get; } = sessionId;
    public BranchId BranchId { get; } = branchId;
    public ActionId? HeadActionId { get; } = headActionId;
    public BoundPlayerProfile PlayerProfile { get; } = playerProfile ?? throw new ArgumentNullException(nameof(playerProfile));
    public ReadOnlyGameState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
    public IActionHistory History { get; } = history ?? throw new ArgumentNullException(nameof(history));

    /// <summary>历史消息桶集合（只读快照；Game 可读取已提交的 bucket 状态）。</summary>
    public IHistoryBucketSet Buckets { get; } = buckets ?? throw new ArgumentNullException(nameof(buckets));

    /// <summary>
    /// 游戏设置持久化存储端口；运行时未注入时为 null。存储契约无身份参数——
    /// SDK 契约面不含用户/游戏包身份概念（Player≠User），身份由平台在构造 store 实现时绑定。
    /// </summary>
    public IGameSettingsStore? GameSettingsStore { get; } = gameSettingsStore;

    /// <summary>请求内日志器；未注入时使用 <see cref="NullLogger" />。</summary>
    public ILogger Logger { get; } = logger ?? NullLogger.Instance;
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

/// <summary>
/// Declares the binding of an Expert Package Assembly: the abstract expert type carrying the
/// contract identity and the concrete expert implementation. An expert package assembly declares
/// exactly one entry point; loaders reject zero or multiple declarations.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ExpertPackageEntryPointAttribute(Type abstractExpertType, Type concreteExpertType) : Attribute
{
    public Type AbstractExpertType { get; } =
        abstractExpertType ?? throw new ArgumentNullException(nameof(abstractExpertType));

    public Type ConcreteExpertType { get; } =
        concreteExpertType ?? throw new ArgumentNullException(nameof(concreteExpertType));
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
