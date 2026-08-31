using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.DevHost;

/// <summary>Composition or invocation failure of the local expert execution path.</summary>
public sealed class LocalExpertException(string message) : InvalidOperationException(message);

/// <summary>
/// Composition options of the local expert path: the BasicAi capability, the player profile,
/// and the optional local settings override file. Credentials never appear here; they stay inside
/// the composition root's <see cref="IRuntimeBasicAi"/> implementation.
/// </summary>
public sealed record LocalExpertCompositionOptions(
    IRuntimeBasicAi BasicAi,
    BoundPlayerProfile PlayerProfile,
    ILogger? Logger = null,
    FileInfo? SettingsOverrideFile = null);

/// <summary>
/// Executes expert invocations against trusted locally loaded Expert Packages. Contract-to-package
/// resolution uses explicit bindings when configured and rejects ambiguous multi-package contracts
/// with a deterministic error listing the candidates. This is a thin adapter over the shared
/// <see cref="ExpertSessionComposition"/> engine: binding validation, instance creation, context
/// binding, and structured invocation all live in the engine; this class contributes the loaded
/// package set and the per-slot execution context factory. Game code reaches the same packages
/// through <see cref="LocalExpertFacade"/>.
/// </summary>
public sealed class LocalExpertComposition : IExpertRunner
{
    private readonly IReadOnlyDictionary<string, LoadedExpertPackage> _packagesById;
    private readonly LocalExpertCompositionOptions _options;
    private readonly IReadOnlyDictionary<string, LoadedExpertPackage> _resolvedContracts;
    private readonly ExpertSessionComposition _engine;

    /// <summary>
    /// 以已加载的 Expert Package 集合与组合配置构造本地专家组合。要求至少一个已加载包；
    /// 重复的包 id、无法唯一解析的契约绑定在构造期即失败。
    /// </summary>
    /// <param name="expertPackages">已加载的本地 Expert Package 集合。</param>
    /// <param name="explicitContractBindings">可选的契约 id → 包 id 显式绑定。</param>
    /// <param name="options">本地执行组合配置（BasicAi、玩家档案、可选设置覆盖文件）。</param>
    public LocalExpertComposition(
        IReadOnlyList<LoadedExpertPackage> expertPackages,
        IReadOnlyDictionary<string, string>? explicitContractBindings,
        LocalExpertCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(expertPackages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BasicAi);
        ArgumentNullException.ThrowIfNull(options.PlayerProfile);
        if (expertPackages.Count == 0)
            throw new LocalExpertException("Local expert execution requires at least one loaded Expert Package.");

        _options = options;
        var explicitBindings = explicitContractBindings ?? new Dictionary<string, string>();
        var duplicates = expertPackages
            .GroupBy(package => package.Manifest.PackageId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
            throw new LocalExpertException(
                $"Expert Package '{duplicates.Key}' was loaded more than once; Expert Package ids must be unique.");

        var errors = new List<string>();
        foreach (var binding in explicitBindings)
        {
            if (!expertPackages.Any(package =>
                    string.Equals(package.Manifest.PackageId, binding.Value, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"the binding for contract '{binding.Key}' points to Expert Package '{binding.Value}', which is not loaded. " +
                    "Loaded Expert Packages: " + PackageList(expertPackages) + ".");
                continue;
            }
            if (!expertPackages.Any(package =>
                    string.Equals(package.Shape.ContractId, binding.Key, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"the binding for contract '{binding.Key}' points to a contract that no loaded Expert Package serves. " +
                    "Served contracts: " + ContractList(expertPackages) + ".");
            }
        }

        var resolved = new Dictionary<string, LoadedExpertPackage>(StringComparer.Ordinal);
        foreach (var contractGroup in expertPackages.GroupBy(
                     package => package.Shape.ContractId, StringComparer.Ordinal))
        {
            var explicitBinding = explicitBindings.GetValueOrDefault(contractGroup.Key);
            if (explicitBinding is null)
            {
                if (contractGroup.Count() > 1)
                {
                    errors.Add(
                        $"contract '{contractGroup.Key}' is served by multiple Expert Packages without an explicit binding: " +
                        string.Join(", ", contractGroup.Select(package => $"'{package.Manifest.PackageId}'")) +
                        ". Configure an explicit binding (contractId=expertPackageId) to disambiguate.");
                    continue;
                }
                resolved.Add(contractGroup.Key, contractGroup.Single());
                continue;
            }

            var bound = contractGroup.FirstOrDefault(package =>
                string.Equals(package.Manifest.PackageId, explicitBinding, StringComparison.Ordinal));
            if (bound is not null)
            {
                resolved.Add(contractGroup.Key, bound);
            }
            else
            {
                var boundPackage = expertPackages.FirstOrDefault(package =>
                    string.Equals(package.Manifest.PackageId, explicitBinding, StringComparison.Ordinal));
                if (boundPackage is not null)
                {
                    errors.Add(
                        $"the binding for contract '{contractGroup.Key}' points to Expert Package " +
                        $"'{explicitBinding}', which serves contract '{boundPackage.Shape.ContractId}', " +
                        $"not '{contractGroup.Key}'.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new LocalExpertException(
                "Local expert binding validation failed:" +
                Environment.NewLine + string.Join(Environment.NewLine, errors.Order(StringComparer.Ordinal)));
        }

        _packagesById = expertPackages.ToDictionary(
            package => package.Manifest.PackageId, package => package, StringComparer.Ordinal);
        _resolvedContracts = resolved;
        _engine = new ExpertSessionComposition(
            resolved.Values.Select(package => new ExpertSessionBinding(
                package.Shape.ContractId,
                package.Shape.AbstractExpertType,
                package.ExpertFactory,
                ResolveInvoker(package.Shape.AbstractExpertType))),
            _ => CreateContext(),
            invocationPrefix: "local");
    }

    /// <summary>已解析契约 id 列表（按 id 排序；Playground 契约展示用）。</summary>
    public IReadOnlyList<string> ContractIds =>
        [.. _resolvedContracts.Keys.Order(StringComparer.Ordinal)];

    /// <summary>All loaded Expert Package ids serving the contract, ordered by id (Playground listing).</summary>
    public IReadOnlyList<string> PackageIdsForContract(string contractId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        return [.. _packagesById.Values
            .Where(package => string.Equals(package.Shape.ContractId, contractId, StringComparison.Ordinal))
            .Select(package => package.Manifest.PackageId)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Creates and binds a fresh expert instance for the game-facing local facade: resolves the
    /// contract-to-package binding, runs the package factory, and binds a per-invocation execution
    /// context. Mirrors the production facade flow (resolver → factory → Bind).
    /// </summary>
    public ExpertBase CreateExpert(string contractId, string? expertPackageIdOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        return expertPackageIdOverride is null
            ? _engine.CreateExpert(contractId)
            : _engine.CreateExpert(contractId, ResolvePackage(contractId, expertPackageIdOverride).ExpertFactory);
    }

    /// <summary>以配置的契约绑定执行一次调用（便捷重载；不做包覆盖）。</summary>
    public ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, events, expertPackageIdOverride: null, cancellationToken);

    /// <summary>
    /// Executes one invocation, optionally overriding the configured contract binding for this
    /// call only (used by the Playground); the override never mutates the global binding state.
    /// </summary>
    public ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        string? expertPackageIdOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        return expertPackageIdOverride is null
            ? _engine.ExecuteAsync(request, events, cancellationToken)
            : _engine.ExecuteAsync(
                request,
                events,
                ResolvePackage(request.ContractId, expertPackageIdOverride).ExpertFactory,
                cancellationToken);
    }

    private static ExpertStructuredInvoker ResolveInvoker(Type abstractExpertType) =>
        abstractExpertType == typeof(AbstractLongTextWritingExpert)
            ? LongTextWritingExpertInvoker.InvokeBindingAsync
            : throw new LocalExpertException(
                $"No structured invoker is available for abstract expert type '{abstractExpertType.FullName}'.");

    private LocalExpertExecutionContext CreateContext() => new(
        _options.BasicAi,
        _options.PlayerProfile,
        _options.Logger ?? NullLogger.Instance,
        schemaDefaults: null,
        _options.SettingsOverrideFile);

    private LoadedExpertPackage ResolvePackage(string contractId, string? expertPackageIdOverride)
    {
        if (expertPackageIdOverride is null)
        {
            if (_resolvedContracts.TryGetValue(contractId, out var bound))
                return bound;
            throw new LocalExpertException(
                $"No local Expert Package is bound for contract '{contractId}'. Bound contracts: " +
                ContractList([.. _resolvedContracts.Values]) + ".");
        }

        if (!_packagesById.TryGetValue(expertPackageIdOverride, out var overridePackage))
            throw new LocalExpertException(
                $"The Expert Package override '{expertPackageIdOverride}' is not loaded. Loaded Expert Packages: " +
                PackageList([.. _packagesById.Values]) + ".");
        if (!string.Equals(overridePackage.Shape.ContractId, contractId, StringComparison.Ordinal))
            throw new LocalExpertException(
                $"The Expert Package override '{expertPackageIdOverride}' serves contract " +
                $"'{overridePackage.Shape.ContractId}', not '{contractId}'.");
        return overridePackage;
    }

    private static string PackageList(IReadOnlyList<LoadedExpertPackage> packages) =>
        packages.Count == 0
            ? "none"
            : string.Join(", ", packages.Select(package => $"'{package.Manifest.PackageId}'"));

    private static string ContractList(IReadOnlyList<LoadedExpertPackage> packages) =>
        packages.Count == 0
            ? "none"
            : string.Join(", ", packages
                .Select(package => package.Shape.ContractId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(id => $"'{id}'"));
}
