using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class JsonStreamParserTests
{
    [Fact]
    public void StreamsStringChunksBeforeObjectCloses()
    {
        var parser = new JsonStreamParser();

        var first = parser.Feed("{\"message\":\"Hel");
        Assert.Equal(
            [
                JsonStreamEvent.ObjectStarted(""),
                JsonStreamEvent.PropertyName("/message", "message"),
                JsonStreamEvent.StringStarted("/message"),
                JsonStreamEvent.StringChunk("/message", "Hel"),
            ],
            first);

        var second = parser.Feed("lo\"}");
        Assert.Equal(
            [
                JsonStreamEvent.StringChunk("/message", "lo"),
                JsonStreamEvent.StringCompleted("/message"),
                JsonStreamEvent.ObjectCompleted(""),
            ],
            second);

        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void DecodesEscapedStringContentAcrossChunks()
    {
        var events = FeedAll(
            "{\"text\":\"A\\",
            "nB\\u00",
            "E9\\\\C\\\"\"}");

        Assert.Equal("A\nBé\\C\"", ConcatenateStringChunks(events, "/text"));
        Assert.Contains(JsonStreamEvent.StringCompleted("/text"), events);
        Assert.Contains(JsonStreamEvent.ObjectCompleted(""), events);
    }

    [Fact]
    public void DecodesUnicodeSurrogatePairAcrossChunks()
    {
        var events = FeedAll(
            "{\"emoji\":\"\\uD8",
            "3D\\u",
            "DE00 ok\"}");

        Assert.Equal("😀 ok", ConcatenateStringChunks(events, "/emoji"));
    }

    [Fact]
    public void EmitsArrayObjectAndPrimitiveEventsWithJsonPointerPaths()
    {
        var events = FeedAll("[{\"a\":1},true,null,\"x\"]");

        Assert.Equal(
            [
                JsonStreamEvent.ArrayStarted(""),
                JsonStreamEvent.ObjectStarted("/0"),
                JsonStreamEvent.PropertyName("/0/a", "a"),
                JsonStreamEvent.NumberValue("/0/a", "1"),
                JsonStreamEvent.ObjectCompleted("/0"),
                JsonStreamEvent.BooleanValue("/1", value: true),
                JsonStreamEvent.NullValue("/2"),
                JsonStreamEvent.StringStarted("/3"),
                JsonStreamEvent.StringChunk("/3", "x"),
                JsonStreamEvent.StringCompleted("/3"),
                JsonStreamEvent.ArrayCompleted(""),
            ],
            events);
    }

    [Fact]
    public void EscapesPropertyNamesInJsonPointerPaths()
    {
        var events = FeedAll("{\"a/b~c\":{\"x\":null}}");

        Assert.Contains(JsonStreamEvent.PropertyName("/a~1b~0c", "a/b~c"), events);
        Assert.Contains(JsonStreamEvent.ObjectStarted("/a~1b~0c"), events);
        Assert.Contains(JsonStreamEvent.NullValue("/a~1b~0c/x"), events);
    }

    [Fact]
    public void ThrowsWhenUnicodeEscapeIsIncompleteAtEndOfInput()
    {
        var parser = new JsonStreamParser();

        parser.Feed("{\"x\":\"\\u12");

        Assert.Throws<JsonStreamException>(parser.Complete);
    }

    [Fact]
    public void ThrowsOnTrailingComma()
    {
        var parser = new JsonStreamParser();

        Assert.Throws<JsonStreamException>(() => parser.Feed("[1,]"));
    }

    private static List<JsonStreamEvent> FeedAll(params string[] chunks)
    {
        var parser = new JsonStreamParser();
        var events = new List<JsonStreamEvent>();

        foreach (var chunk in chunks) events.AddRange(parser.Feed(chunk));

        events.AddRange(parser.Complete());
        return events;
    }

    private static string ConcatenateStringChunks(IEnumerable<JsonStreamEvent> events, string path)
    {
        return string.Concat(
            events.OfType<JsonStreamStringChunkEvent>().Where(e => string.Equals(e.Path, path, StringComparison.Ordinal)).Select(e => e.Value));
    }
}
