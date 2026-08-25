using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts;
using ThousandLi.ExpertContracts.Narration;

namespace ThousandLi.Sdk.Tests;

public sealed class OfficialNarratorContractTests
{
    [Fact]
    public void DescriptorCarriesTheOfficialNarratorIdentity()
    {
        var descriptor = AbstractNarratorExpert.Descriptor;

        Assert.Equal("thousandli.expert/narrator", descriptor.Id);
        Assert.Equal(new ContractVersion(1, 0), descriptor.Version);
        Assert.Equal(
            ExpertContractFingerprint.Compute("thousandli.expert/narrator", new ContractVersion(1, 0), AbstractNarratorExpert.Definition),
            descriptor.Fingerprint);
    }

    [Fact]
    public void BaseClassExposesTheContractIdentity()
    {
        Assert.True(typeof(AbstractNarratorExpert).IsAbstract);
        Assert.True(typeof(IExpertContract).IsAssignableFrom(typeof(AbstractNarratorExpert)));
    }

    [Fact]
    public void DefinitionMatchesTheSlice1SampleNarratorShape()
    {
        var definition = AbstractNarratorExpert.Definition;

        Assert.Equal(["chunk"], definition.SemanticEventTypes);
        Assert.Equal(JsonValueKind.Object, definition.InputSchema?.ValueKind);
        Assert.Equal(JsonValueKind.Object, definition.OutputSchema?.ValueKind);

        var requiredInput = definition.InputSchema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString()!)
            .ToArray();
        Assert.Equal(["turn", "player", "action"], requiredInput);

        var requiredOutput = definition.OutputSchema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString()!)
            .ToArray();
        Assert.Equal(["text"], requiredOutput);
    }
}
