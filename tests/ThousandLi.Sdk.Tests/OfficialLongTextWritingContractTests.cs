using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class OfficialLongTextWritingContractTests
{
    [Fact]
    public void DescriptorCarriesTheOfficialCategoryIdentity()
    {
        var descriptor = AbstractLongTextWritingExpert.Descriptor;

        Assert.Equal("thousandli.expert/long-text-writing", descriptor.Id);
        Assert.Equal(new ContractVersion(1, 0), descriptor.Version);
        Assert.Equal(
            ExpertContractFingerprint.Compute(
                "thousandli.expert/long-text-writing",
                new ContractVersion(1, 0),
                AbstractLongTextWritingExpert.Definition),
            descriptor.Fingerprint);
    }

    [Fact]
    public void AnchorExposesTheContractIdentity()
    {
        Assert.True(typeof(AbstractLongTextWritingExpert).IsAbstract);
        Assert.True(typeof(IExpertContract).IsAssignableFrom(typeof(AbstractLongTextWritingExpert)));
        Assert.NotNull(typeof(AbstractLongTextWritingExpert)
            .GetCustomAttributes(typeof(ExpertContractAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public void DefinitionMatchesTheFirstVersionCategoryShape()
    {
        var definition = AbstractLongTextWritingExpert.Definition;

        Assert.Equal(["actionOption", "chunk", "jsonStream", "timetag"], definition.SemanticEventTypes);
        Assert.Equal(JsonValueKind.Object, definition.InputSchema?.ValueKind);
        Assert.Equal(JsonValueKind.Object, definition.OutputSchema?.ValueKind);

        var requiredInput = definition.InputSchema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString()!)
            .ToArray();
        Assert.Equal(["worldSettings", "playerInput"], requiredInput);

        var inputProperties = definition.InputSchema!.Value.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(
            ["currentState", "features", "historyBuckets", "playerInput", "playerPersona", "primaryOutput", "stateSchema", "worldSettings"],
            inputProperties.Order(StringComparer.Ordinal));
    }
}
