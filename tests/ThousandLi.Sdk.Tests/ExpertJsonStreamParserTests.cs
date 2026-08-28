using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertJsonStreamParserTests
{

    private static IReadOnlyList<(string Path, string Value)> ConcatenateChunksPerPath(
        IReadOnlyList<ExpertJsonStreamEvent> events) =>
        [.. events.OfType<ExpertJsonStringChunkEvent>()
            .GroupBy(chunk => chunk.Path)
            .Select(group => (group.Key, string.Concat(group.Select(chunk => chunk.Value))))];

    private static List<ExpertJsonStreamEvent> ParseAll(string json, int? chunkSize = null)
    {
        var parser = new ExpertJsonStreamParser();
        var events = new List<ExpertJsonStreamEvent>();
        if (chunkSize is null)
        {
            events.AddRange(parser.Feed(json.AsSpan()));
        }
        else
        {
            for (var index = 0; index < json.Length; index += chunkSize.Value)
            {
                var length = Math.Min(chunkSize.Value, json.Length - index);
                events.AddRange(parser.Feed(json.AsSpan(index, length)));
            }
        }

        events.AddRange(parser.Complete());
        return events;
    }

    private static void Describe(IReadOnlyList<ExpertJsonStreamEvent> events) =>
        Assert.Equal(
            events.Select(@event => @event switch
            {
                ExpertJsonObjectStartedEvent => "ObjectStarted",
                ExpertJsonObjectCompletedEvent => "ObjectCompleted",
                ExpertJsonArrayStartedEvent => "ArrayStarted",
                ExpertJsonArrayCompletedEvent => "ArrayCompleted",
                ExpertJsonPropertyNameEvent name => $"Name({name.Path},{name.Name})",
                ExpertJsonStringStartedEvent started => $"StringStarted({started.Path})",
                ExpertJsonStringChunkEvent chunk => $"Chunk({chunk.Path},{chunk.Value})",
                ExpertJsonStringCompletedEvent completed => $"StringCompleted({completed.Path})",
                ExpertJsonNumberValueEvent number => $"Number({number.Path},{number.RawValue})",
                ExpertJsonBooleanValueEvent boolean => $"Bool({boolean.Path},{boolean.Value})",
                ExpertJsonNullValueEvent => "Null",
                _ => throw new InvalidOperationException(@event.GetType().FullName)
            }),
            [
                "ObjectStarted",
                "Name(,n)",
                "StringStarted(/n)",
                "Chunk(/n,v)",
                "StringCompleted(/n)",
                "Name(,i)",
                "Number(/i,42)",
                "Name(,b)",
                "Bool(/b,True)",
                "Name(,f)",
                "Bool(/f,False)",
                "Name(,z)",
                "Null",
                "ObjectCompleted"
            ]);

    [Fact]
    public void FlatObjectWithEveryLeafKindProducesOrderedEvents()
    {
        var events = ParseAll("""{"n":"v","i":42,"b":true,"f":false,"z":null}""");
        Describe(events);
    }

    [Fact]
    public void NestedContainersUsePointerPathsAndEscapeTokens()
    {
        var events = ParseAll("""{"a":{"b c":[1,{"d/e":"x"}]}}""");
        var paths = events.Select(@event => @event.Path).ToArray();

        Assert.Equal(
            [
                "",
                "",
                "/a",
                "/a",
                "/a/b c",
                "/a/b c/0",
                "/a/b c/1",
                "/a/b c/1",
                "/a/b c/1/d~1e",
                "/a/b c/1/d~1e",
                "/a/b c/1/d~1e",
                "/a/b c/1",
                "/a/b c",
                "/a",
                ""
            ],
            paths);
        Assert.Equal(
            [
                typeof(ExpertJsonObjectStartedEvent),
                typeof(ExpertJsonPropertyNameEvent),
                typeof(ExpertJsonObjectStartedEvent),
                typeof(ExpertJsonPropertyNameEvent),
                typeof(ExpertJsonArrayStartedEvent),
                typeof(ExpertJsonNumberValueEvent),
                typeof(ExpertJsonObjectStartedEvent),
                typeof(ExpertJsonPropertyNameEvent),
                typeof(ExpertJsonStringStartedEvent),
                typeof(ExpertJsonStringChunkEvent),
                typeof(ExpertJsonStringCompletedEvent),
                typeof(ExpertJsonObjectCompletedEvent),
                typeof(ExpertJsonArrayCompletedEvent),
                typeof(ExpertJsonObjectCompletedEvent),
                typeof(ExpertJsonObjectCompletedEvent)
            ],
            events.Select(@event => @event.GetType()).ToArray());
        var propertyName = Assert.IsType<ExpertJsonPropertyNameEvent>(events[7]);
        Assert.Equal("d/e", propertyName.Name);
        var arrayStart = Assert.IsType<ExpertJsonArrayStartedEvent>(events[4]);
        Assert.Equal("/a/b c", arrayStart.Path);
    }

    [Fact]
    public void TildeAndSlashInNamesAreEscapedIntoPaths()
    {
        var events = ParseAll("""{"~x":1,"a/b":2}""");
        Assert.Contains(events, @event => @event is ExpertJsonNumberValueEvent { Path: "/~0x" });
        Assert.Contains(events, @event => @event is ExpertJsonNumberValueEvent { Path: "/a~1b" });
    }

    [Fact]
    public void EmptyContainersProduceBalancedStructuralEvents()
    {
        var events = ParseAll("""{"o":{},"a":[]}""");

        Assert.Equal(
            [
                typeof(ExpertJsonObjectStartedEvent),
                typeof(ExpertJsonPropertyNameEvent),
                typeof(ExpertJsonObjectStartedEvent),
                typeof(ExpertJsonObjectCompletedEvent),
                typeof(ExpertJsonPropertyNameEvent),
                typeof(ExpertJsonArrayStartedEvent),
                typeof(ExpertJsonArrayCompletedEvent),
                typeof(ExpertJsonObjectCompletedEvent)
            ],
            events.Select(@event => @event.GetType()).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void ArbitraryChunkBoundariesPreserveTheEventSequence(int? chunkSize)
    {
        const string json = """{"text":"he\u00e9llo \ud83d\ude00","nums":[12.5e-3,-4],"flags":[true,false,null]}""";
        var whole = ParseAll(json);
        var chunked = ParseAll(json, chunkSize);

        Assert.Equal(
            whole.Where(@event => @event is not ExpertJsonStringChunkEvent)
                .Select(@event => (@event.GetType(), @event.Path)),
            chunked.Where(@event => @event is not ExpertJsonStringChunkEvent)
                .Select(@event => (@event.GetType(), @event.Path)));
        Assert.Equal(
            ConcatenateChunksPerPath(whole),
            ConcatenateChunksPerPath(chunked));
    }

    [Fact]
    public void StringChunksFlushAtFeedBoundaries()
    {
        var parser = new ExpertJsonStreamParser();
        var first = parser.Feed("{\"t\":\"ab".AsSpan());
        var second = parser.Feed("cd\"}".AsSpan());
        var all = first.Concat(second).Concat(parser.Complete()).ToArray();

        var chunks = all.OfType<ExpertJsonStringChunkEvent>().Select(chunk => chunk.Value).ToArray();
        Assert.Equal(["ab", "cd"], chunks);
        Assert.Contains(all, @event => @event is ExpertJsonStringStartedEvent { Path: "/t" });
        Assert.Contains(all, @event => @event is ExpertJsonStringCompletedEvent { Path: "/t" });
    }

    [Fact]
    public void SurrogatePairEscapesDecodeIntoSingleCodePoint()
    {
        var events = ParseAll("""{"emoji":"\ud83d\ude00"}""");
        var chunk = Assert.Single(events.OfType<ExpertJsonStringChunkEvent>());
        Assert.Equal("\U0001F600", chunk.Value);
    }

    [Fact]
    public void WhitespaceIsToleratedBetweenTokens()
    {
        var events = ParseAll("  { \"n\" : [ 1 , true ] }  ");
        Assert.Equal(
            [typeof(ExpertJsonNumberValueEvent), typeof(ExpertJsonBooleanValueEvent)],
            events
                .Where(@event => @event is ExpertJsonNumberValueEvent or ExpertJsonBooleanValueEvent)
                .Select(@event => @event.GetType())
                .ToArray());
    }

    [Theory]
    [InlineData("\"scalar\"")]
    [InlineData("42")]
    [InlineData("-1.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    public void ScalarOrEmptyRootsAreRejected(string json)
    {
        Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));
    }

    [Theory]
    [InlineData("{}trailing")]
    [InlineData("[] x")]
    public void TrailingContentIsRejected(string json)
    {
        Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));
    }

    [Theory]
    [InlineData("""{"a":""")]
    [InlineData("[1")]
    [InlineData("""{"a":tru""")]
    public void IncompleteStreamsAreRejectedAtComplete(string json)
    {
        var parser = new ExpertJsonStreamParser();
        parser.Feed(json.AsSpan());
        Assert.Throws<ExpertJsonStreamException>(parser.Complete);
    }

    [Theory]
    [InlineData("""{"a":"\q"}""")]
    [InlineData("""{"a":"\uZZZZ"}""")]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"a":"\ud800A"}""")]
    [InlineData("[01]")]
    [InlineData("[1.]")]
    [InlineData("[+1]")]
    [InlineData("[truX]")]
    public void MalformedTokensAreRejected(string json)
    {
        Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));
    }

    [Theory]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"a":"\ud800A"}""")]
    [InlineData("""{"a":"x\udc00y"}""")]
    [InlineData("""{"a":"\ud800x\udc00"}""")]
    public void UnpairedEscapedSurrogatesFailExactlyWhereSystemTextJsonFails(string json)
    {
        Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));

        // System.Text.Json parses the document but cannot materialize its string values; the
        // stream parser surfaces the equivalent failure at parse time.
        Assert.Throws<InvalidOperationException>(
            () => JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(json)));
    }

    [Fact]
    public void NestingBeyondTheDepthLimitIsRejected()
    {
        var json = new string('[', 200) + new string(']', 200);
        var exception = Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));
        Assert.Contains("nesting depth", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawControlCharactersInsideStringsAreRejected()
    {
        Assert.Throws<ExpertJsonStreamException>(() => ParseAll("{\"a\":\"x\u0001y\"}"));
    }

    [Fact]
    public void ErrorPositionsReferenceTheOffendingCharacter()
    {
        var parser = new ExpertJsonStreamParser();
        parser.Feed("{\"a\": 1".AsSpan());
        var exception = Assert.Throws<ExpertJsonStreamException>(() => parser.Feed(",@}".AsSpan()));
        Assert.Equal(1, exception.Line);
        Assert.Equal(9, exception.Column);
    }

    [Fact]
    public void OnlyStandardJsonWhitespaceIsInsignificant()
    {
        string[] documents =
        [
            "{\f\"a\":1}",
            "[1\v]",
            "{\u00a0}",
            "{\"a\"\v:1}",
            "[1\u0085]",
            "[1\u3000]" // ideographic space
        ];

        foreach (var document in documents)
        {
            Assert.ThrowsAny<JsonException>(
                () => JsonDocument.Parse(document)); // the oracle must agree these are malformed
            Assert.Throws<ExpertJsonStreamException>(() => ParseAll(document));
        }
    }

    [Fact]
    public void EscapedControlCharactersInsideStringsAreAccepted()
    {
        const string json = """{"a":"x\u0000y"}""";
        using var reference = JsonDocument.Parse(json);
        var events = ParseAll(json);

        var chunk = Assert.Single(events.OfType<ExpertJsonStringChunkEvent>());
        Assert.Equal(reference.RootElement.GetProperty("a").GetString(), chunk.Value);
    }

    [Fact]
    public void StandardJsonWhitespaceRemainsInsignificant()
    {
        var events = ParseAll("[\t1,\r\n2, 3\u0020]");

        Assert.Equal(
            ["1", "2", "3"],
            [.. events.OfType<ExpertJsonNumberValueEvent>().Select(number => number.RawValue)]);
    }

    [Fact]
    public void EmptyAndWhitespacePropertyNamesAreAcceptedLikeSystemTextJson()
    {
        const string json = """{"":1," ":2}""";

        using var reference = JsonDocument.Parse(json); // valid JSON: names may be empty/blank
        var events = ParseAll(json);

        Assert.Equal(
            [string.Empty, " "],
            [.. events.OfType<ExpertJsonPropertyNameEvent>().Select(name => name.Name)]);
        Assert.Equal(2, reference.RootElement.GetPropertyCount());
    }

    [Theory]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":{},"b":2,}""")]
    [InlineData("""{"a":{"b":1,}}""")]
    [InlineData("[1,]")]
    [InlineData("""[{"a":1},]""")]
    public void TrailingCommasAreRejectedLikeSystemTextJson(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json)); // the oracle rejects them too

        Assert.Throws<ExpertJsonStreamException>(() => ParseAll(json));
    }
}
