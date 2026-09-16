using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 一次结构化专家调用请求：契约 id、可选场景键、类别 codec 定义的 JSON 输入与可选的
/// 幂等/关联通道键。契约身份由 <c>[ExpertContract]</c> 携带的稳定 id 表达——没有版本协商，
/// 也没有指纹比对（类型即契约）。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertInvocationRequest
{
    /// <summary>创建结构化调用请求。</summary>
    /// <param name="contractId">目标契约 id（如 <c>thousandli.expert/long-text-writing</c>）。</param>
    /// <param name="scenarioId">可选场景键（Fake/录制回放所需）。</param>
    /// <param name="input">类别 codec 定义的 JSON 输入。</param>
    /// <param name="channelKey">可选通道键（远程调用作为幂等键使用）。</param>
    public ExpertInvocationRequest(string contractId, string? scenarioId, JsonElement input, string? channelKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        if (scenarioId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        if (channelKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        JsonContractGuard.ThrowIfUndefined(input, nameof(input));
        ContractId = contractId;
        ScenarioId = scenarioId;
        ChannelKey = channelKey;
        Input = input.Clone();
    }

    /// <summary>目标契约 id。</summary>
    public string ContractId { get; }

    /// <summary>可选场景键。</summary>
    public string? ScenarioId { get; }

    /// <summary>可选通道键。</summary>
    public string? ChannelKey { get; }

    /// <summary>类别 codec 定义的 JSON 输入。</summary>
    public JsonElement Input { get; }
}

/// <summary>一次已发生的结构化调用（请求 + 调用 id），供 Fake/录制断言使用。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertInvocationRecord
{
    public ExpertInvocationRecord(ExpertInvocationRequest request, string invocationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        Request = new ExpertInvocationRequest(request.ContractId, request.ScenarioId, request.Input, request.ChannelKey);
        InvocationId = invocationId;
    }

    public ExpertInvocationRequest Request { get; }

    public string InvocationId { get; }
}

/// <summary>一条专家语义事件：开放的事件类型字符串 + codec 编码的 JSON payload。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertSemanticEvent
{
    public ExpertSemanticEvent(string eventType, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        JsonContractGuard.ThrowIfUndefined(payload, nameof(payload));
        EventType = eventType;
        Payload = payload.Clone();
    }

    public string EventType { get; }

    public JsonElement Payload { get; }
}

/// <summary>结构化调用结果信封：调用 id + 类别完成帧聚合输出。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ExpertInvocationResult
{
    public ExpertInvocationResult(string invocationId, JsonElement output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        JsonContractGuard.ThrowIfUndefined(output, nameof(output));
        InvocationId = invocationId;
        Output = output.Clone();
    }

    public string InvocationId { get; }

    /// <summary>类别完成帧聚合输出（长文本写作类别为 <c>{primary, metadata?, reasoning?}</c>）。</summary>
    public JsonElement Output { get; }
}

/// <summary>结构化调用的语义事件出口（Playground / 录制 / 远程通道工具面）。</summary>
public interface IExpertSemanticEventSink
{
    ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// 结构化调用执行器：把 <see cref="ExpertInvocationRequest"/> 的 JSON 输入解码成流畅配置链，
/// 驱动专家执行一次，聚合出 <see cref="ExpertInvocationResult"/>。Fake、录制回放、远程与
/// 本地组合执行统一实现本接口；宿主在组合根按名字解析一次，调用方面不再做字符串分支。
/// </summary>
public interface IExpertRunner
{
    /// <summary>执行一次结构化调用。</summary>
    ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 包兼容性版本（runtime / frontend 契约面）。这不是专家契约版本——专家契约身份是
/// <c>[ExpertContract]</c> 的稳定 id，没有版本协商；本类型只服务于 Package Artifact 的
/// runtime/frontend 兼容性声明。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public readonly record struct PackageVersion
{
    public PackageVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    /// <summary>当前版本是否满足所需版本（major 相等且 minor 不低于要求）。</summary>
    public bool Supports(PackageVersion required) => Major == required.Major && Minor >= required.Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Game Package 的 runtime/frontend 兼容性声明。专家契约不再出现在兼容清单中。</summary>
public sealed record GamePackageCompatibility(PackageVersion Runtime, PackageVersion? Frontend);

/// <summary>SDK 自身声明的 runtime/frontend 契约版本。</summary>
public static class SdkContracts
{
    public static PackageVersion Runtime { get; } = new(1, 1);

    public static PackageVersion Frontend { get; } = new(1, 0);
}
