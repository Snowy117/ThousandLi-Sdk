using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper;

/// <summary>类型化 SessionState 的 Game-facing 访问扩展。</summary>
public static class SessionStateExtensions
{
    private const string GameHelperPath = "/_gameHelper";
    private const string SessionVariablesPath = "/_gameHelper/sessionVariables";

    private static readonly JsonElement EmptyObject =
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal));

    private static readonly ConditionalWeakTable<ActionContext, Dictionary<Type, object>> ContextStates = [];

    /// <summary>
    /// 从 <see cref="FrontendRequestContext" /> 只读快照读取 typed SessionState 根。
    /// 与 <see cref="GetSessionState{TSessionStateRoot}(ActionContext)" /> 不同，此重载不会挂载或写回状态——
    /// 若 managed root 不存在（例如 setup 尚未运行），返回 contract 的默认初始实例。
    /// 返回的对象是纯数据快照，修改它不会影响已提交状态。
    /// </summary>
    public static TSessionStateRoot GetSessionState<TSessionStateRoot>(this FrontendRequestContext context)
        where TSessionStateRoot : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var contract = SessionStateContract.Create(typeof(TSessionStateRoot));
        if (!context.State.Contains(new JsonPointer(SessionVariablesPath)))
            return (TSessionStateRoot)contract.CreateInitialInstance(context.PlayerProfile);
        var snapshot = context.State.Get(new JsonPointer(SessionVariablesPath));
        return (TSessionStateRoot)(SessionStateJson.Deserialize(snapshot, typeof(TSessionStateRoot))
                                   ?? throw new InvalidOperationException(
                                       $"SessionState root could not be materialized as '{typeof(TSessionStateRoot).FullName}'."));
    }

    /// <summary>
    /// 从 <see cref="ActionContext" /> 读取（必要时挂载）typed SessionState 根，
    /// 并返回通过 Castle 代理写回 <see cref="GameState" /> 的代理实例。
    /// </summary>
    public static TSessionStateRoot GetSessionState<TSessionStateRoot>(this ActionContext context)
        where TSessionStateRoot : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var cache = ContextStates.GetValue(context, _ => []);
        if (cache.TryGetValue(typeof(TSessionStateRoot), out var cached)) return (TSessionStateRoot)cached;
        if (cache.Count > 0)
        {
            throw new SessionStateContractException(
                "GameHelper supports only one SessionState root type per action run context.");
        }

        var contract = SessionStateContract.Create(typeof(TSessionStateRoot));
        EnsureSessionStateMounted(context.State, contract, context.PlayerProfile);
        var snapshot = ReadManagedRoot(context.State);
        var root = SessionStateJson.Deserialize(snapshot, typeof(TSessionStateRoot))
                   ?? throw new InvalidOperationException(
                       $"SessionState root could not be materialized as '{typeof(TSessionStateRoot).FullName}'.");
        var normalized = SessionStateJson.Serialize(root, typeof(TSessionStateRoot), contract);
        if (!JsonElement.DeepEquals(normalized, snapshot))
            context.State.Replace(new JsonPointer(SessionVariablesPath), normalized);

        var tracker = new GameStateSessionStateTracker(context.State, contract, SessionVariablesPath);
        var proxy = (TSessionStateRoot)SessionStateProxyFactory.Default.CreateRoot(contract, root, tracker);
        cache.Add(typeof(TSessionStateRoot), proxy);
        return proxy;
    }

    /// <summary>
    /// 物化 typed SessionState 根的初始托管状态，作为 GameBackend 初始状态的
    /// <c>/_gameHelper/sessionVariables</c> 段。采用 GameHelper 作为权威变量存储的
    /// GameBackend 应调用此方法，而不是手写存储前缀。
    /// </summary>
    public static JsonElement MaterializeSessionState<TSessionStateRoot>(BoundPlayerProfile playerProfile)
        where TSessionStateRoot : class
    {
        ArgumentNullException.ThrowIfNull(playerProfile);
        var contract = SessionStateContract.Create(typeof(TSessionStateRoot));
        var root = contract.CreateInitialInstance(playerProfile);
        var managedRoot = SessionStateJson.Serialize(root, typeof(TSessionStateRoot), contract);
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
(StringComparer.Ordinal)
        {
            ["_gameHelper"] = new Dictionary<string, object?>
(StringComparer.Ordinal)
            {
                ["sessionVariables"] = managedRoot
            }
        }, SessionStateJson.Options).Clone();
    }

    private static JsonElement ReadManagedRoot(GameState state)
    {
        return state.Get(new JsonPointer(SessionVariablesPath));
    }

    internal static JsonElement ReadAiFacingManagedRoot(GameState state, SessionStateContract contract)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        return contract.ProjectAiFacingState(ReadManagedRoot(state));
    }

    internal static void InvalidateCachedSessionState(ActionContext context, Type rootType)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rootType);
        if (ContextStates.TryGetValue(context, out var cache))
            _ = cache.Remove(rootType);
    }

    /// <summary>将专家提出的结构化变量更新应用到 GameHelper 托管根。</summary>
    internal static VariableUpdateApplyResult ApplyVariableUpdateToManagedRoot(
        GameState state,
        SessionStateContract contract,
        IReadOnlyList<VariableUpdateOperation> operations,
        ILogger? logger = null,
        Func<VariableUpdateOperation, bool>? validateOperation = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(operations);
        var managedState = new GameState(ReadManagedRoot(state));
        var result = VariableUpdatePatchApplier.Apply(
            managedState, contract, operations, logger ?? NullLogger.Instance, validateOperation);
        state.Replace(new JsonPointer(SessionVariablesPath), managedState.Snapshot);
        return result;
    }

    private static void EnsureSessionStateMounted(GameState state, SessionStateContract contract,
        BoundPlayerProfile playerProfile)
    {
        if (!state.Contains(new JsonPointer(GameHelperPath))) state.Add(new JsonPointer(GameHelperPath), EmptyObject);
        if (state.Contains(new JsonPointer(SessionVariablesPath))) return;

        var initialRoot = contract.CreateInitialInstance(playerProfile);
        state.Add(new JsonPointer(SessionVariablesPath),
            SessionStateJson.Serialize(initialRoot, contract.RootType, contract));
    }
}
