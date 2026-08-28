using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Property-based parity checks: for randomly generated JSON documents (object/array roots) the
/// incremental stream parser must agree with System.Text.Json on acceptance, and replaying its
/// event stream must reconstruct a deep-equal document. For randomly mutated documents the
/// accept/reject verdicts must match the System.Text.Json oracle exactly.
/// </summary>
public sealed class ExpertJsonStreamParserFuzzTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RandomDocumentsAreAcceptedAndReplayToDeepEqualNodes(int seed)
    {
        var random = new Random(seed);
        var compared = 0;
        for (var i = 0; i < 50; i++)
        {
            var json = JsonFuzzDocument.Generate(random);
            if (!TryParseNode(json, out var reference))
                continue; // the oracle rejects the sample (e.g. strict surrogate policy); skip it

            var parserAccepted = TryStreamParse(json, random, out var replayed, out var failure);
            Assert.True(parserAccepted, $"The parser rejected a valid document: {failure?.Message} {json}");
            Assert.True(
                JsonNode.DeepEquals(reference, replayed),
                $"Replayed document differs from the reference: {json}");

            var whole = Replay(ParseWhole(json));
            Assert.True(JsonNode.DeepEquals(reference, whole), json);
            compared++;
        }

        Assert.True(compared > 10, "The fuzz sample was almost entirely skipped; tighten the generator.");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void MutatedDocumentsAgreeWithSystemTextJsonOnAcceptance(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 80; i++)
        {
            var mutated = Mutate(random, JsonFuzzDocument.Generate(random));
            var oracleAccepts = TryParseNode(mutated, out var reference);
            var parserAccepts = TryStreamParse(mutated, random, out var replayed, out var failure);

            Assert.True(
                oracleAccepts == parserAccepts,
                $"Divergent verdicts (oracle={oracleAccepts}, parser={parserAccepts}): {failure?.Message} {mutated}");
            if (oracleAccepts)
                Assert.True(JsonNode.DeepEquals(reference, replayed), mutated);
        }
    }

    private static bool TryParseNode(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json);
            // Force value materialization: System.Text.Json parses documents containing unpaired
            // escaped surrogates but throws when the string values are read, which counts as
            // rejection for parity purposes.
            return JsonNode.DeepEquals(node, JsonNode.Parse(json));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            node = null;
            return false;
        }
    }

    private static bool TryStreamParse(string json, Random random, out JsonNode? replayed, out Exception? failure)
    {
        try
        {
            replayed = Replay(FeedInRandomChunks(json, random));
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is ExpertJsonStreamException or InvalidOperationException or ArgumentException)
        {
            replayed = null;
            failure = exception;
            return false;
        }
    }

    private static List<ExpertJsonStreamEvent> ParseWhole(string json)
    {
        var parser = new ExpertJsonStreamParser();
        var events = new List<ExpertJsonStreamEvent>(parser.Feed(json.AsSpan()));
        events.AddRange(parser.Complete());
        return events;
    }

    private static List<ExpertJsonStreamEvent> FeedInRandomChunks(string json, Random random)
    {
        var parser = new ExpertJsonStreamParser();
        var events = new List<ExpertJsonStreamEvent>();
        for (var index = 0; index < json.Length;)
        {
            var length = Math.Min(random.Next(1, 6), json.Length - index);
            events.AddRange(parser.Feed(json.AsSpan(index, length)));
            index += length;
        }

        events.AddRange(parser.Complete());
        return events;
    }

    private static string Mutate(Random random, string json)
    {
        var mutations = random.Next(1, 4);
        for (var i = 0; i < mutations; i++)
        {
            var index = random.Next(json.Length);
            var character = RandomMutationCharacter(random).ToString();
            json = random.Next(3) switch
            {
                0 => json.Insert(index, character),
                1 => json.Remove(index, 1),
                _ => string.Concat(json.AsSpan(0, index), character, json.AsSpan(index + 1))
            };
        }

        return json;
    }

    private static char RandomMutationCharacter(Random random) => random.Next(16) switch
    {
        0 => ' ',
        1 => '\t',
        2 => '\n',
        3 => '\r',
        4 => '"',
        5 => '{',
        6 => '}',
        7 => '[',
        8 => ']',
        9 => ':',
        10 => ',',
        11 => '\f',
        12 => '\u00a0',
        13 => '\\',
        14 => 'u',
        _ => (char)random.Next(0x20, 0x7f)
    };

    private sealed record ReplayOutcome(Stack<ReplayFrame> Frames, JsonNode? Root);

    private static JsonNode? Replay(List<ExpertJsonStreamEvent> events)
    {
        var outcome = new ReplayOutcome(new Stack<ReplayFrame>(), null);
        foreach (var streamEvent in events)
            switch (streamEvent)
            {
                case ExpertJsonObjectStartedEvent:
                    outcome.Frames.Push(new ObjectReplayFrame());
                    break;
                case ExpertJsonArrayStartedEvent:
                    outcome.Frames.Push(new ArrayReplayFrame());
                    break;
                case ExpertJsonPropertyNameEvent name:
                    ((ObjectReplayFrame)outcome.Frames.Peek()).PendingName = name.Name;
                    break;
                case ExpertJsonStringStartedEvent:
                    outcome.Frames.Push(new StringReplayFrame());
                    break;
                case ExpertJsonStringChunkEvent chunk:
                    ((StringReplayFrame)outcome.Frames.Peek()).Builder.Append(chunk.Value);
                    break;
                case ExpertJsonStringCompletedEvent:
                    var stringFrame = (StringReplayFrame)outcome.Frames.Pop();
                    outcome = Attach(outcome, JsonValue.Create(stringFrame.Builder.ToString()));
                    break;
                case ExpertJsonObjectCompletedEvent:
                    var objectFrame = (ObjectReplayFrame)outcome.Frames.Pop();
                    outcome = Attach(outcome, objectFrame.Value);
                    break;
                case ExpertJsonArrayCompletedEvent:
                    var arrayFrame = (ArrayReplayFrame)outcome.Frames.Pop();
                    outcome = Attach(outcome, new JsonArray([.. arrayFrame.Values]));
                    break;
                case ExpertJsonNumberValueEvent number:
                    outcome = Attach(outcome, JsonNode.Parse(number.RawValue));
                    break;
                case ExpertJsonBooleanValueEvent boolean:
                    outcome = Attach(outcome, JsonValue.Create(boolean.Value));
                    break;
                case ExpertJsonNullValueEvent:
                    outcome = Attach(outcome, null);
                    break;
                default:
                    throw new InvalidOperationException(streamEvent.GetType().FullName);
            }

        Assert.Empty(outcome.Frames);
        return outcome.Root;
    }

    private static ReplayOutcome Attach(ReplayOutcome outcome, JsonNode? node)
    {
        if (outcome.Frames.Count == 0)
            return outcome with { Root = node };
        outcome.Frames.Peek().Accept(node);
        return outcome;
    }

    private abstract class ReplayFrame
    {
        public abstract void Accept(JsonNode? node);
    }

    private sealed class ObjectReplayFrame : ReplayFrame
    {
        public JsonObject Value { get; } = [];

        public string? PendingName { get; set; }

        public override void Accept(JsonNode? node) =>
            Value[PendingName ?? throw new InvalidOperationException("Value without a preceding property name.")] = node;
    }

    private sealed class ArrayReplayFrame : ReplayFrame
    {
        public List<JsonNode?> Values { get; } = [];

        public override void Accept(JsonNode? node) => Values.Add(node);
    }

    private sealed class StringReplayFrame : ReplayFrame
    {
        public StringBuilder Builder { get; } = new();

        public override void Accept(JsonNode? node) => throw new InvalidOperationException("Strings never nest.");
    }
}

internal static class JsonFuzzDocument
{
    private static readonly string[] NamePool = ["a", "bb", "ccc", "d/e", "x~y", "n_1", "poem", "q~0z"];

    public static string Generate(Random random)
    {
        var builder = new StringBuilder();
        AppendValue(random, builder, depth: 0, topLevel: true);
        return builder.ToString();
    }

    private static void AppendValue(Random random, StringBuilder builder, int depth, bool topLevel)
    {
        if (!topLevel && (depth >= 4 || random.Next(4) == 0))
        {
            AppendLeaf(random, builder);
            return;
        }

        if (topLevel || random.Next(2) == 0)
            AppendObject(random, builder, depth);
        else
            AppendArray(random, builder, depth);
    }

    private static void AppendObject(Random random, StringBuilder builder, int depth)
    {
        builder.Append(Whitespace(random)).Append('{');
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = random.Next(0, 5);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(Whitespace(random));
            string name;
            do
            {
                name = NamePool[random.Next(NamePool.Length)];
            }
            while (!names.Add(name));
            AppendJsonString(builder, name);
            builder.Append(Whitespace(random)).Append(':').Append(Whitespace(random));
            AppendValue(random, builder, depth + 1, topLevel: false);
            builder.Append(Whitespace(random));
        }

        builder.Append(Whitespace(random)).Append('}');
    }

    private static void AppendArray(Random random, StringBuilder builder, int depth)
    {
        builder.Append(Whitespace(random)).Append('[');
        var count = random.Next(0, 5);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(Whitespace(random));
            AppendValue(random, builder, depth + 1, topLevel: false);
            builder.Append(Whitespace(random));
        }

        builder.Append(Whitespace(random)).Append(']');
    }

    private static void AppendLeaf(Random random, StringBuilder builder)
    {
        switch (random.Next(7))
        {
            case 0:
                builder.Append('"');
                AppendStringBody(random, builder);
                builder.Append('"');
                break;
            case 1:
                builder.Append(random.NextInt64(-1_000_000_000_000, 1_000_000_000_000)
                    .ToString(CultureInfo.InvariantCulture));
                break;
            case 2:
                builder.Append((random.NextDouble() * 2000 - 1000).ToString(CultureInfo.InvariantCulture));
                break;
            case 3:
                builder.Append(CultureInfo.InvariantCulture,
                    $"{random.Next(1, 99)}.{random.Next(0, 1000):000}e{(random.Next(2) == 0 ? "+" : "-")}{random.Next(0, 20)}");
                break;
            case 4:
                builder.Append(random.Next(2) == 0 ? "true" : "false");
                break;
            case 5:
                builder.Append(random.Next(2) == 0 ? "0" : "-0");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void AppendJsonString(StringBuilder builder, string name)
    {
        builder.Append('"');
        builder.Append(name);
        builder.Append('"');
    }

    /// <summary>Escapes a well-formed code point: either a non-surrogate unit or a complete
    /// high+low pair. Lone surrogates are avoided because JsonNode.DeepEquals cannot compare
    /// strings containing them.</summary>
    private static void AppendUnicodeEscape(Random random, StringBuilder builder)
    {
        if (random.Next(2) == 0)
        {
            builder.Append("\\u");
            builder.Append(random.Next(0xd800).ToString("x4", CultureInfo.InvariantCulture));
            return;
        }

        var high = random.Next(0xd800, 0xdc00);
        var low = random.Next(0xdc00, 0xe000);
        builder.Append("\\u").Append(high.ToString("x4", CultureInfo.InvariantCulture));
        builder.Append("\\u").Append(low.ToString("x4", CultureInfo.InvariantCulture));
    }

    private static void AppendStringBody(Random random, StringBuilder builder)
    {
        var length = random.Next(0, 12);
        for (var i = 0; i < length; i++)
        {
            switch (random.Next(9))
            {
                case 0:
                    builder.Append("\\\"");
                    break;
                case 1:
                    builder.Append(@"\\");
                    break;
                case 2:
                    builder.Append("\\/");
                    break;
                case 3:
                    builder.Append("\\n");
                    break;
                case 4:
                    builder.Append("\\t");
                    break;
                case 5:
                    AppendUnicodeEscape(random, builder);
                    break;
                case 6:
                    builder.Append((char)random.Next(0x20, 0x7f));
                    break;
                case 7:
                    builder.Append((char)random.Next(0xa1, 0x2fff));
                    break;
                default:
                    var printable = (char)random.Next(0x20, 0x7f);
                    if (printable is '"' or '\\')
                        printable = 's';
                    builder.Append(printable);
                    break;
            }
        }
    }

    private static string Whitespace(Random random) => random.Next(5) switch
    {
        0 => " ",
        1 => "\t",
        2 => "\r\n",
        3 => " \t ",
        _ => ""
    };
}
