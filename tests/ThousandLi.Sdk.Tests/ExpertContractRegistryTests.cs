using ThousandLi.BrokenContractFixtures;
using ThousandLi.Contracts;
using JetBrains.Annotations;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertContractRegistryTests
{
    private static ContractVersion V(int major, int minor) => new(major, minor);

    private static ExpertContractRegistration Registration(
        string id,
        ContractVersion version,
        string? fingerprint = null,
        ExpertContractDefinition? definition = null,
        string source = "tests/explicit")
    {
        definition ??= new ExpertContractDefinition();
        return new ExpertContractRegistration(
            new ExpertContractDescriptor(id, version, fingerprint ?? ExpertContractFingerprint.Compute(id, version, definition)),
            definition,
            source);
    }

    [Fact]
    public void OfficialLongTextWritingContractResolvesFromTheOfficialAssembly()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);

        Assert.True(registry.TryGetContract("thousandli.expert/long-text-writing", out var contract));
        Assert.Equal("thousandli.expert/long-text-writing", contract.Id);
        Assert.Equal(new ContractVersion(1, 0), contract.Version);
        Assert.Equal(typeof(AbstractLongTextWritingExpert), contract.AbstractType);
        Assert.Equal("ThousandLi.Contracts", contract.Source);
        Assert.Equal(
            ExpertContractFingerprint.Compute("thousandli.expert/long-text-writing", new ContractVersion(1, 0), AbstractLongTextWritingExpert.Definition),
            contract.Fingerprint);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor, contract.ToDescriptor());
        Assert.Contains("chunk", contract.Definition.SemanticEventTypes);
    }

    [Fact]
    public void MultipleAssembliesAndExplicitRegistrationsCoexist()
    {
        var registry = new ExpertContractRegistry(
            [typeof(AbstractLongTextWritingExpert).Assembly, typeof(ExpertContractRegistryTests).Assembly],
            [Registration("tests/explicit-contract", V(1, 0))]);

        Assert.NotNull(registry.GetRequiredContract("thousandli.expert/long-text-writing"));
        Assert.NotNull(registry.GetRequiredContract("tests/good-contract"));
        Assert.NotNull(registry.GetRequiredContract("tests/explicit-contract"));
        Assert.Equal(3, registry.Contracts.Count);
    }

    [Fact]
    public void DuplicateExactVersionIsRejectedWithStableIdAndSources()
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([],
            [
                Registration("tests.acme/duplicate", V(1, 0), source: "tests/source-a"),
                Registration("tests.acme/duplicate", V(1, 0), source: "tests/source-b")
            ]));

        Assert.Contains("tests.acme/duplicate", exception.Message);
        Assert.Contains("tests/source-a", exception.Message);
        Assert.Contains("tests/source-b", exception.Message);
    }

    [Fact]
    public void IncompatibleMajorVersionsAreRejected()
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([],
            [
                Registration("tests.acme/split", V(1, 0), source: "tests/source-a"),
                Registration("tests.acme/split", V(2, 0), source: "tests/source-b")
            ]));

        Assert.Contains("tests.acme/split", exception.Message);
        Assert.Contains("incompatible major versions", exception.Message);
        Assert.Contains("1.0", exception.Message);
        Assert.Contains("2.0", exception.Message);
    }

    [Fact]
    public void CompatibleMinorCoLineActivatesTheHighestMinorVersion()
    {
        var registry = new ExpertContractRegistry([],
        [
            Registration("tests.acme/coline", V(1, 0), source: "tests/source-a"),
            Registration("tests.acme/coline", V(1, 3), source: "tests/source-b"),
            Registration("tests.acme/coline", V(1, 2), source: "tests/source-c")
        ]);

        var contract = registry.GetRequiredContract("tests.acme/coline");
        Assert.Equal(new ContractVersion(1, 3), contract.Version);
        Assert.Equal("tests/source-b", contract.Source);
        Assert.Equal("tests.acme/coline", registry.Contracts.Single(entry => entry.Id == "tests.acme/coline").Id);
    }

    [Fact]
    public void FingerprintMismatchIsRejectedWithDeclaredAndComputedValues()
    {
        var definition = new ExpertContractDefinition();
        var computed = ExpertContractFingerprint.Compute("tests.acme/mismatch", V(1, 0), definition);
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([],
                [Registration("tests.acme/mismatch", V(1, 0), fingerprint: "declared-not-computed", definition: definition)]));

        Assert.Contains("tests.acme/mismatch", exception.Message);
        Assert.Contains("declared-not-computed", exception.Message);
        Assert.Contains(computed, exception.Message);
    }

    [Theory]
    [InlineData("thousandli.expert/custom")]
    [InlineData("no-slash")]
    [InlineData("double/slash/name")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("UPPER/name")]
    [InlineData("under_score/name")]
    [InlineData("tests.acme/Bad_Name")]
    [InlineData("tests.acme/name with space")]
    public void InvalidContractIdsAreRejected(string id)
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([], [Registration(id, V(1, 0), fingerprint: "irrelevant")]));

        Assert.Contains(id, exception.Message);
    }

    [Fact]
    public void BrokenFixtureAssemblyAggregatesAllDiscoveryErrors()
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([typeof(FixtureAssembly).Assembly]));

        Assert.Contains("is declared by multiple sources", exception.Message);
        Assert.Contains("must be an abstract class", exception.Message);
        Assert.Contains("must implement 'IExpertContract'", exception.Message);
        Assert.Contains("thousandli.expert/squatter", exception.Message);
        Assert.Contains("reserved", exception.Message);
        Assert.Contains("fixtures.example/bad-fingerprint", exception.Message);
        Assert.Contains("does not match", exception.Message);
        Assert.Contains("fixtures.example/Bad_Name", exception.Message);
        Assert.Contains("kebab-case", exception.Message);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpertContractRegistry(contractAssemblies: null!));
        Assert.Throws<ArgumentNullException>(() => new ExpertContractRegistry([null!]));
        Assert.Throws<ArgumentNullException>(() => new ExpertContractRegistry([], [null!]));
    }

    [Fact]
    public void LookupRejectsNullId()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);

        Assert.Throws<ArgumentNullException>(() => registry.TryGetContract(null!, out _));
        Assert.Throws<ArgumentNullException>(() => registry.GetRequiredContract(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void LookupRejectsBlankId(string id)
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);

        Assert.Throws<ArgumentException>(() => registry.TryGetContract(id, out _));
        Assert.Throws<ArgumentException>(() => registry.GetRequiredContract(id));
    }

    [Fact]
    public void GetRequiredContractThrowsForUnknownId()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => registry.GetRequiredContract("tests.acme/unknown"));
        Assert.Contains("tests.acme/unknown", exception.Message);
        Assert.False(registry.TryGetContract("tests.acme/unknown", out var contract));
        Assert.Null(contract);
    }

    [Fact]
    public void ValidateGameRequirementsPassesForSatisfiedRequirements()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [AbstractLongTextWritingExpert.Descriptor]);

        registry.ValidateGameRequirements(compatibility);
    }

    [Fact]
    public void ValidateGameRequirementsListsMissingAndAvailableContracts()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [new ExpertContractDescriptor("tests.acme/missing", V(1, 0), "any")]);

        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => registry.ValidateGameRequirements(compatibility));

        Assert.Contains("tests.acme/missing", exception.Message);
        Assert.Contains("thousandli.expert/long-text-writing", exception.Message);
    }

    [Fact]
    public void ValidateGameRequirementsRejectsUnsupportedVersions()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [new ExpertContractDescriptor("thousandli.expert/long-text-writing", V(1, 2), "any")]);

        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => registry.ValidateGameRequirements(compatibility));

        Assert.Contains("thousandli.expert/long-text-writing", exception.Message);
        Assert.Contains("1.0", exception.Message);
        Assert.Contains("1.2", exception.Message);
    }

    [Fact]
    public void EmptyRegistryRejectsAnyRequirementWithNoAvailableContracts()
    {
        var registry = new ExpertContractRegistry([]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [new ExpertContractDescriptor("tests.acme/missing", V(1, 0), "any")]);

        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => registry.ValidateGameRequirements(compatibility));

        Assert.Contains("Registered expert contracts: none", exception.Message);
    }

    [Fact]
    public void PassingTheSameAssemblyTwiceRegistersItsContractsOnce()
    {
        var official = typeof(AbstractLongTextWritingExpert).Assembly;
        var registry = new ExpertContractRegistry([official, official]);

        Assert.True(registry.TryGetContract("thousandli.expert/long-text-writing", out _));
        Assert.Single(registry.Contracts);
    }

    [Fact]
    public void DuplicateTypesWithinASingleAssemblyAreStillRejected()
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([typeof(FixtureAssembly).Assembly]));

        Assert.Contains("fixtures.example/duplicate", exception.Message);
        Assert.Contains("is declared by multiple sources", exception.Message);
    }

    [Fact]
    public void RegistryContractsAreSortedById()
    {
        var registry = new ExpertContractRegistry(
            [typeof(AbstractLongTextWritingExpert).Assembly],
            [
                Registration("tests/zulu", V(1, 0)),
                Registration("tests/alpha", V(1, 0))
            ]);

        var registeredIds = registry.Contracts.Select(contract => contract.Id).ToArray();
        Assert.Equal(["tests/alpha", "tests/zulu", "thousandli.expert/long-text-writing"], registeredIds);
    }

    [Fact]
    public void RegistryContractsCollectionIsReadOnly()
    {
        var registry = new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]);

        Assert.True(((ICollection<RegisteredExpertContract>)registry.Contracts).IsReadOnly);
    }

    [Fact]
    public void OfficialNamespaceIsReservedAgainstExplicitDescriptorRegistrations()
    {
        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => new ExpertContractRegistry([], [Registration("thousandli.expert/custom", V(1, 0))]));

        Assert.Contains("thousandli.expert/custom", exception.Message);
        Assert.Contains("reserved", exception.Message);
    }

    [Fact]
    public void DerivedContractClassesDoNotInheritTheContractAttribute()
    {
        var registry = new ExpertContractRegistry([typeof(DerivedLongTextExpert).Assembly]);

        Assert.True(typeof(AbstractLongTextWritingExpert).IsAssignableFrom(typeof(DerivedLongTextExpert)));
        Assert.True(registry.TryGetContract("tests/good-contract", out _));
        Assert.False(registry.TryGetContract("thousandli.expert/long-text-writing", out _));
    }

    [Fact]
    public void ValidateGameRequirementsSupportsLowerMinorRequirement()
    {
        var registry = new ExpertContractRegistry([], [Registration("tests.acme/coline", V(1, 1))]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [new ExpertContractDescriptor("tests.acme/coline", V(1, 0), "any")]);

        registry.ValidateGameRequirements(compatibility);
    }

    [Fact]
    public void ValidateGameRequirementsRejectsCrossMajorRequirement()
    {
        var registry = new ExpertContractRegistry([], [Registration("tests.acme/coline", V(1, 1))]);
        var compatibility = new GamePackageCompatibility(
            SdkContracts.Runtime,
            frontend: null,
            expertContracts: [new ExpertContractDescriptor("tests.acme/coline", V(2, 0), "any")]);

        var exception = Assert.Throws<ExpertContractRegistryException>(
            () => registry.ValidateGameRequirements(compatibility));

        Assert.Contains("tests.acme/coline", exception.Message);
        Assert.Contains("2.0", exception.Message);
    }

    [Fact]
    public void ValidateGameRequirementsPassesTriviallyWithoutRequirements()
    {
        var registry = new ExpertContractRegistry([]);
        var compatibility = new GamePackageCompatibility(SdkContracts.Runtime, frontend: null, expertContracts: []);

        registry.ValidateGameRequirements(compatibility);
    }
}

// Discovered through the [ExpertContract] scan of this test assembly; never referenced directly.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
[ExpertContract("tests/good-contract", 1, 0, "d150d4f1dca4e867be84ef9b6e2201695dd75c7873ab605afa0b86f20962e6ce")]
internal abstract class TestAssemblyGoodContract : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new(semanticEventTypes: ["tick"]);
}

internal sealed class DerivedLongTextExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
