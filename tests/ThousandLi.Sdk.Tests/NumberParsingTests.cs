using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class NumberParsingTests
{
    [Fact]
    public void IntegerNumber_ProducesNumberValueEvent()
    {
        var events = FeedAll("42");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("42", num.RawValue);
    }

    [Fact]
    public void Zero_ProducesNumberValueEvent()
    {
        var events = FeedAll("0");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("0", num.RawValue);
    }

    [Fact]
    public void NegativeInteger_ProducesNumberValueEvent()
    {
        var events = FeedAll("-1");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("-1", num.RawValue);
    }

    [Fact]
    public void FloatNumber_ProducesNumberValueEvent()
    {
        var events = FeedAll("3.14");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("3.14", num.RawValue);
    }

    [Fact]
    public void NegativeFloat_ProducesNumberValueEvent()
    {
        var events = FeedAll("-0.5");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("-0.5", num.RawValue);
    }

    [Fact]
    public void ExponentNumber_ProducesNumberValueEvent()
    {
        var events = FeedAll("1.0e10");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("1.0e10", num.RawValue);
    }

    [Fact]
    public void ExponentPositiveSign_ProducesNumberValueEvent()
    {
        var events = FeedAll("1E+5");
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(events));
        Assert.Equal("1E+5", num.RawValue);
    }

    [Fact]
    public void NumberStreamedAcrossChunks_CompletesCorrectly()
    {
        var parser = new JsonStreamParser();
        var first = parser.Feed("1.");
        Assert.Empty(first);
        var second = parser.Feed("5");
        Assert.Empty(second);
        var final = parser.Complete();
        var num = Assert.IsType<JsonStreamNumberValueEvent>(Assert.Single(final));
        Assert.Equal("1.5", num.RawValue);
    }

    [Fact]
    public void NumberInObject_ProducesCorrectPath()
    {
        var events = FeedAll("""{"x":42}""");
        Assert.Contains(JsonStreamEvent.NumberValue("/x", "42"), events);
    }

    [Fact]
    public void NumberInArray_ProducesCorrectPath()
    {
        var events = FeedAll("[3.14]");
        Assert.Contains(JsonStreamEvent.NumberValue("/0", "3.14"), events);
    }

    [Fact]
    public void InvalidNumber_LeadingZeros_ThrowsJsonStreamException()
    {
        Assert.Throws<JsonStreamException>(() => FeedAll("01"));
    }

    [Fact]
    public void InvalidNumber_TrailingDot_ThrowsJsonStreamException()
    {
        Assert.Throws<JsonStreamException>(() => FeedAll("1."));
    }

    [Fact]
    public void InvalidNumber_DotWithoutDigits_ThrowsJsonStreamException()
    {
        Assert.Throws<JsonStreamException>(() => FeedAll("-.5"));
    }

    private static List<JsonStreamEvent> FeedAll(params string[] chunks)
    {
        var parser = new JsonStreamParser();
        var events = new List<JsonStreamEvent>();
        foreach (var chunk in chunks) events.AddRange(parser.Feed(chunk));
        events.AddRange(parser.Complete());
        return events;
    }
}
