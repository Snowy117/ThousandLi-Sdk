using ThousandLi.ExpertContracts.Narration;

namespace ThousandLi.TemplateName.Tests;

public sealed class NarratorExpertTests
{
    [Fact]
    public void NarratorExpertDerivesFromTheOfficialContractBase()
    {
        var expert = new NarratorExpert();

        Assert.IsAssignableFrom<AbstractNarratorExpert>(expert);
        Assert.Equal("thousandli.expert/narrator", AbstractNarratorExpert.Descriptor.Id);
    }
}
