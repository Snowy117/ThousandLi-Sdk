using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertTextDeltaEventTests
{
    [Fact]
    public void TextDeltasAcceptWhitespaceButRejectNullAndEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpertTextDeltaEvent(null!));
        Assert.Throws<ArgumentException>(() => new ExpertTextDeltaEvent(string.Empty));
        Assert.Equal("  ", new ExpertTextDeltaEvent("  ").Delta);
        Assert.Equal("\n\n", new ExpertTextDeltaEvent("\n\n").Delta);
    }

    [Fact]
    public void EventsUseRecordEquality()
    {
        Assert.Equal(new ExpertTextDeltaEvent("hi"), new ExpertTextDeltaEvent("hi"));
        Assert.NotEqual(new ExpertTextDeltaEvent("hi"), new ExpertTextDeltaEvent("ho"));
    }
}
