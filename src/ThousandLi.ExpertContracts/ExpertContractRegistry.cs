using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertContracts;

public sealed record ExpertContractRegistration
{
    public ExpertContractRegistration(ExpertContractDescriptor contract, ExpertContractDefinition definition, string source)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Contract = contract;
        Definition = definition;
        Source = source;
    }

    public ExpertContractDescriptor Contract { get; }
    public ExpertContractDefinition Definition { get; }
    public string Source { get; }
}

public sealed record RegisteredExpertContract
{
    internal RegisteredExpertContract(
        string id,
        ContractVersion version,
        string fingerprint,
        ExpertContractDefinition definition,
        Type? abstractType,
        string source)
    {
        Id = id;
        Version = version;
        Fingerprint = fingerprint;
        Definition = definition;
        AbstractType = abstractType;
        Source = source;
    }

    public string Id { get; }
    public ContractVersion Version { get; }
    public string Fingerprint { get; }
    public ExpertContractDefinition Definition { get; }
    public Type? AbstractType { get; }
    public string Source { get; }

    public ExpertContractDescriptor ToDescriptor() => new(Id, Version, Fingerprint);
}

public sealed class ExpertContractRegistryException(string message) : Exception(message);

public sealed class ExpertContractRegistry
{
    private const string OfficialAssemblyName = "ThousandLi.ExpertContracts";
    private const string OfficialNamespacePrefix = "thousandli.expert/";
    private const string DefinitionPropertyName = "Definition";

    private readonly Dictionary<string, RegisteredExpertContract> _contracts;

    public ExpertContractRegistry(
        IEnumerable<Assembly> contractAssemblies,
        IEnumerable<ExpertContractRegistration>? explicitRegistrations = null)
    {
        ArgumentNullException.ThrowIfNull(contractAssemblies);

        var errors = new List<string>();
        var entries = new List<ContractEntry>();
        var seenAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in contractAssemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            // Re-listing a trusted assembly is configuration redundancy, not a conflicting declaration.
            if (!seenAssemblies.Add(assembly.GetName().Name ?? assembly.FullName ?? assembly.ToString()))
                continue;
            DiscoverAssemblyContracts(assembly, entries, errors);
        }

        entries.AddRange((explicitRegistrations ?? []).Select(registration =>
        {
            ArgumentNullException.ThrowIfNull(registration);
            return new ContractEntry(
                registration.Contract, registration.Definition, AbstractType: null, registration.Source);
        }));

        var validEntries = entries.Where(entry => ValidateEntry(entry, errors)).ToList();

        var contracts = new Dictionary<string, RegisteredExpertContract>(StringComparer.Ordinal);
        foreach (var idGroup in validEntries.GroupBy(entry => entry.Contract.Id, StringComparer.Ordinal))
        {
            var errorCountBeforeGroup = errors.Count;
            ValidateVersionLine(idGroup, errors);
            if (errors.Count != errorCountBeforeGroup)
                continue;

            // Within the single active major line, the highest minor version is the contract that activates.
            var active = idGroup.MaxBy(entry => entry.Contract.Version.Minor)!;
            contracts.Add(idGroup.Key, new RegisteredExpertContract(
                active.Contract.Id,
                active.Contract.Version,
                active.Contract.Fingerprint,
                active.Definition,
                active.AbstractType,
                active.Source));
        }

        if (errors.Count > 0)
        {
            throw new ExpertContractRegistryException(
                "Expert contract registration failed:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Order(StringComparer.Ordinal)));
        }

        _contracts = contracts;
        Contracts = new ReadOnlyCollection<RegisteredExpertContract>(
            [.. contracts.Values.OrderBy(contract => contract.Id, StringComparer.Ordinal)]);
    }

    public IReadOnlyList<RegisteredExpertContract> Contracts { get; }

    public bool TryGetContract(string id, [NotNullWhen(true)] out RegisteredExpertContract? contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _contracts.TryGetValue(id, out contract);
    }

    public RegisteredExpertContract GetRequiredContract(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _contracts.TryGetValue(id, out var contract)
            ? contract
            : throw new KeyNotFoundException($"Expert contract '{id}' is not registered.");
    }

    public void ValidateGameRequirements(GamePackageCompatibility compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);

        var problems = new List<string>();
        foreach (var required in compatibility.ExpertContracts)
        {
            if (!_contracts.TryGetValue(required.Id, out var registered))
            {
                problems.Add($"required expert contract '{required.Id}' ({required.Version}) is not registered.");
                continue;
            }

            if (!registered.Version.Supports(required.Version))
            {
                problems.Add(
                    $"registered expert contract '{registered.Id}' ({registered.Version}) does not support the required version ({required.Version}).");
            }
        }

        if (problems.Count == 0)
            return;

        var available = Contracts.Count == 0
            ? "none"
            : string.Join(", ", Contracts.Select(contract => $"'{contract.Id}' ({contract.Version})"));
        throw new ExpertContractRegistryException(
            "Game package expert contract requirements are not satisfied:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems) + Environment.NewLine +
            $"Registered expert contracts: {available}.");
    }

    private static void DiscoverAssemblyContracts(Assembly assembly, List<ContractEntry> entries, List<string> errors)
    {
        var source = assembly.GetName().Name ?? assembly.FullName ?? assembly.ToString();
        foreach (var type in assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<ExpertContractAttribute>(inherit: false);
            if (attribute is null)
                continue;

            if (!type.IsAbstract)
            {
                errors.Add($"{attribute.Id} ({source}): contract type '{type.FullName}' must be an abstract class.");
                continue;
            }

            if (!typeof(IExpertContract).IsAssignableFrom(type))
            {
                errors.Add($"{attribute.Id} ({source}): contract type '{type.FullName}' must implement '{nameof(IExpertContract)}'.");
                continue;
            }

            ExpertContractDefinition? definition;
            try
            {
                definition = type.GetProperty(DefinitionPropertyName,
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?.GetValue(null) as ExpertContractDefinition;
            }
            catch (TargetInvocationException exception)
            {
                errors.Add(
                    $"{attribute.Id} ({source}): reading '{DefinitionPropertyName}' of '{type.FullName}' failed: " +
                    (exception.InnerException?.Message ?? exception.Message));
                continue;
            }

            if (definition is null)
            {
                errors.Add(
                    $"{attribute.Id} ({source}): contract type '{type.FullName}' does not expose a public static " +
                    $"'{DefinitionPropertyName}' property of type '{nameof(ExpertContractDefinition)}'.");
                continue;
            }

            entries.Add(new ContractEntry(
                new ExpertContractDescriptor(attribute.Id, attribute.Version, attribute.Fingerprint),
                definition,
                type,
                source));
        }
    }

    private static bool ValidateEntry(ContractEntry entry, List<string> errors)
    {
        var id = entry.Contract.Id;
        string? error;
        if (FindIdFormatError(id) is { } formatError)
            error = $"{id} ({entry.Source}): {formatError}";
        else if (id.StartsWith(OfficialNamespacePrefix, StringComparison.Ordinal) &&
                 (entry.AbstractType is null || entry.Source != OfficialAssemblyName))
            error = $"{id} ({entry.Source}): the '{OfficialNamespacePrefix}' namespace is reserved for the official " +
                    $"'{OfficialAssemblyName}' assembly; third-party contracts must use their own namespace.";
        else
        {
            var computed = ExpertContractFingerprint.Compute(id, entry.Contract.Version, entry.Definition);
            error = string.Equals(entry.Contract.Fingerprint, computed, StringComparison.Ordinal)
                ? null
                : $"{id} ({entry.Source}): declared fingerprint '{entry.Contract.Fingerprint}' does not match the " +
                  $"fingerprint '{computed}' computed from the contract definition.";
        }

        if (error is null)
            return true;
        errors.Add(error);
        return false;
    }

    private static string? FindIdFormatError(string id)
    {
        var separatorIndex = id.IndexOf('/');
        if (separatorIndex < 0 || id.IndexOf('/', separatorIndex + 1) >= 0)
            return "the id must follow '<namespace>/<name>' with exactly one '/'.";
        var namespaceSegment = id[..separatorIndex];
        var nameSegment = id[(separatorIndex + 1)..];
        if (namespaceSegment.Length == 0 || nameSegment.Length == 0)
            return "neither the namespace nor the name segment may be empty.";
        if (!namespaceSegment.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
            return "the namespace segment allows only lowercase letters, digits, '-', and '.'.";
        return nameSegment.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            ? null
            : "the name segment must be kebab-case: lowercase letters, digits, and '-' only.";
    }

    private static void ValidateVersionLine(IGrouping<string, ContractEntry> idGroup, List<string> errors)
    {
        errors.AddRange(idGroup
            .GroupBy(entry => entry.Contract.Version)
            .Where(versionGroup => versionGroup.Count() > 1)
            .Select(versionGroup =>
                $"expert contract '{idGroup.Key}' version {versionGroup.Key} is declared by multiple sources: " +
                string.Join(", ", versionGroup.Select(entry => $"'{entry.Source}'")) + "."));

        if (idGroup.Select(entry => entry.Contract.Version.Major).Distinct().Count() > 1)
        {
            errors.Add(
                $"expert contract '{idGroup.Key}' declares incompatible major versions: " +
                string.Join(", ", idGroup.Select(entry => $"{entry.Contract.Version} ('{entry.Source}')")) +
                "; only one active major line is allowed.");
        }
    }

    private sealed record ContractEntry(
        ExpertContractDescriptor Contract,
        ExpertContractDefinition Definition,
        Type? AbstractType,
        string Source);
}
