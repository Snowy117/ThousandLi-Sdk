using System.Text;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class JsonExpertStreamEventSinkBaseTests
{
    private sealed class TestSink(IEnumerable<string> declaredProperties) : JsonExpertStreamEventSinkBase(declaredProperties)
    {
        public Func<JsonStreamEvent, ValueTask>? OnDeclared { get; init; }

        protected override ValueTask OnDeclaredEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken)
            => OnDeclared?.Invoke(streamEvent) ?? ValueTask.CompletedTask;

        public static bool InvokeMatchPathPrefix(JsonStreamEvent streamEvent, string propertyName)
            => MatchPathPrefix(streamEvent, propertyName);

        public static bool InvokeMatchPathExact(JsonStreamEvent streamEvent, string propertyName)
            => MatchPathExact(streamEvent, propertyName);

        public static string? InvokeAccumulateStringField(JsonStreamEvent streamEvent, string propertyName, StringBuilder buffer)
            => AccumulateStringField(streamEvent, propertyName, buffer);

        public static ExpertStreamArrayBoundary InvokeTrackArrayBoundary(JsonStreamEvent streamEvent, string propertyName)
            => TrackArrayBoundary(streamEvent, propertyName);
    }

    [Fact]
    public async Task WhitelistForwardsDeclaredPropertyEvents()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["narrative", "actionOptions"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/narrative", "hello"), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/actionOptions/0", "Attack"), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.ArrayStarted("/actionOptions"), TestSupport.CancellationToken);

        Assert.Equal(3, received.Count);
    }

    [Fact]
    public async Task WhitelistDropsUndeclaredPropertyEventsSilently()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["narrative"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/rogueField", "leak"), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.StringCompleted("/rogueField"), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/rogueField/nested", "leak2"), TestSupport.CancellationToken);

        Assert.Empty(received);
    }

    [Fact]
    public async Task WhitelistDropsEmptyPathEvents()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["narrative"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.ObjectStarted(""), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.ObjectCompleted(""), TestSupport.CancellationToken);

        Assert.Empty(received);
    }

    [Fact]
    public async Task WhitelistDropsPathsWithoutALeadingSlash()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["narrative"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.StringChunk("narrative", "hello"), TestSupport.CancellationToken);

        Assert.Empty(received);
    }

    [Fact]
    public async Task WhitelistForwardsNestedPathsOfDeclaredProperties()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["dialogues"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/dialogues/0/speaker", "NPC"), TestSupport.CancellationToken);
        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/dialogues/1/text", "Hi"), TestSupport.CancellationToken);

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task WhitelistDoesNotMatchSimilarPropertyNames()
    {
        var received = new List<JsonStreamEvent>();
        var sink = new TestSink(["narrative"])
        {
            OnDeclared = streamEvent =>
            {
                received.Add(streamEvent);
                return ValueTask.CompletedTask;
            }
        };

        await sink.OnEventAsync(JsonStreamEvent.StringChunk("/narrativeExtra", "leak"), TestSupport.CancellationToken);

        Assert.Empty(received);
    }

    [Fact]
    public void MatchPathPrefixMatchesTheExactPath()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/narrative", "x");
        Assert.True(TestSink.InvokeMatchPathPrefix(streamEvent, "narrative"));
    }

    [Fact]
    public void MatchPathPrefixMatchesPrefixedPaths()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/dialogues/0/speaker", "x");
        Assert.True(TestSink.InvokeMatchPathPrefix(streamEvent, "dialogues"));
    }

    [Fact]
    public void MatchPathPrefixRejectsDifferentRoots()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/actionOptions", "x");
        Assert.False(TestSink.InvokeMatchPathPrefix(streamEvent, "narrative"));
    }

    [Fact]
    public void MatchPathPrefixRejectsSimilarPrefixes()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/narrativeExtra", "x");
        Assert.False(TestSink.InvokeMatchPathPrefix(streamEvent, "narrative"));
    }

    [Fact]
    public void MatchPathPrefixRejectsEmptyPaths()
    {
        var streamEvent = JsonStreamEvent.ObjectStarted("");
        Assert.False(TestSink.InvokeMatchPathPrefix(streamEvent, "narrative"));
    }

    [Fact]
    public void MatchPathExactMatchesTheExactPath()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/timeTagStart", "春");
        Assert.True(TestSink.InvokeMatchPathExact(streamEvent, "timeTagStart"));
    }

    [Fact]
    public void MatchPathExactRejectsPrefixedPaths()
    {
        var streamEvent = JsonStreamEvent.StringChunk("/actionOptions/0", "x");
        Assert.False(TestSink.InvokeMatchPathExact(streamEvent, "actionOptions"));
    }

    [Fact]
    public void AccumulateStringFieldReturnsTheFullStringAndClearsTheBuffer()
    {
        var buffer = new StringBuilder();
        Assert.Null(TestSink.InvokeAccumulateStringField(
            JsonStreamEvent.StringChunk("/timeTagStart", "春"), "timeTagStart", buffer));
        Assert.Null(TestSink.InvokeAccumulateStringField(
            JsonStreamEvent.StringChunk("/timeTagStart", "夜"), "timeTagStart", buffer));
        var completed = TestSink.InvokeAccumulateStringField(
            JsonStreamEvent.StringCompleted("/timeTagStart"), "timeTagStart", buffer);
        Assert.Equal("春夜", completed);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void AccumulateStringFieldLeavesUnmatchedPathsUntouched()
    {
        var buffer = new StringBuilder("keep");
        Assert.Null(TestSink.InvokeAccumulateStringField(
            JsonStreamEvent.StringChunk("/other", "x"), "timeTagStart", buffer));
        Assert.Equal("keep", buffer.ToString());
    }

    [Fact]
    public void AccumulateStringFieldThrowsOnNullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TestSink.InvokeAccumulateStringField(
                JsonStreamEvent.StringChunk("/timeTagStart", "x"), "timeTagStart", null!));
    }

    [Fact]
    public void TrackArrayBoundaryReportsStartedAndCompletedOnTheExactPath()
    {
        Assert.Equal(
            ExpertStreamArrayBoundary.Started,
            TestSink.InvokeTrackArrayBoundary(JsonStreamEvent.ArrayStarted("/actionOptions"), "actionOptions"));
        Assert.Equal(
            ExpertStreamArrayBoundary.Completed,
            TestSink.InvokeTrackArrayBoundary(JsonStreamEvent.ArrayCompleted("/actionOptions"), "actionOptions"));
    }

    [Fact]
    public void TrackArrayBoundaryIgnoresNestedOrDifferentPaths()
    {
        Assert.Equal(
            ExpertStreamArrayBoundary.None,
            TestSink.InvokeTrackArrayBoundary(JsonStreamEvent.ArrayStarted("/actionOptions/0"), "actionOptions"));
        Assert.Equal(
            ExpertStreamArrayBoundary.None,
            TestSink.InvokeTrackArrayBoundary(JsonStreamEvent.ArrayStarted("/other"), "actionOptions"));
        Assert.Equal(
            ExpertStreamArrayBoundary.None,
            TestSink.InvokeTrackArrayBoundary(JsonStreamEvent.StringChunk("/actionOptions", "x"), "actionOptions"));
    }

    [Fact]
    public void ConstructorThrowsOnNullDeclaredProperties()
    {
        Assert.Throws<ArgumentNullException>(() => new TestSink(null!));
    }
}
