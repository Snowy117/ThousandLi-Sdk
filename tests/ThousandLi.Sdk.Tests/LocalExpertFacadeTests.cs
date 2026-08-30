using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;
using ThousandLi.LocalExpertNoContractFixture;
using ThousandLi.UnregisteredContractFixture;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalExpertFacadeTests
{
    private static string ValidArtifact =>
        Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "tests", "ThousandLi.LocalExpertFixture", "bin", "Debug", "net10.0", "PackageArtifact");

    private static LocalExpertComposition CompositionWithLoadedPackage(
        ExpertContractRegistry? registry = null,
        LoadedExpertPackage? package = null)
    {
        package ??= ExpertPackageLoader.Load(ValidArtifact);
        return new LocalExpertComposition(
            registry ?? new ExpertContractRegistry([typeof(AbstractLongTextWritingExpert).Assembly]),
            [package],
            null,
            new LocalExpertCompositionOptions(
                new RecordedBasicAi(["test-model"], [], []),
                new BoundPlayerProfile(TestSupport.PlayerId, "Creator", "Curious explorer")));
    }

    [Fact]
    public void UseCreatesDistinctBoundInstancesPerCall()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var facade = new LocalExpertFacade(CompositionWithLoadedPackage(package: package));

        var first = facade.Use<AbstractLongTextWritingExpert>();
        var second = facade.Use<AbstractLongTextWritingExpert>();

        Assert.NotSame(first, second);
        Assert.Equal("ThousandLi.LocalExpertFixture.RecordedLongTextWritingExpert", first.GetType().FullName);
    }

    [Fact]
    public void UseResolvesTheCategoryContractThroughInheritedAnchorAttributes()
    {
        // The cast failure (not an attribute-lookup failure) proves the category identity is
        // inherited from the anchor: implementing the contract by inheritance (design D1.1).
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var facade = new LocalExpertFacade(CompositionWithLoadedPackage(package: package));

        Assert.Throws<InvalidCastException>(facade.Use<LocalAbstractExpert>);
    }

    [Fact]
    public void UseFailsFastWhenNoPackageIsBoundForTheContract()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var registry = new ExpertContractRegistry(
            [typeof(AbstractLongTextWritingExpert).Assembly, typeof(UnregisteredLongTextWritingExpert).Assembly]);
        var facade = new LocalExpertFacade(CompositionWithLoadedPackage(registry, package));

        var exception = Assert.Throws<LocalExpertException>(facade.Use<UnregisteredLongTextWritingExpert>);

        Assert.Contains("tests.unregistered/story", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No local Expert Package is bound", exception.Message, StringComparison.Ordinal);
    }
}
