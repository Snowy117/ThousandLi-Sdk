using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class BuildJsonElementTests
{
    [Fact]
    public void BuildJsonElement_SimpleObject_ReturnsObject()
    {
        var events = FeedAll("""{"name":"test","value":42}""");
        var element = events.BuildJsonElement();
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("test", element.GetProperty("name").GetString());
        Assert.Equal(42, element.GetProperty("value").GetInt32());
    }

    [Fact]
    public void BuildJsonElement_NestedObject_ReturnsNestedStructure()
    {
        var events = FeedAll("""{"outer":{"inner":true}}""");
        var element = events.BuildJsonElement();
        Assert.True(element.GetProperty("outer").GetProperty("inner").GetBoolean());
    }

    [Fact]
    public void BuildJsonElement_ArrayInObject_ReturnsArray()
    {
        var events = FeedAll("""{"items":[1,2,3]}""");
        var element = events.BuildJsonElement();
        var items = element.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(3, items.GetArrayLength());
    }

    [Fact]
    public void BuildJsonElement_NullValue_HandledCorrectly()
    {
        var events = FeedAll("""{"x":null}""");
        var element = events.BuildJsonElement();
        Assert.Equal(JsonValueKind.Null, element.GetProperty("x").ValueKind);
    }

    [Fact]
    public void BuildJsonElement_BooleanValue_HandledCorrectly()
    {
        var events = FeedAll("""{"t":true,"f":false}""");
        var element = events.BuildJsonElement();
        Assert.True(element.GetProperty("t").GetBoolean());
        Assert.False(element.GetProperty("f").GetBoolean());
    }

    [Fact]
    public void BuildJsonElement_NumberValue_HandledCorrectly()
    {
        var events = FeedAll("""{"n":3.14}""");
        var element = events.BuildJsonElement();
        Assert.Equal(3.14, element.GetProperty("n").GetDouble());
    }

    [Fact]
    public void BuildJsonElement_ChunkedString_ConcatenatesChunks()
    {
        var parser = new JsonStreamParser();
        var events = FeedAllChunks(parser, "{\"msg\":\"Hel", "lo\"}");
        var element = events.BuildJsonElement();
        Assert.Equal("Hello", element.GetProperty("msg").GetString());
    }

    [Fact]
    public void BuildJsonElement_NoRootValue_ThrowsInvalidOperationException()
    {
        // ReSharper disable once CollectionNeverUpdated.Local
        List<JsonStreamEvent> events = [];
        Assert.Throws<InvalidOperationException>(() => events.BuildJsonElement());
    }

    [Fact]
    public void BuildJsonElement_RootIsNotObject_ThrowsInvalidOperationException()
    {
        var events = FeedAll("[1,2,3]");
        Assert.Throws<InvalidOperationException>(() => events.BuildJsonElement());
    }

    [Fact]
    public void BuildJsonElement_EmptyString_HandledCorrectly()
    {
        var events = FeedAll("""{"s":""}""");
        var element = events.BuildJsonElement();
        Assert.Equal("", element.GetProperty("s").GetString());
    }

    private static List<JsonStreamEvent> FeedAll(params string[] chunks)
    {
        var parser = new JsonStreamParser();
        var events = new List<JsonStreamEvent>();
        foreach (var chunk in chunks) events.AddRange(parser.Feed(chunk));
        events.AddRange(parser.Complete());
        return events;
    }

    private static List<JsonStreamEvent> FeedAllChunks(JsonStreamParser parser, params string[] chunks)
    {
        var events = new List<JsonStreamEvent>();
        foreach (var chunk in chunks) events.AddRange(parser.Feed(chunk));
        events.AddRange(parser.Complete());
        return events;
    }
}
