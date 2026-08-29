using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class CompleteErrorTests
{
    [Fact]
    public void Complete_EmptyInput_ThrowsNoCompleteValue()
    {
        var parser = new JsonStreamParser();
        var ex = Assert.Throws<JsonStreamException>(parser.Complete);
        Assert.Contains("No complete JSON value was provided", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_UnclosedObject_ThrowsIncompleteContainer()
    {
        var parser = new JsonStreamParser();
        parser.Feed("{\"a\":1");
        var ex = Assert.Throws<JsonStreamException>(parser.Complete);
        Assert.Contains("Incomplete JSON container", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_UnclosedArray_ThrowsIncompleteContainer()
    {
        var parser = new JsonStreamParser();
        parser.Feed("[1,2");
        var ex = Assert.Throws<JsonStreamException>(parser.Complete);
        Assert.Contains("Incomplete JSON container", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_UnterminatedString_Throws()
    {
        var parser = new JsonStreamParser();
        parser.Feed("\"hello");
        var ex = Assert.Throws<JsonStreamException>(parser.Complete);
        Assert.Contains("Unterminated JSON string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_IncompleteLiteral_ThrowsIncompleteLiteral()
    {
        var parser = new JsonStreamParser();
        parser.Feed("tru");
        var ex = Assert.Throws<JsonStreamException>(parser.Complete);
        Assert.Contains("Incomplete JSON literal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tru", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_ValidSingleObject_Succeeds()
    {
        var parser = new JsonStreamParser();
        parser.Feed("{}");
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void Complete_ValidSingleArray_Succeeds()
    {
        var parser = new JsonStreamParser();
        parser.Feed("[]");
        Assert.Empty(parser.Complete());
    }
}
