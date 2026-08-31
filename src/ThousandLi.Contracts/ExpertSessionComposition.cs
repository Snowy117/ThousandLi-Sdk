using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>一个类别槽位的结构化调用委托：由具体类别的 invoker（如 LongTextWritingExpertInvoker）提供。</summary>
/// <param name="expert">未绑定的具体专家实例（委托负责恰好一次 Bind 并执行）。</param>
/// <param name="request">结构化调用请求。</param>
/// <param name="events">语义事件出口。</param>
/// <param name="context">绑定到本次专家实例的执行上下文。</param>
/// <param name="bucketResolver">按 bucketId 解析历史桶引用的委托（远程场景使用）。</param>
/// <param name="cancellationToken">取消令牌。</param>
/// <returns>完成帧聚合输出。</returns>
public delegate Task<JsonElement> ExpertStructuredInvoker(
    ExpertBase expert,
    ExpertInvocationRequest request,
    IExpertSemanticEventSink events,
    IExpertExecutionContext context,
    Func<string, IHistoryBucket>? bucketResolver,
    CancellationToken cancellationToken);

/// <summary>专家组合绑定条目：契约 id ↔ 抽象锚类型 + 专家工厂 + 该类别的结构化调用委托。</summary>
/// <param name="ContractId">契约 id（与锚类型 <c>[ExpertContract]</c> 一致）。</param>
/// <param name="AbstractType">抽象类别锚类型（必须继承 <see cref="ExpertBase"/>）。</param>
/// <param name="Factory">创建未绑定专家实例的工厂（每次调用返回新实例）。</param>
/// <param name="Invoker">该类别的结构化调用委托。</param>
public sealed record ExpertSessionBinding(
    string ContractId,
    Type AbstractType,
    Func<ExpertBase> Factory,
    ExpertStructuredInvoker Invoker);

/// <summary>
/// 统一的专家组合引擎：按契约 id注册「抽象锚类型 + 工厂 + 结构化调用委托」，按 CLR 类型
/// 精确命中创建并绑定实例，驱动结构化调用。绑定校验（未知 id、类型不符、重复注册）集中于此；
/// DevHost 本地组合与平台运行时 facade 都是本引擎之上的薄适配。
/// </summary>
public sealed class ExpertSessionComposition : IExpertRunner
{
    private readonly Dictionary<string, ExpertSessionBinding> _byContractId;
    private readonly Dictionary<Type, ExpertSessionBinding> _byAbstractType;
    private readonly Func<string, IExpertExecutionContext> _contextFactory;
    private readonly string _invocationPrefix;
    private long _sequence;

    /// <summary>按槽位绑定表构造（启动时一次校验：id 非空、类型继承、id/类型均不重复）。</summary>
    /// <param name="bindings">槽位绑定集合。</param>
    /// <param name="contextFactory">按契约 id 构造 <see cref="IExpertExecutionContext"/> 的工厂。</param>
    /// <param name="invocationPrefix">调用 id 前缀（诊断用途，如 <c>local</c>/<c>session</c>）。</param>
    public ExpertSessionComposition(
        IEnumerable<ExpertSessionBinding> bindings,
        Func<string, IExpertExecutionContext> contextFactory,
        string invocationPrefix = "expert")
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationPrefix);

        var byContractId = new Dictionary<string, ExpertSessionBinding>(StringComparer.Ordinal);
        var byAbstractType = new Dictionary<Type, ExpertSessionBinding>();
        var errors = new List<string>();
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.ContractId))
            {
                errors.Add("a session binding declares a blank contract id.");
                continue;
            }

            if (!typeof(ExpertBase).IsAssignableFrom(binding.AbstractType))
            {
                errors.Add(
                    $"the session binding for '{binding.ContractId}' declares abstract type " +
                    $"'{binding.AbstractType.FullName}', which does not inherit ExpertBase.");
                continue;
            }

            if (!byContractId.TryAdd(binding.ContractId, binding))
            {
                errors.Add($"the contract '{binding.ContractId}' is bound more than once.");
                continue;
            }

            if (!byAbstractType.TryAdd(binding.AbstractType, binding))
            {
                errors.Add(
                    $"the abstract type '{binding.AbstractType.FullName}' is bound to more than one contract " +
                    $"('{byAbstractType[binding.AbstractType].ContractId}' and '{binding.ContractId}').");
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(
                "Expert session binding validation failed:" +
                Environment.NewLine + string.Join(Environment.NewLine, errors.Order(StringComparer.Ordinal)));
        }

        _byContractId = byContractId;
        _byAbstractType = byAbstractType;
        _contextFactory = contextFactory;
        _invocationPrefix = invocationPrefix;
    }

    /// <summary>已注册契约 id 列表（确定性排序）。</summary>
    [UsedImplicitly]
    public IReadOnlyList<string> ContractIds =>
        [.. _byContractId.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// 按抽象锚类型精确命中创建并绑定一个新专家实例。泛型约束类别无关（<see cref="ExpertBase"/>）。
    /// </summary>
    [UsedImplicitly]
    public TAbstract Use<TAbstract>() where TAbstract : ExpertBase
    {
        var requested = typeof(TAbstract);
        if (!_byAbstractType.TryGetValue(requested, out var binding))
        {
            throw new InvalidOperationException(
                $"No expert binding is registered for abstract type '{requested.FullName}'. Registered contracts: " +
                JoinContractIds() + ".");
        }

        return CreateBoundExpert<TAbstract>(binding);
    }

    /// <summary>按契约 id 创建并绑定一个新专家实例（Playground/协议面使用）。</summary>
    public ExpertBase CreateExpert(string contractId)
    {
        var binding = ResolveBinding(contractId);
        return CreateBoundExpert<ExpertBase>(binding);
    }

    /// <summary>按契约 id 与专家包覆盖创建并绑定一个新专家实例；覆盖仅作用于本次调用。</summary>
    /// <param name="contractId">契约 id。</param>
    /// <param name="factoryOverride">替代绑定工厂的实例工厂（如 Playground 的包覆盖）。</param>
    public ExpertBase CreateExpert(string contractId, Func<ExpertBase> factoryOverride)
    {
        ArgumentNullException.ThrowIfNull(factoryOverride);
        var binding = ResolveBinding(contractId);
        var expert = factoryOverride();
        if (!binding.AbstractType.IsInstanceOfType(expert))
        {
            throw new InvalidOperationException(
                $"The expert override for contract '{contractId}' must inherit '{binding.AbstractType.FullName}', " +
                $"but it is '{expert.GetType().FullName}'.");
        }

        expert.Bind(_contextFactory(binding.ContractId));
        return expert;
    }

    /// <summary>驱动一次结构化调用：按契约 id 命中绑定 → 工厂 → invoker → 完成帧聚合输出。</summary>
    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);

        var binding = ResolveBinding(request.ContractId);
        var output = await InvokeCoreAsync(
            binding,
            binding.Factory,
            request,
            events,
            bucketResolver: null,
            cancellationToken).ConfigureAwait(false);
        return new ExpertInvocationResult(CreateInvocationId(), output);
    }

    /// <summary>
    /// 以外部提供的实例工厂驱动一次结构化调用（Playground 包覆盖路径）：绑定校验仍走引擎，
    /// 实例创建交给调用方。
    /// </summary>
    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        Func<ExpertBase> factoryOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(factoryOverride);

        var binding = ResolveBinding(request.ContractId);
        var output = await InvokeCoreAsync(
            binding,
            factoryOverride,
            request,
            events,
            bucketResolver: null,
            cancellationToken).ConfigureAwait(false);
        return new ExpertInvocationResult(CreateInvocationId(), output);
    }

    private async Task<JsonElement> InvokeCoreAsync(
        ExpertSessionBinding binding,
        Func<ExpertBase> factory,
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        Func<string, IHistoryBucket>? bucketResolver,
        CancellationToken cancellationToken)
    {
        var expert = factory();
        return await binding.Invoker(
            expert,
            request,
            events,
            _contextFactory(binding.ContractId),
            bucketResolver,
            cancellationToken).ConfigureAwait(false);
    }

    private ExpertSessionBinding ResolveBinding(string contractId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        if (!_byContractId.TryGetValue(contractId, out var binding))
        {
            throw new InvalidOperationException(
                $"No expert binding is registered for contract '{contractId}'. Registered contracts: " +
                JoinContractIds() + ".");
        }

        return binding;
    }

    private TExpert CreateBoundExpert<TExpert>(ExpertSessionBinding binding)
        where TExpert : ExpertBase
    {
        var expert = binding.Factory();
        if (expert is not TExpert typed)
        {
            throw new InvalidOperationException(
                $"The expert bound for contract '{binding.ContractId}' is '{expert.GetType().FullName}', " +
                $"which is not compatible with '{typeof(TExpert).FullName}'.");
        }

        typed.Bind(_contextFactory(binding.ContractId));
        return typed;
    }

    private string CreateInvocationId() => $"{_invocationPrefix}-{Interlocked.Increment(ref _sequence):D8}";

    private string JoinContractIds()
    {
        var ids = _byContractId.Keys.Order(StringComparer.Ordinal).ToArray();
        return ids.Length == 0 ? "none" : string.Join(", ", ids.Select(id => $"'{id}'"));
    }
}
