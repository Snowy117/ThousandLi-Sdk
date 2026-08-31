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
        LoadedExpertPackage? package = null)
    {
        package ??= ExpertPackageLoader.Load(ValidArtifact);
        return new LocalExpertComposition(
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
    public void UseRequiresTheCategoryAnchorToCarryTheContractAttribute()
    {
        // The attribute is resolved on the exact category type (inherit: false), so an anchor
        // subtype without its own [ExpertContract] fails fast instead of silently resolving
        // through the base anchor.
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var facade = new LocalExpertFacade(CompositionWithLoadedPackage(package: package));

        var exception = Assert.Throws<InvalidOperationException>(facade.Use<NoContractAbstractExpert>);
        Assert.Contains("[ExpertContract]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseFailsFastWhenNoPackageIsBoundForTheContract()
    {
        using var package = ExpertPackageLoader.Load(ValidArtifact);
        var facade = new LocalExpertFacade(CompositionWithLoadedPackage(package: package));

        var exception = Assert.ThrowsAny<InvalidOperationException>(facade.Use<UnregisteredLongTextWritingExpert>);

        Assert.Contains("tests.unregistered/story", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No expert binding is registered", exception.Message, StringComparison.Ordinal);
    }
}
