using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class ParserEdgeCaseTests
{
    [Fact]
    public void Feed_Null_ThrowsArgumentNullException()
    {
        var parser = new JsonStreamParser();
        Assert.Throws<ArgumentNullException>(() => parser.Feed(null!));
    }

    [Fact]
    public void BooleanTrue_ProducesBooleanValueEvent()
    {
        var events = FeedAll("true");
        var b = Assert.IsType<JsonStreamBooleanValueEvent>(Assert.Single(events));
        Assert.True(b.Value);
    }

    [Fact]
    public void BooleanFalse_ProducesBooleanValueEvent()
    {
        var events = FeedAll("false");
        var b = Assert.IsType<JsonStreamBooleanValueEvent>(Assert.Single(events));
        Assert.False(b.Value);
    }

    [Fact]
    public void NullLiteral_ProducesNullValueEvent()
    {
        var events = FeedAll("null");
        Assert.IsType<JsonStreamNullValueEvent>(Assert.Single(events));
    }

    [Fact]
    public void BooleanSplitAcrossChunks_CompletesCorrectly()
    {
        var parser = new JsonStreamParser();
        parser.Feed("fa");
        var second = parser.Feed("lse");
        var final = parser.Complete();
        var allEvents = second.Concat(final).ToList();
        var b = Assert.IsType<JsonStreamBooleanValueEvent>(Assert.Single(allEvents));
        Assert.False(b.Value);
    }

    [Fact]
    public void NullSplitAcrossChunks_CompletesCorrectly()
    {
        var parser = new JsonStreamParser();
        parser.Feed("nu");
        var second = parser.Feed("ll");
        var final = parser.Complete();
        var allEvents = second.Concat(final).ToList();
        Assert.IsType<JsonStreamNullValueEvent>(Assert.Single(allEvents));
    }

    [Fact]
    public void InvalidLiteral_WrongCharacter_ThrowsJsonStreamException()
    {
        var parser = new JsonStreamParser();
        Assert.Throws<JsonStreamException>(() => parser.Feed("truu"));
    }

    [Fact]
    public void EmptyObject_ProducesStartAndEnd()
    {
        var events = FeedAll("{}");
        Assert.Equal(2, events.Count);
        Assert.IsType<JsonStreamObjectStartedEvent>(events[0]);
        Assert.IsType<JsonStreamObjectCompletedEvent>(events[1]);
    }

    [Fact]
    public void EmptyArray_ProducesStartAndEnd()
    {
        var events = FeedAll("[]");
        Assert.Equal(2, events.Count);
        Assert.IsType<JsonStreamArrayStartedEvent>(events[0]);
        Assert.IsType<JsonStreamArrayCompletedEvent>(events[1]);
    }

    [Fact]
    public void LeadingWhitespace_Skipped()
    {
        var events = FeedAll("  {\"a\":1}");
        Assert.Contains(JsonStreamEvent.ObjectStarted(""), events);
    }

    [Fact]
    public void TrailingWhitespace_Skipped()
    {
        var events = FeedAll("{\"a\":1}  ");
        Assert.Contains(JsonStreamEvent.ObjectCompleted(""), events);
    }

    [Fact]
    public void WhitespaceBetweenTokens_Skipped()
    {
        var events = FeedAll("{ \"a\" : 1 }");
        Assert.Contains(JsonStreamEvent.NumberValue("/a", "1"), events);
    }

    [Fact]
    public void MultipleRootValues_ThrowsJsonStreamException()
    {
        var parser = new JsonStreamParser();
        parser.Feed("{}");
        Assert.Throws<JsonStreamException>(() => parser.Feed("{}"));
    }

    [Fact]
    public void ContainerMismatch_CloseBraceClosesArray_ThrowsJsonStreamException()
    {
        var parser = new JsonStreamParser();
        Assert.Throws<JsonStreamException>(() => parser.Feed("[1}"));
    }

    [Fact]
    public void ContainerMismatch_CloseBracketClosesObject_ThrowsJsonStreamException()
    {
        var parser = new JsonStreamParser();
        Assert.Throws<JsonStreamException>(() => parser.Feed("{\"a\":1]"));
    }

    [Fact]
    public void EmptyStringValue_NoStringChunk()
    {
        var events = FeedAll("{\"s\":\"\"}");
        Assert.Contains(JsonStreamEvent.StringStarted("/s"), events);
        Assert.Contains(JsonStreamEvent.StringCompleted("/s"), events);
        // ReSharper disable once MergeIntoPattern
        Assert.DoesNotContain(events, e => e is JsonStreamStringChunkEvent chunk && string.Equals(chunk.Path, "/s", StringComparison.Ordinal));
    }

    [Fact]
    public void StandaloneString_ProducesEvents()
    {
        var events = FeedAll("\"hello\"");
        Assert.Equal(
            [
                JsonStreamEvent.StringStarted(""),
                JsonStreamEvent.StringChunk("", "hello"),
                JsonStreamEvent.StringCompleted(""),
            ],
            events);
    }

    [Fact]
    public void StandaloneBoolean_ProducesEvent()
    {
        var events = FeedAll("true");
        Assert.Equal([JsonStreamEvent.BooleanValue("", value: true)], events);
    }

    [Fact]
    public void StandaloneNull_ProducesEvent()
    {
        var events = FeedAll("null");
        Assert.Equal([JsonStreamEvent.NullValue("")], events);
    }

    [Fact]
    public void StandaloneNumber_ProducesEvent()
    {
        var events = FeedAll("42");
        Assert.Equal([JsonStreamEvent.NumberValue("", "42")], events);
    }

    [Fact]
    public void CrLfWhitespace_Handled()
    {
        var events = FeedAll("\r\n{\"a\":1}\r\n");
        Assert.Contains(JsonStreamEvent.ObjectStarted(""), events);
    }

    [Fact]
    public void EveryTwoCharSplit_ProducesCorrectEvents()
    {
        var chunks = new[] { "{\"", "a\"", ":", "1}" };
        var events = FeedAll(chunks);
        Assert.Contains(JsonStreamEvent.ObjectStarted(""), events);
        Assert.Contains(JsonStreamEvent.PropertyName("/a", "a"), events);
        Assert.Contains(JsonStreamEvent.NumberValue("/a", "1"), events);
        Assert.Contains(JsonStreamEvent.ObjectCompleted(""), events);
    }

    [Fact]
    public void NestedObjectAndArray_ProducesCorrectPaths()
    {
        var events = FeedAll("""{"a":[{"b":1}]}""");
        Assert.Contains(JsonStreamEvent.ArrayStarted("/a"), events);
        Assert.Contains(JsonStreamEvent.ObjectStarted("/a/0"), events);
        Assert.Contains(JsonStreamEvent.NumberValue("/a/0/b", "1"), events);
    }

    [Fact]
    public void InvalidValueStart_ThrowsJsonStreamException()
    {
        var parser = new JsonStreamParser();
        Assert.Throws<JsonStreamException>(() => parser.Feed("!"));
    }

    [Fact]
    public void StringWithEscapes_ProducesDecodedContent()
    {
        var parser = new JsonStreamParser();
        var events = new List<JsonStreamEvent>();
        events.AddRange(parser.Feed("{\"s\":\"a\\"));
        events.AddRange(parser.Feed("nb\"}"));
        events.AddRange(parser.Complete());
        var chunks = events.OfType<JsonStreamStringChunkEvent>()
            .Where(e => string.Equals(e.Path, "/s", StringComparison.Ordinal))
            .ToList();
        var result = string.Concat(chunks.Select(c => c.Value));
        Assert.Equal("a\nb", result);
    }

    [Fact]
    public void NumberFollowedByObjectInArray_ProducesCorrectEvents()
    {
        var events = FeedAll("[1,{\"x\":2}]");
        Assert.Equal(
            [
                JsonStreamEvent.ArrayStarted(""),
                JsonStreamEvent.NumberValue("/0", "1"),
                JsonStreamEvent.ObjectStarted("/1"),
                JsonStreamEvent.PropertyName("/1/x", "x"),
                JsonStreamEvent.NumberValue("/1/x", "2"),
                JsonStreamEvent.ObjectCompleted("/1"),
                JsonStreamEvent.ArrayCompleted(""),
            ],
            events);
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
