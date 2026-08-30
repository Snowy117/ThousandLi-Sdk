using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// <see cref="ActionContext" /> 的测试组装工厂：未提供的依赖使用清晰的禁用态默认
/// （确定性的测试 ID、空 JSON 对象状态、丢弃事件的 frontend、空历史、抛错的专家门面、
/// 内存历史桶），供 Game Package 测试一行构造 action 运行上下文。
/// 生产代码不应调用本工厂。
/// </summary>
public static class ActionContextTestFactory
{
    /// <summary>构造测试用 <see cref="ActionContext" />。</summary>
    /// <param name="playerProfile">绑定玩家资料；默认 <c>test-player</c> 占位 profile。</param>
    /// <param name="state">可变游戏状态；默认空 JSON 对象。</param>
    /// <param name="frontend">前端事件接收器；默认 <see cref="NoOpFrontendEventSink.Instance" />。</param>
    /// <param name="history">玩家行动历史；默认 <see cref="EmptyActionHistory.Instance" />。</param>
    /// <param name="experts">类型化专家 facade；默认 <see cref="ThrowingExpertFacade.Instance" />。</param>
    /// <param name="buckets">历史消息桶集合；默认新的 <see cref="InMemoryHistoryBucketSet" />。</param>
    /// <param name="gameSettingsStore">游戏设置存储；默认 null（未注入）。</param>
    /// <param name="getGameSettings">延迟解析游戏设置的委托；默认 null（回退存储/默认实例）。</param>
    /// <param name="logger">请求内日志器；默认 <c>NullLogger</c>。</param>
    /// <param name="sessionId">测试会话标识；默认 <c>test-session</c>。</param>
    /// <param name="branchId">测试分支标识；默认 <c>test-branch</c>。</param>
    /// <param name="actionId">测试行动标识；默认 <c>test-action</c>。</param>
    /// <param name="actionRunId">测试行动运行标识；默认 <c>test-action-run</c>。</param>
    /// <returns>填充完毕的 <see cref="ActionContext" />。</returns>
    public static ActionContext Create(
        BoundPlayerProfile? playerProfile = null,
        GameState? state = null,
        IFrontendEventSink? frontend = null,
        IActionHistory? history = null,
        IExpertFacade? experts = null,
        IHistoryBucketSet? buckets = null,
        IGameSettingsStore? gameSettingsStore = null,
        Func<Type, CancellationToken, Task<object>>? getGameSettings = null,
        ILogger? logger = null,
        SessionId? sessionId = null,
        BranchId? branchId = null,
        ActionId? actionId = null,
        ActionRunId? actionRunId = null)
    {
        playerProfile ??= new BoundPlayerProfile(new PlayerId("test-player"), "TestPlayer", "Test persona");
        state ??= new GameState(EmptyObject());
        frontend ??= NoOpFrontendEventSink.Instance;
        history ??= EmptyActionHistory.Instance;
        experts ??= ThrowingExpertFacade.Instance;
        buckets ??= new InMemoryHistoryBucketSet();

        return new ActionContext(
            sessionId ?? new SessionId("test-session"),
            branchId ?? new BranchId("test-branch"),
            actionId ?? new ActionId("test-action"),
            actionRunId ?? new ActionRunId("test-action-run"),
            playerProfile,
            state,
            frontend,
            history,
            experts,
            buckets,
            getGameSettings,
            gameSettingsStore,
            logger);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

/// <summary>
/// <see cref="FrontendRequestContext" /> 的测试组装工厂：未提供的依赖使用与
/// <see cref="ActionContextTestFactory" /> 一致的禁用态默认。生产代码不应调用本工厂。
/// </summary>
public static class FrontendRequestContextTestFactory
{
    /// <summary>构造测试用 <see cref="FrontendRequestContext" />。</summary>
    /// <param name="playerProfile">绑定玩家资料；默认 <c>test-player</c> 占位 profile。</param>
    /// <param name="state">只读已提交状态视图；默认空 JSON 对象。</param>
    /// <param name="buckets">历史消息桶集合（只读快照语义）；默认新的 <see cref="InMemoryHistoryBucketSet" />。</param>
    /// <param name="history">玩家行动历史；默认 <see cref="EmptyActionHistory.Instance" />。</param>
    /// <param name="gameSettingsStore">游戏设置存储；默认 null（未注入）。</param>
    /// <param name="logger">请求内日志器；默认 <c>NullLogger</c>。</param>
    /// <param name="sessionId">测试会话标识；默认 <c>test-session</c>。</param>
    /// <param name="branchId">测试分支标识；默认 <c>test-branch</c>。</param>
    /// <param name="headActionId">捕获的分支头行动标识；默认 null。</param>
    /// <returns>填充完毕的 <see cref="FrontendRequestContext" />。</returns>
    public static FrontendRequestContext Create(
        BoundPlayerProfile? playerProfile = null,
        ReadOnlyGameState? state = null,
        IHistoryBucketSet? buckets = null,
        IActionHistory? history = null,
        IGameSettingsStore? gameSettingsStore = null,
        ILogger? logger = null,
        SessionId? sessionId = null,
        BranchId? branchId = null,
        ActionId? headActionId = null)
    {
        playerProfile ??= new BoundPlayerProfile(new PlayerId("test-player"), "TestPlayer", "Test persona");
        state ??= new ReadOnlyGameState(EmptyObject());
        buckets ??= new InMemoryHistoryBucketSet();
        history ??= EmptyActionHistory.Instance;

        return new FrontendRequestContext(
            sessionId ?? new SessionId("test-session"),
            branchId ?? new BranchId("test-branch"),
            headActionId,
            playerProfile,
            state,
            history,
            buckets,
            gameSettingsStore,
            logger);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
