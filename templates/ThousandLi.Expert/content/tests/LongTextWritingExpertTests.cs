using ThousandLi.Contracts;

namespace ThousandLi.TemplateName.Tests;

public sealed class LongTextWritingExpertTests
{
    [Fact]
    public void LongTextWritingExpertDerivesFromTheOfficialCategoryAnchor()
    {
        var expert = new LongTextWritingExpert();

        Assert.IsAssignableFrom<AbstractLongTextWritingExpert>(expert);
        Assert.Equal("thousandli.expert/long-text-writing", AbstractLongTextWritingExpert.Descriptor.Id);
    }
}
