using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertStreamEventModelTests
{
    private sealed class RecordingSink(IEnumerable<string>? declaredRootProperties = null) :
        ExpertStreamEventSinkBase(declaredRootProperties)
    {
        public List<ExpertStreamEvent> Received { get; } = [];

        protected override ValueTask OnTextDeltaAsync(ExpertTextDeltaEvent delta, CancellationToken cancellationToken)
        {
            Received.Add(delta);
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnJsonEventAsync(ExpertJsonStreamEvent streamEvent, CancellationToken cancellationToken)
        {
            Received.Add(streamEvent);
            return ValueTask.CompletedTask;
        }

        public static bool MatchesPrefix(string path, string prefix) =>
            MatchPathPrefix(path, prefix);

        public bool TryGetCompletedString(string path, out string value) =>
            TryGetStringField(path, out value);

        public IReadOnlyDictionary<string, string> CompletedStrings => CompletedStringFields;
    }

    private static async ValueTask FeedAll(IExpertStreamEventSink sink, params ExpertStreamEvent[] events)
    {
        foreach (var streamEvent in events)
            await sink.OnEventAsync(streamEvent);
    }

    [Fact]
    public void TextDeltasAcceptWhitespaceButRejectNullAndEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpertTextDeltaEvent(null!));
        Assert.Throws<ArgumentException>(() => new ExpertTextDeltaEvent(string.Empty));
        Assert.Equal("  ", new ExpertTextDeltaEvent("  ").Delta);
        Assert.Equal("\n\n", new ExpertTextDeltaEvent("\n\n").Delta);
    }

    [Fact]
    public void JsonEventPathsMustBePointersOrRoot()
    {
        Assert.Throws<ArgumentException>(() => new ExpertJsonObjectStartedEvent("narrative"));
        Assert.Equal("", new ExpertJsonObjectStartedEvent("").Path);
        Assert.Equal("/a/b", new ExpertJsonNullValueEvent("/a/b").Path);
    }

    [Fact]
    public void PropertyNamesAndNumbersRejectInvalidValues()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpertJsonPropertyNameEvent("", null!));
        Assert.Equal(string.Empty, new ExpertJsonPropertyNameEvent("", "").Name);
        Assert.Equal(" ", new ExpertJsonPropertyNameEvent("", " ").Name);
        Assert.Throws<ArgumentException>(() => new ExpertJsonNumberValueEvent("/i", ""));
    }

    [Fact]
    public void StringChunksAcceptWhitespaceOnlyContent()
    {
        var chunk = new ExpertJsonStringChunkEvent("/text", " \t ");
        Assert.Equal(" \t ", chunk.Value);
    }

    [Fact]
    public void EventsUseRecordEquality()
    {
        Assert.Equal(new ExpertTextDeltaEvent("hi"), new ExpertTextDeltaEvent("hi"));
        Assert.NotEqual(new ExpertTextDeltaEvent("hi"), new ExpertTextDeltaEvent("ho"));
        Assert.Equal(
            new ExpertJsonPropertyNameEvent("/obj", "name"),
            new ExpertJsonPropertyNameEvent("/obj", "name"));
        Assert.Equal(
            new ExpertJsonBooleanValueEvent("/flag", true),
            new ExpertJsonBooleanValueEvent("/flag", true));
    }

    [Fact]
    public async Task UndeclaredRootPropertiesAreDroppedWhenWhitelistIsDeclared()
    {
        var sink = new RecordingSink(["narrative"]);
        await FeedAll(sink,
            new ExpertJsonObjectStartedEvent(""),
            new ExpertJsonPropertyNameEvent("", "narrative"),
            new ExpertJsonStringStartedEvent("/narrative"),
            new ExpertJsonStringChunkEvent("/narrative", "hello"),
            new ExpertJsonStringCompletedEvent("/narrative"),
            new ExpertJsonPropertyNameEvent("", "afterThinking"),
            new ExpertJsonStringCompletedEvent("/afterThinking"));

        Assert.All(sink.Received, @event =>
        {
            var path = ((ExpertJsonStreamEvent)@event).Path;
            Assert.True(path.Length == 0 || path.StartsWith("/narrative", StringComparison.Ordinal));
        });
        Assert.Contains(sink.Received, @event => @event is ExpertJsonObjectStartedEvent);
        Assert.Contains(sink.Received, @event => @event is ExpertJsonStringChunkEvent);
    }

    [Fact]
    public async Task WithoutAWhitelistEverythingIsForwardedAndTextIsRoutedSeparately()
    {
        var sink = new RecordingSink();
        await FeedAll(sink,
            new ExpertTextDeltaEvent("hello"),
            new ExpertJsonStringCompletedEvent("/secret"));

        Assert.Equal(2, sink.Received.Count);
        Assert.IsType<ExpertTextDeltaEvent>(sink.Received[0]);
        Assert.IsType<ExpertJsonStringCompletedEvent>(sink.Received[1]);
    }

    [Fact]
    public async Task StringFieldsAccumulateIntoCompletedValues()
    {
        var sink = new RecordingSink();
        await FeedAll(sink,
            new ExpertJsonStringStartedEvent("/field"),
            new ExpertJsonStringChunkEvent("/field", "ab"),
            new ExpertJsonStringChunkEvent("/field", "cd"),
            new ExpertJsonStringCompletedEvent("/field"));

        Assert.True(sink.TryGetCompletedString("/field", out var value));
        Assert.Equal("abcd", value);
        Assert.True(sink.CompletedStrings.ContainsKey("/field"));
    }

    [Fact]
    public void PathPrefixMatchingIsSegmentAware()
    {
        Assert.True(RecordingSink.MatchesPrefix("/a/b", "/a"));
        Assert.False(RecordingSink.MatchesPrefix("/ab", "/a"));
        Assert.True(RecordingSink.MatchesPrefix("/a", "/a"));
        Assert.False(RecordingSink.MatchesPrefix("/a/b", "/b"));
        Assert.True(RecordingSink.MatchesPrefix("/a/b", "/"));
    }

    [Fact]
    public async Task DeclaredRootPropertiesMatchUnescapedPointerSegments()
    {
        var sink = new RecordingSink(["a/b", "x~y"]);
        await FeedAll(sink,
            new ExpertJsonPropertyNameEvent("", "a/b"),
            new ExpertJsonStringStartedEvent("/a~1b"),
            new ExpertJsonStringChunkEvent("/a~1b", "value"),
            new ExpertJsonStringCompletedEvent("/a~1b"),
            new ExpertJsonStringStartedEvent("/x~0y"),
            new ExpertJsonStringCompletedEvent("/x~0y"),
            new ExpertJsonPropertyNameEvent("", "undeclared"),
            new ExpertJsonStringChunkEvent("/undeclared", "dropped"));

        Assert.Contains(sink.Received, @event => @event is ExpertJsonStringChunkEvent { Path: "/a~1b" });
        Assert.Contains(sink.Received, @event => @event is ExpertJsonStringCompletedEvent { Path: "/x~0y" });
        Assert.DoesNotContain(sink.Received, @event => @event is ExpertJsonStreamEvent { Path: "/undeclared" });
        Assert.True(sink.TryGetCompletedString("/a~1b", out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public async Task UnknownStreamEventSubtypesAreRejected()
    {
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.OnEventAsync(new ForeignStreamEvent(), CancellationToken.None).AsTask());
    }

    private sealed record ForeignStreamEvent : ExpertStreamEvent;

    [Fact]
    public async Task NullEventsAreRejected()
    {
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sink.OnEventAsync(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public void DeclaredRootPropertyListsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new RecordingSink(["ok", " "]));
        Assert.Throws<ArgumentException>(() => new RecordingSink(["ok", null!]));
    }

    [Fact]
    public async Task AnEmptyDeclaredWhitelistDropsEveryNonRootEvent()
    {
        var sink = new RecordingSink([]);
        await FeedAll(sink,
            new ExpertJsonObjectStartedEvent(""),
            new ExpertJsonStringChunkEvent("/a", "dropped"));

        var root = Assert.Single(sink.Received);
        Assert.Equal(string.Empty, Assert.IsType<ExpertJsonObjectStartedEvent>(root).Path);
    }
}
