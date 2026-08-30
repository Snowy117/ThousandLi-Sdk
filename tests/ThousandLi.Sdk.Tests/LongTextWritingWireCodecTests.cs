using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class LongTextWritingWireCodecTests
{
    private readonly LongTextWritingWireCodec _codec = LongTextWritingWireCodec.Default;

    private static JsonElement P(object value) => JsonSerializer.SerializeToElement(value);

    // codec 内部 writer 与测试期望原文的非 ASCII 转义策略不同（语义等价），
    // 断言前统一规范化为同一 raw text 表示。
    private static string TextOf(JsonElement element) =>
        JsonSerializer.SerializeToElement(element).GetRawText();

    private static string Norm(string json) => TextOf(TestSupport.Json(json));

    [Fact]
    public void EncodeInvocationRoundTripsAFullyConfiguredInvocation()
    {
        var bucket = NewBucket("主叙事历史", ("user", "我推门进去"), ("assistant", "门后是一条长廊。"));
        var packet = _codec.EncodeInvocation(
            worldSettings: "仙侠世界",
            playerInput: "我往长廊深处走。",
            playerPersona: "冷剑客",
            currentState: "走廊中段",
            stateSchema: "{\"kind\":\"object\",\"properties\":[]}",
            primaryOutput: new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask, "story"),
            features: [new TimeTagsFeature(null, null), new ActionOptionsFeature(3, static (_, _) => ValueTask.CompletedTask)],
            historyBuckets: [bucket]);

        Assert.Equal(
            Norm("""{"kind":"ref","bucketId":"b0","description":"主叙事历史"}"""),
            TextOf(packet.Input.GetProperty("historyBuckets")[0]));
        var registeredBucket = Assert.Single(packet.BucketRegistry);
        Assert.Equal(("b0", bucket), (registeredBucket.Key, registeredBucket.Value));

        var sink = new RecordingWireEventSink();
        var resolvedBuckets = new Dictionary<string, IHistoryBucket>(StringComparer.Ordinal) { ["b0"] = bucket };
        var config = _codec.DecodeInvocation(
            packet.Input, sink, bucketId => resolvedBuckets[bucketId]);

        Assert.Equal("仙侠世界", config.WorldSettings);
        Assert.Equal("我往长廊深处走。", config.PlayerInput);
        Assert.Equal("冷剑客", config.PlayerPersona);
        Assert.Equal("走廊中段", config.CurrentState);
        Assert.Equal("{\"kind\":\"object\",\"properties\":[]}", config.StateSchema);
        var textOutput = Assert.IsType<TextPrimaryOutput>(config.PrimaryOutput);
        Assert.Equal("story", textOutput.PropertyName);
        Assert.NotNull(textOutput.OnDelta);
        Assert.Equal(2, config.Features.Count);
        var timeTags = Assert.IsType<TimeTagsFeature>(config.Features[0]);
        Assert.NotNull(timeTags.OnStartTag);
        Assert.NotNull(timeTags.OnEndTag);
        var actionOptions = Assert.IsType<ActionOptionsFeature>(config.Features[1]);
        Assert.Equal(3, actionOptions.MaxCount);
        Assert.NotNull(actionOptions.OnOptionCompleted);
        var decodedBucket = Assert.Single(config.HistoryBuckets);
        Assert.Equal("主叙事历史", decodedBucket.Description);
        Assert.Equal(2, decodedBucket.GetRawTurns().Count);
    }

    [Fact]
    public void EncodeInvocationWithInlineBucketsProjectsTurnsAndKeepsTheRegistryEmpty()
    {
        var bucket = NewBucket(
            "支线A",
            metadata: new Dictionary<string, string> { ["afterThinking"] = "计划" },
            digest: "支线A的开局摘要",
            ("user", "查看地图"));
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            historyBuckets: [bucket],
            inlineHistoryBuckets: true);

        Assert.Empty(packet.BucketRegistry);
        var wireBucket = packet.Input.GetProperty("historyBuckets")[0];
        Assert.Equal("inline", wireBucket.GetProperty("kind").GetString());
        Assert.Equal("支线A", wireBucket.GetProperty("description").GetString());
        var wireTurn = wireBucket.GetProperty("turns")[0];
        Assert.Equal(
            Norm("""{"role":"user","content":"查看地图"}"""),
            TextOf(wireTurn.GetProperty("messages")[0]));
        Assert.Equal("支线A的开局摘要", wireTurn.GetProperty("digest").GetString());
        Assert.Equal(0, wireTurn.GetProperty("turnOrdinal").GetInt64());
        Assert.Equal("计划", wireTurn.GetProperty("metadata").GetProperty("afterThinking").GetString());

        var config = _codec.DecodeInvocation(packet.Input, new RecordingWireEventSink(), static _ => throw new InvalidOperationException("inline buckets must not resolve refs."));
        var decoded = Assert.Single(config.HistoryBuckets);
        Assert.Equal("支线A", decoded.Description);
        var turn = Assert.Single(decoded.GetRawTurns());
        Assert.Equal("支线A的开局摘要", turn.Digest);
        Assert.Equal("计划", turn.Metadata!["afterThinking"]);
        Assert.Throws<NotSupportedException>(() => decoded.AddMessages(null, null, ChatMessage.User("x")));
    }

    [Fact]
    public void DecodeInvocationDefaultsToATextPrimaryOutputWhenThePacketOmitsIt()
    {
        var packet = _codec.EncodeInvocation("世界", "输入");

        Assert.False(packet.Input.TryGetProperty("primaryOutput", out _));
        var config = _codec.DecodeInvocation(packet.Input, new RecordingWireEventSink(), static _ => throw new InvalidOperationException("no buckets"));

        var textOutput = Assert.IsType<TextPrimaryOutput>(config.PrimaryOutput);
        Assert.Equal("narrative", textOutput.PropertyName);
        Assert.NotNull(textOutput.OnDelta);
        Assert.Empty(config.Features);
        Assert.Empty(config.HistoryBuckets);
    }

    [Fact]
    public void EncodeInvocationRejectsFeatureTypesWithoutARegisteredCodec()
    {
        Assert.Throws<NotSupportedException>(() => _codec.EncodeInvocation(
            "世界",
            "输入",
            features: [new UnsupportedFeature()]));
    }

    [Fact]
    public async Task DecodedCallbacksEmitWireEventsThroughTheSink()
    {
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            primaryOutput: new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask),
            features: [new ActionOptionsFeature(2, static (_, _) => ValueTask.CompletedTask)]);
        var sink = new RecordingWireEventSink();
        var config = _codec.DecodeInvocation(packet.Input, sink, static _ => throw new InvalidOperationException("no buckets"));

        var textOutput = (TextPrimaryOutput)config.PrimaryOutput;
        await textOutput.OnDelta(new TextDeltaEvent("你"), CancellationToken.None);
        await textOutput.OnDelta(new TextDeltaEvent("好"), CancellationToken.None);
        var actionOptions = (ActionOptionsFeature)config.Features[0];
        await actionOptions.OnOptionCompleted(new ActionOptionCompletedEvent(0, "进攻"), CancellationToken.None);
        if (actionOptions.OnArrayStarted is not null)
            await actionOptions.OnArrayStarted(CancellationToken.None);

        Assert.Equal(
            [
                ("chunk", TextOf(P(new { text = "你" }))),
                ("chunk", TextOf(P(new { text = "好" }))),
                ("actionOption", TextOf(P(new { index = 0, value = "进攻" }))),
                ("actionOption", TextOf(P(new { arrayEvent = "started" }))),
            ],
            [.. sink.Events.Select(static sent => (sent.EventType, Payload: TextOf(sent.Payload)))]);
    }

    [Fact]
    public async Task DispatchRoutesChunkTimetagAndActionOptionPayloadsToGameCallbacks()
    {
        var deltas = new List<TextDeltaEvent>();
        var startTags = new List<TimeTagDeltaEvent>();
        var endTags = new List<TimeTagDeltaEvent>();
        var options = new List<ActionOptionCompletedEvent>();
        var arrayStarted = 0;
        var arrayCompleted = 0;
        var primaryOutput = new TextPrimaryOutput((evt, _) => RecordEvent(deltas, evt));
        var features = new ILongTextWritingFeature[]
        {
            new TimeTagsFeature((evt, _) => RecordEvent(startTags, evt), (evt, _) => RecordEvent(endTags, evt)),
            new ActionOptionsFeature(
                3,
                (evt, _) => RecordEvent(options, evt),
                _ => CountCall(ref arrayStarted),
                _ => CountCall(ref arrayCompleted)),
        };

        await _codec.DispatchEventAsync(primaryOutput, features, "chunk", P(new { text = "夜色" }), CancellationToken.None);
        await _codec.DispatchEventAsync(primaryOutput, features, "timetag", P(new { delta = "晨", isStart = true }), CancellationToken.None);
        await _codec.DispatchEventAsync(primaryOutput, features, "timetag", P(new { delta = "暮", isStart = false }), CancellationToken.None);
        await _codec.DispatchEventAsync(primaryOutput, features, "actionOption", P(new { index = 1, value = "逃跑" }), CancellationToken.None);
        await _codec.DispatchEventAsync(primaryOutput, features, "actionOption", P(new { arrayEvent = "started" }), CancellationToken.None);
        await _codec.DispatchEventAsync(primaryOutput, features, "actionOption", P(new { arrayEvent = "completed" }), CancellationToken.None);

        Assert.Equal(new TextDeltaEvent("夜色"), Assert.Single(deltas));
        var startedTag = Assert.Single(startTags);
        Assert.Equal(new TimeTagDeltaEvent("晨", true), startedTag);
        Assert.Equal(new TimeTagDeltaEvent("暮", false), Assert.Single(endTags));
        Assert.Equal(new ActionOptionCompletedEvent(1, "逃跑"), Assert.Single(options));
        Assert.Equal((1, 1), (arrayStarted, arrayCompleted));

        return;

        static ValueTask RecordEvent<T>(List<T> sink, T evt)
        {
            sink.Add(evt);
            return ValueTask.CompletedTask;
        }

        static ValueTask CountCall(ref int counter)
        {
            counter++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task VariableUpdateRoundTripsThroughEncodeReadAndDispatch()
    {
        var proposals = new List<VariableUpdatePatchProposal>();
        var gameFeature = new VariableUpdateFeature((proposal, _) =>
        {
            proposals.Add(proposal);
            return ValueTask.CompletedTask;
        });
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            currentState: "{\"courage\":10}",
            stateSchema: "{\"kind\":\"object\",\"properties\":[]}",
            features: [gameFeature]);

        Assert.Equal(
            Norm("""{"kind":"variableUpdate"}"""),
            TextOf(packet.Input.GetProperty("features")[0]));

        var sink = new RecordingWireEventSink();
        var config = _codec.DecodeInvocation(packet.Input, sink, static _ => throw new InvalidOperationException("no buckets"));
        var platformFeature = Assert.IsType<VariableUpdateFeature>(Assert.Single(config.Features));
        await platformFeature.OnPatchProposed(
            new VariableUpdatePatchProposal(
            [
                new VariableUpdateDelta("/courage", 2),
                new VariableUpdateReplace("/trust", JsonValue.Create(80)),
                new VariableUpdateInsert("/notes/1", JsonValue.Create("新记录")),
                new VariableUpdateRemove("/flags/open"),
            ]),
            CancellationToken.None);

        var (sentEventType, sentPayload) = sink.Events.Single();
        Assert.Equal("variableUpdate", sentEventType);
        Assert.Equal(4, sentPayload.GetProperty("operations").GetArrayLength());

        await _codec.DispatchEventAsync(null, [gameFeature], "variableUpdate", sentPayload, CancellationToken.None);

        var operations = Assert.Single(proposals).Operations;
        Assert.Equal(4, operations.Count);
        var delta = Assert.IsType<VariableUpdateDelta>(operations[0]);
        Assert.Equal("/courage", delta.Path);
        Assert.Equal(2m, delta.Value);
        var replace = Assert.IsType<VariableUpdateReplace>(operations[1]);
        Assert.Equal(80, replace.Value!.GetValue<int>());
        var insert = Assert.IsType<VariableUpdateInsert>(operations[2]);
        Assert.Equal("新记录", insert.Value!.GetValue<string>());
        Assert.IsType<VariableUpdateRemove>(operations[3]);
    }

    private static readonly (string PayloadJson, JsonStreamEvent Expected)[] JsonStreamPayloads =
    [
        ("""{"kind":"objectStarted","path":"/0"}""", new JsonStreamObjectStartedEvent("/0")),
        ("""{"kind":"objectCompleted","path":"/0"}""", new JsonStreamObjectCompletedEvent("/0")),
        ("""{"kind":"arrayStarted","path":""}""", new JsonStreamArrayStartedEvent("")),
        ("""{"kind":"arrayCompleted","path":""}""", new JsonStreamArrayCompletedEvent("")),
        ("""{"kind":"propertyName","path":"/0","name":"speaker"}""", new JsonStreamPropertyNameEvent("/0", "speaker")),
        ("""{"kind":"stringStarted","path":"/0/speaker"}""", new JsonStreamStringStartedEvent("/0/speaker")),
        ("""{"kind":"stringChunk","path":"/0/speaker","value":"阿雪"}""", new JsonStreamStringChunkEvent("/0/speaker", "阿雪")),
        ("""{"kind":"stringCompleted","path":"/0/speaker"}""", new JsonStreamStringCompletedEvent("/0/speaker")),
        ("""{"kind":"numberValue","path":"/turn","rawValue":"3.25"}""", new JsonStreamNumberValueEvent("/turn", "3.25")),
        ("""{"kind":"booleanValue","path":"/alive","value":true}""", new JsonStreamBooleanValueEvent("/alive", true)),
        ("""{"kind":"nullValue","path":"/note"}""", new JsonStreamNullValueEvent("/note")),
    ];

    [Fact]
    public async Task JsonStreamWireRoundTripsEveryEventShapeThroughTheCodec()
    {
        foreach (var (payloadJson, expected) in JsonStreamPayloads)
        {
            var platformSinks = new RecordingWireEventSink();
            var packet = _codec.EncodeInvocation(
                "世界",
                "输入",
                primaryOutput: new JsonPrimaryOutput(
                    "dialogues",
                    AiJsonSchema.Array(AiJsonSchema.String()),
                    static (_, _) => ValueTask.CompletedTask));
            var config = _codec.DecodeInvocation(packet.Input, platformSinks, static _ => throw new InvalidOperationException("no buckets"));
            var platformOutput = (JsonPrimaryOutput)config.PrimaryOutput;

            await platformOutput.OnJsonEvent(new PrimaryJsonStreamEvent(expected), CancellationToken.None);
            var wirePayload = Assert.Single(platformSinks.Events, static sent => sent.EventType == "jsonStream").Payload;

            var received = new List<PrimaryJsonStreamEvent>();
            var gameOutput = new JsonPrimaryOutput(
                "dialogues",
                AiJsonSchema.Array(AiJsonSchema.String()),
                (evt, _) => Capture(received, evt));
            await _codec.DispatchEventAsync(
                gameOutput, [], "jsonStream", TestSupport.Json(payloadJson), CancellationToken.None);

            Assert.Equal(expected, Assert.Single(received).Event);
            Assert.Equal(Norm(payloadJson), TextOf(wirePayload));
        }

        return;

        static ValueTask Capture(List<PrimaryJsonStreamEvent> sink, PrimaryJsonStreamEvent evt)
        {
            sink.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task CompletionAggregatesTextPrimaryMetadataAndReasoning()
    {
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            primaryOutput: new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask, "story"));
        var sink = new RecordingWireEventSink();
        var config = _codec.DecodeInvocation(packet.Input, sink, static _ => throw new InvalidOperationException("no buckets"));
        var platformOutput = (TextPrimaryOutput)config.PrimaryOutput;
        await platformOutput.OnDelta(new TextDeltaEvent("长廊"), CancellationToken.None);
        await platformOutput.OnDelta(new TextDeltaEvent("尽头"), CancellationToken.None);

        var output = _codec.EncodeCompletion(
            new ExpertCompletionResult(new Dictionary<string, string> { ["afterThinking"] = "伏笔" }, "先写环境"),
            config);

        Assert.Equal("长廊尽头", output.GetProperty("primary").GetString());
        Assert.Equal("伏笔", output.GetProperty("metadata").GetProperty("afterThinking").GetString());
        Assert.Equal("先写环境", output.GetProperty("reasoning").GetString());

        var completed = new List<TextCompletedEvent>();
        var gameOutput = new TextPrimaryOutput(
            static (_, _) => ValueTask.CompletedTask,
            "story",
            (evt, _) => Capture(completed, evt));
        var result = await LongTextWritingWireCodec.DecodeCompletionAsync(output, gameOutput, CancellationToken.None);

        Assert.Equal(new TextCompletedEvent("长廊尽头"), Assert.Single(completed));
        Assert.Equal("伏笔", result.Metadata!["afterThinking"]);
        Assert.Equal("先写环境", result.Reasoning);

        return;

        static ValueTask Capture(List<TextCompletedEvent> sink, TextCompletedEvent evt)
        {
            sink.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task CompletionRebuildsTheJsonPrimaryFromTheEventSequenceWithoutGameSideDelivery()
    {
        var schema = AiJsonSchema.Array(
            AiJsonSchema.Object(
                AiJsonSchema.Required("speaker", AiJsonSchema.String()),
                AiJsonSchema.Required("text", AiJsonSchema.String())));
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            primaryOutput: new JsonPrimaryOutput("dialogues", schema, static (_, _) => ValueTask.CompletedTask));
        var sink = new RecordingWireEventSink();
        var config = _codec.DecodeInvocation(packet.Input, sink, static _ => throw new InvalidOperationException("no buckets"));
        var platformOutput = (JsonPrimaryOutput)config.PrimaryOutput;

        JsonStreamEvent[] events =
        [
            JsonStreamEvent.ArrayStarted(""),
            JsonStreamEvent.ObjectStarted("/0"),
            JsonStreamEvent.PropertyName("/0", "speaker"),
            JsonStreamEvent.StringStarted("/0/speaker"),
            JsonStreamEvent.StringChunk("/0/speaker", "阿雪"),
            JsonStreamEvent.StringCompleted("/0/speaker"),
            JsonStreamEvent.PropertyName("/0", "text"),
            JsonStreamEvent.StringStarted("/0/text"),
            JsonStreamEvent.StringChunk("/0/text", "你来了。"),
            JsonStreamEvent.StringCompleted("/0/text"),
            JsonStreamEvent.ObjectCompleted("/0"),
            JsonStreamEvent.ArrayCompleted(""),
        ];
        foreach (var evt in events)
        {
            await platformOutput.OnJsonEvent(new PrimaryJsonStreamEvent(evt), CancellationToken.None);
        }

        var output = _codec.EncodeCompletion(new ExpertCompletionResult(), config);
        Assert.Equal(
            Norm("""[{"speaker":"阿雪","text":"你来了。"}]"""),
            TextOf(output.GetProperty("primary")));

        var jsonDeliveries = 0;
        var gameOutput = new JsonPrimaryOutput(
            "dialogues", schema, (_, _) => Capture(ref jsonDeliveries));
        await LongTextWritingWireCodec.DecodeCompletionAsync(output, gameOutput, CancellationToken.None);
        Assert.Equal(0, jsonDeliveries);

        return;

        static ValueTask Capture(ref int counter)
        {
            counter++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void BuildInputSchemaDerivesTheSameTopLevelShapeAndRequiredSet()
    {
        var schema = _codec.BuildInputSchema();

        Assert.Equal(
            ["currentState", "features", "historyBuckets", "playerInput", "playerPersona", "primaryOutput", "stateSchema", "worldSettings"],
            schema.GetProperty("properties").EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["worldSettings", "playerInput"],
            schema.GetProperty("required").EnumerateArray().Select(static value => value.GetString()));
        Assert.Equal(
            ["actionOptions", "timeTags", "variableUpdate"],
            schema.GetProperty("properties").GetProperty("features").GetProperty("items")
                .GetProperty("anyOf").EnumerateArray()
                .Select(static variant => variant.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString()));
        Assert.Equal(
            ["json", "text"],
            schema.GetProperty("properties").GetProperty("primaryOutput").GetProperty("anyOf").EnumerateArray()
                .Select(static variant => variant.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString()));
        Assert.Equal(
            ["actionOption", "chunk", "jsonStream", "timetag", "variableUpdate"],
            _codec.EventTypes);
    }

    [Fact]
    public async Task FakeFeatureCodecExtendsTheRegistryEndToEndWithoutTouchingTheCore()
    {
        var codec = LongTextWritingWireCodec.Default.WithFeatureCodec(new EmotionTagsFeatureWireCodec());

        var gameTags = new List<EmotionTagDeltaEvent>();
        var gameFeature = new EmotionTagsFeature((evt, _) => Capture(gameTags, evt));
        var packet = codec.EncodeInvocation(
            "世界",
            "输入",
            features: [gameFeature]);

        Assert.Equal(
            Norm("""{"kind":"emotionTags"}"""),
            TextOf(packet.Input.GetProperty("features")[0]));
        Assert.Contains("emotionTag", codec.EventTypes);
        Assert.DoesNotContain("emotionTag", LongTextWritingWireCodec.Default.EventTypes);

        var sink = new RecordingWireEventSink();
        var config = codec.DecodeInvocation(packet.Input, sink, static _ => throw new InvalidOperationException("no buckets"));
        var platformFeature = Assert.IsType<EmotionTagsFeature>(Assert.Single(config.Features));
        await platformFeature.OnTag(new EmotionTagDeltaEvent("紧张"), CancellationToken.None);
        var (sentEventType, sentPayload) = sink.Events
            .Select(static sent => (sent.EventType, Payload: TextOf(sent.Payload)))
            .Single();
        Assert.Equal("emotionTag", sentEventType);
        Assert.Equal(TextOf(P(new { tag = "紧张" })), sentPayload);

        await codec.DispatchEventAsync(null, [gameFeature], "emotionTag", P(new { tag = "雀跃" }), CancellationToken.None);
        Assert.Equal(new EmotionTagDeltaEvent("雀跃"), Assert.Single(gameTags));

        var schema = codec.BuildInputSchema();
        Assert.Contains(
            "emotionTags",
            schema.GetProperty("properties").GetProperty("features").GetProperty("items")
                .GetProperty("anyOf").EnumerateArray()
                .Select(static variant => variant.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString()));

        return;

        static ValueTask Capture(List<EmotionTagDeltaEvent> sink, EmotionTagDeltaEvent evt)
        {
            sink.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void ValidateReportsUnknownKindsAndMissingRequiredScalarsWithStableCodes()
    {
        Assert.Empty(_codec.Validate(TestSupport.Json("""
            {"worldSettings":"w","playerInput":"p","primaryOutput":{"kind":"text"},
             "features":[{"kind":"timeTags"}],"historyBuckets":[{"kind":"ref","bucketId":"b0"}]}
            """)));

        var errors = _codec.Validate(TestSupport.Json(
            """{"playerInput":"p","primaryOutput":{"kind":"voice"},"features":[{"kind":"emotionTags"}],"historyBuckets":[{"kind":"lazy"}]}"""));
        Assert.Equal(
            ["worldSettings/missing", "primaryOutput/unknownKind", "features[0]/unknownKind", "historyBuckets[0]/unknownKind"],
            errors.Select(static error => error.Code));

        var notObject = _codec.Validate(TestSupport.Json("[]"));
        Assert.Equal(["input/notObject"], notObject.Select(static error => error.Code));

        var notString = _codec.Validate(TestSupport.Json("""{"worldSettings":1,"playerInput":"p"}"""));
        Assert.Equal(["worldSettings/notString"], notString.Select(static error => error.Code));
    }

    [Fact]
    public void ApplyReplaysTheDecodedConfigurationOntoAFreshExpert()
    {
        var bucket = NewBucket("主叙事历史", ("user", "开局"));
        var packet = _codec.EncodeInvocation(
            "世界",
            "输入",
            playerPersona: "剑客",
            currentState: "第一章",
            stateSchema: "{}",
            primaryOutput: new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask),
            features: [new TimeTagsFeature(null, null)],
            historyBuckets: [bucket]);
        var config = _codec.DecodeInvocation(packet.Input, new RecordingWireEventSink(), _ => bucket);

        var expert = new TestLongTextWritingExpert();
        LongTextWritingWireCodec.Apply(expert, config);

        Assert.Equal("世界", expert.ExposedWorldSettings);
        Assert.Equal("输入", expert.ExposedPlayerInput);
        Assert.Equal("剑客", expert.ExposedPlayerPersona);
        Assert.Equal("第一章", expert.ExposedCurrentState);
        Assert.Equal("{}", expert.ExposedStateSchema);
        Assert.IsType<TextPrimaryOutput>(expert.ExposedPrimaryOutput);
        Assert.IsType<TimeTagsFeature>(Assert.Single(expert.ExposedFeatures));
        var replayedBucket = Assert.Single(expert.ExposedHistoryBuckets);
        Assert.Equal("主叙事历史", replayedBucket.Description);
    }

    private static StubHistoryBucket NewBucket(
        string description,
        params (string Role, string Content)[] messages)
    {
        var bucket = new StubHistoryBucket(description);
        foreach (var (role, content) in messages)
        {
            bucket.AddMessages(null, null, role switch
            {
                "user" => ChatMessage.User(content),
                "assistant" => ChatMessage.Assistant(content),
                _ => ChatMessage.System(content),
            });
        }

        return bucket;
    }

    private static StubHistoryBucket NewBucket(
        string description,
        IReadOnlyDictionary<string, string> metadata,
        string digest,
        params (string Role, string Content)[] messages)
    {
        var bucket = new StubHistoryBucket(description);
        bucket.AddMessages(digest, metadata, [.. messages.Select(static message => ChatMessage.User(message.Content))]);
        return bucket;
    }

    private sealed class StubHistoryBucket(string description) : IHistoryBucket
    {
        private readonly List<HistoryTurn> _turns = [];

        public string Description { get; } = description;

        public void AddMessages(
            string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages)
        {
            _turns.Add(new HistoryTurn(messages, digest, _turns.Count, metadata));
        }

        public IReadOnlyList<HistoryTurn> GetRawTurns() => _turns;

        public IReadOnlyList<HistoryProjectionEntry> GetCompressedView(CompressedViewOptions? options = null)
        {
            _ = options;
            return [.. _turns.Select(static turn => new HistoryProjectionRawTurn(turn))];
        }
    }

    private sealed class RecordingWireEventSink : IWireEventSink
    {
        private readonly List<(string EventType, JsonElement Payload)> _events = [];

        public IReadOnlyList<(string EventType, JsonElement Payload)> Events => _events;

        public ValueTask SendAsync(string eventType, JsonElement payload, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            _events.Add((eventType, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsupportedFeature : ILongTextWritingFeature;

    private sealed record EmotionTagDeltaEvent(string Tag) : ExpertFeatureEvent;

    private sealed class EmotionTagsFeature(Func<EmotionTagDeltaEvent, CancellationToken, ValueTask> onTag)
        : ILongTextWritingFeature
    {
        public Func<EmotionTagDeltaEvent, CancellationToken, ValueTask> OnTag { get; } = onTag;
    }

    private sealed class EmotionTagsFeatureWireCodec : LongTextWritingFeatureWireCodec<EmotionTagsFeature>
    {
        public override string Kind => "emotionTags";

        public override IReadOnlyList<string> EventTypes => ["emotionTag"];

        public override JsonObject ParameterSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kind) },
            },
            ["required"] = new JsonArray("kind"),
        };

        protected override void Write(EmotionTagsFeature feature, Utf8JsonWriter writer)
        {
            _ = feature;
            _ = writer;
        }

        protected override EmotionTagsFeature Read(JsonElement wireFeature, IWireEventSink sink)
        {
            _ = wireFeature;
            return new EmotionTagsFeature(
                (evt, cancellationToken) => sink.SendAsync(
                    "emotionTag", JsonSerializer.SerializeToElement(new { tag = evt.Tag }), cancellationToken));
        }

        public override ValueTask DispatchAsync(
            EmotionTagsFeature feature, string eventType, JsonElement payload, CancellationToken cancellationToken)
        {
            _ = eventType;
            _ = cancellationToken;
            return feature.OnTag(new EmotionTagDeltaEvent(payload.GetProperty("tag").GetString()!), CancellationToken.None);
        }
    }

    private sealed class TestLongTextWritingExpert : AbstractLongTextWritingExpert
    {
        public string? ExposedWorldSettings => WorldSettings;

        public string? ExposedPlayerInput => PlayerInput;

        public string? ExposedPlayerPersona => PlayerPersona;

        public string? ExposedCurrentState => CurrentState;

        public string? ExposedStateSchema => StateSchema;

        public IExpertPrimaryOutput? ExposedPrimaryOutput => ConfiguredPrimaryOutput;

        public IReadOnlyList<ILongTextWritingFeature> ExposedFeatures => ConfiguredFeatures;

        public IReadOnlyList<IHistoryBucket> ExposedHistoryBuckets => ConfiguredHistoryBuckets;

        protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test expert never executes.");

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test expert never executes.");
    }
}
