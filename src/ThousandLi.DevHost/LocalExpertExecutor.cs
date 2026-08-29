using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts;

namespace ThousandLi.DevHost;

/// <summary>Composition or invocation failure of the local expert execution path.</summary>
public sealed class LocalExpertException(string message) : InvalidOperationException(message);

/// <summary>
/// Per-executor composition of the local expert path: the BasicAi capability, the player profile,
/// and the optional local settings override file. Credentials never appear here; they stay inside
/// the composition root's <see cref="IRuntimeBasicAi"/> implementation.
/// </summary>
public sealed record LocalExpertExecutorOptions(
    IRuntimeBasicAi BasicAi,
    BoundPlayerProfile PlayerProfile,
    ILogger? Logger = null,
    FileInfo? SettingsOverrideFile = null);

/// <summary>
/// Executes expert invocations against trusted locally loaded Expert Packages through the same
/// <see cref="IExpertExecutor"/> seam as the Fake executors. Contract-to-package resolution uses
/// explicit bindings when configured and rejects ambiguous multi-package contracts with a
/// deterministic error listing the candidates. Expert instances and sinks are created fresh per
/// invocation.
/// </summary>
public sealed class LocalExpertExecutor : IExpertExecutor
{
    private readonly ExpertContractRegistry _registry;
    private readonly IReadOnlyDictionary<string, LoadedExpertPackage> _packagesById;
    private readonly LocalExpertExecutorOptions _options;
    private readonly IReadOnlyDictionary<string, LoadedExpertPackage> _resolvedContracts;
    private long _sequence;

    public LocalExpertExecutor(
        ExpertContractRegistry registry,
        IReadOnlyList<LoadedExpertPackage> expertPackages,
        IReadOnlyDictionary<string, string>? explicitContractBindings,
        LocalExpertExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(expertPackages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BasicAi);
        ArgumentNullException.ThrowIfNull(options.PlayerProfile);
        if (expertPackages.Count == 0)
            throw new LocalExpertException("Local expert execution requires at least one loaded Expert Package.");

        _registry = registry;
        _options = options;
        var explicitBindings = explicitContractBindings ?? new Dictionary<string, string>();
        var duplicates = expertPackages
            .GroupBy(package => package.Manifest.PackageId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
            throw new LocalExpertException(
                $"Expert Package '{duplicates.Key}' was loaded more than once; Expert Package ids must be unique.");

        var errors = new List<string>();
        foreach (var package in expertPackages)
        {
            if (!registry.TryGetContract(package.Contract.Id, out var registered))
            {
                errors.Add(
                    $"Expert Package '{package.Manifest.PackageId}' declares contract '{package.Contract.Id}' ({package.Contract.Version}), " +
                    "which is not registered.");
                continue;
            }
            if (!string.Equals(registered.Fingerprint, package.Contract.Fingerprint, StringComparison.Ordinal) ||
                registered.Version != package.Contract.Version)
            {
                errors.Add(
                    $"Expert Package '{package.Manifest.PackageId}' declares contract '{package.Contract.Id}' {package.Contract.Version} " +
                    $"with fingerprint '{package.Contract.Fingerprint}', but the registry active line is {registered.Version} " +
                    $"with fingerprint '{registered.Fingerprint}'. Rebuild the package against the active contract line.");
            }
        }

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
            if (!expertPackages.Any(package => string.Equals(package.Contract.Id, binding.Key, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"the binding for contract '{binding.Key}' points to a contract that no loaded Expert Package serves. " +
                    "Served contracts: " + ContractList(expertPackages) + ".");
            }
        }

        var resolved = new Dictionary<string, LoadedExpertPackage>(StringComparer.Ordinal);
        foreach (var contractGroup in expertPackages.GroupBy(
                     package => package.Contract.Id, StringComparer.Ordinal))
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
                        $"'{explicitBinding}', which serves contract '{boundPackage.Contract.Id}', " +
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
    }

    public IReadOnlyList<ExpertContractDescriptor> ContractDescriptors =>
        [.. _resolvedContracts.Values.Select(package => package.Contract)
            .DistinctBy(contract => contract.Id)
            .OrderBy(contract => contract.Id, StringComparer.Ordinal)];

    /// <summary>All loaded Expert Package ids serving the contract, ordered by id (Playground listing).</summary>
    public IReadOnlyList<string> PackageIdsForContract(string contractId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        return [.. _packagesById.Values
            .Where(package => string.Equals(package.Contract.Id, contractId, StringComparison.Ordinal))
            .Select(package => package.Manifest.PackageId)
            .Order(StringComparer.Ordinal)];
    }

    public ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, events, expertPackageIdOverride: null, cancellationToken);

    /// <summary>
    /// Executes one invocation, optionally overriding the configured contract binding for this
    /// call only (used by the Playground); the override never mutates the global binding state.
    /// </summary>
    public async ValueTask<ExpertInvocationResult> ExecuteAsync(
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        string? expertPackageIdOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        var package = ResolvePackage(request, expertPackageIdOverride);
        var contract = _registry.GetRequiredContract(request.Contract.Id);
        if (!contract.Version.Supports(request.Contract.Version) ||
            !string.Equals(contract.Fingerprint, request.Contract.Fingerprint, StringComparison.Ordinal))
        {
            throw new LocalExpertException(
                $"Contract mismatch for '{request.Contract.Id}': the invocation requires {request.Contract.Version} " +
                $"with fingerprint '{request.Contract.Fingerprint}', but the registered contract is {contract.Version} " +
                $"with fingerprint '{contract.Fingerprint}'.");
        }

        var expert = package.ExpertFactory();
        var context = new LocalExpertExecutionContext(
            _options.BasicAi,
            _options.PlayerProfile,
            _options.Logger ?? NullLogger.Instance,
            schemaDefaults: null,
            _options.SettingsOverrideFile);
        expert.Bind(context);
        var invocationId = $"local-{Interlocked.Increment(ref _sequence):D8}";
        var output = await ((IInvocableExpert)expert).InvokeAsync(request.Input, events, cancellationToken)
            .ConfigureAwait(false);
        return new ExpertInvocationResult(invocationId, output);
    }

    private LoadedExpertPackage ResolvePackage(ExpertInvocationRequest request, string? expertPackageIdOverride)
    {
        if (expertPackageIdOverride is null)
        {
            if (_resolvedContracts.TryGetValue(request.Contract.Id, out var bound))
                return bound;
            throw new LocalExpertException(
                $"No local Expert Package is bound for contract '{request.Contract.Id}'. Bound contracts: " +
                ContractList([.. _resolvedContracts.Values]) + ".");
        }

        if (!_packagesById.TryGetValue(expertPackageIdOverride, out var overridePackage))
            throw new LocalExpertException(
                $"The Expert Package override '{expertPackageIdOverride}' is not loaded. Loaded Expert Packages: " +
                PackageList([.. _packagesById.Values]) + ".");
        if (!string.Equals(overridePackage.Contract.Id, request.Contract.Id, StringComparison.Ordinal))
            throw new LocalExpertException(
                $"The Expert Package override '{expertPackageIdOverride}' serves contract " +
                $"'{overridePackage.Contract.Id}', not '{request.Contract.Id}'.");
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
                .Select(package => package.Contract.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(id => $"'{id}'"));
}
