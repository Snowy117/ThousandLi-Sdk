using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.GameHelper;
using ThousandLi.RemoteExperts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// End-to-end proxy tests over a fake platform HTTP transport: fluent configuration is encoded on
/// the POST input, SSE event frames drive the game's real callbacks through the category codec,
/// dataRequest frames are served from the local bucket registry, and the completion frame produces
/// the <see cref="ExpertCompletionResult" /> artifact.
/// </summary>
public sealed class RemoteLongTextWritingExpertTests
{
    private const string ContractId = "tests/long-text";
    private const string PackageId = "tests/remote-expert@1";
    private const string InvocationId = "0199feed-aaaa-7bbb-8ccc-ddddddddeeee";

    private static RemoteInvocationRunnerOptions Options() => new(
        new Dictionary<string, string> { [ContractId] = PackageId });

    private static RemoteInvocationRunnerOptions OfficialOptions() => new(
        new Dictionary<string, string> { [AbstractLongTextWritingExpert.ContractId] = PackageId });

    private static string OfficialCatalogJson() =>
        "[{\"contractId\":\"" + AbstractLongTextWritingExpert.ContractId +
        "\",\"name\":\"LongTextWriting\",\"description\":\"\"}]";

    private static RemoteLongTextWritingExpert CreateExpert(FakeRemoteHttpHandler handler)
    {
        var expert = new RemoteLongTextWritingExpert(
            new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
            Options(),
            ContractId);
        expert.Bind(new RemoteExpertExecutionContext());
        return expert;
    }

    private static string CatalogJson() =>
        "[{\"contractId\":\"" + ContractId + "\",\"name\":\"LongTextWriting\",\"description\":\"\"}]";

    private static string SnapshotJson() =>
        "{\"expertInvocationId\":\"" + InvocationId + "\",\"status\":\"running\",\"replayed\":false,\"contractId\":\"" +
        ContractId + "\",\"expertPackageId\":\"" + PackageId +
        "\",\"lastEventOrdinal\":-1,\"createdAt\":\"2026-08-30T10:00:00Z\"}";

    private static string EventSse(long ordinal, string eventType, string payloadJson) =>
        "id: " + ordinal + "\ndata: {\"type\":\"event\",\"eventType\":\"" + eventType + "\",\"payload\":" +
        payloadJson + "}\n\n";

    private static string DataRequestSse(long ordinal, string requestId, string bucketId, string view,
        long? cursor = null, int? limit = null)
    {
        var extra = (cursor is { } c ? ",\"cursor\":" + c : "") +
                    (limit is { } l ? ",\"limit\":" + l : "");
        return "id: " + ordinal + "\ndata: {\"type\":\"dataRequest\",\"requestId\":\"" + requestId +
               "\",\"bucketId\":\"" + bucketId + "\",\"view\":\"" + view + "\"" + extra + "}\n\n";
    }

    private static string CompletedSse(string outputJson) =>
        "data: {\"type\":\"completed\",\"output\":" + outputJson + "}\n\n";

    private static JsonElement ParseBody(CapturedRemoteRequest request)
    {
        Assert.NotNull(request.Body);
        return JsonDocument.Parse(request.Body).RootElement.Clone();
    }

    private static StubBucket NewBucket(string description, params string[] userMessages)
    {
        var bucket = new StubBucket(description);
        foreach (var message in userMessages)
            bucket.AddMessages(null, null, ChatMessage.User(message));
        return bucket;
    }

    [Fact]
    public async Task StreamAsyncEncodesFluentStateAndDrivesFeatureCallbacks()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "chunk", """{"text":"夜色"}""") +
            EventSse(1, "chunk", """{"text":"渐深"}""") +
            EventSse(2, "timetag", """{"delta":"【子时】","isStart":true}""") +
            EventSse(3, "actionOption", """{"arrayEvent":"started"}""") +
            EventSse(4, "actionOption", """{"index":0,"value":"拔剑"}""") +
            EventSse(5, "actionOption", """{"arrayEvent":"completed"}""") +
            CompletedSse("""{"primary":"夜色渐深","metadata":{"afterThinking":"伏笔"},"reasoning":"推理"}"""));

        var chunks = new List<string>();
        var completedTexts = new List<string>();
        var startTags = new List<string>();
        var optionsStarted = 0;
        var optionValues = new List<string>();
        var optionsCompleted = 0;
        var bucket = NewBucket("主叙事", "开局");
        var expert = CreateExpert(handler)
            .WithWorldSettings("仙侠世界")
            .WithPlayerInput("我走出客栈。")
            .WithPlayerPersona("剑客")
            .WithPrimaryOutput(new TextPrimaryOutput(
                (evt, _) => Capture(chunks, evt.Delta),
                "narrative",
                (evt, _) => Capture(completedTexts, evt.Text)))
            .WithFeatures(
                new TimeTagsFeature((evt, _) => Capture(startTags, evt.Delta), null),
                new ActionOptionsFeature(
                    3,
                    (evt, _) => Capture(optionValues, evt.Text),
                    _ => { optionsStarted++; return ValueTask.CompletedTask; },
                    _ => { optionsCompleted++; return ValueTask.CompletedTask; }))
            .WithHistoryBuckets(bucket);

        var result = await expert.StreamAsync(TestSupport.CancellationToken);

        // The start request carries the encoded invocation packet and the bound package id.
        var start = ParseBody(handler.CapturedRequests[1]);
        Assert.Equal(PackageId, start.GetProperty("expertPackageId").GetString());
        Assert.Equal(ContractId, start.GetProperty("contractId").GetString());
        var input = start.GetProperty("input");
        Assert.Equal("仙侠世界", input.GetProperty("worldSettings").GetString());
        Assert.Equal("我走出客栈。", input.GetProperty("playerInput").GetString());
        Assert.Equal("剑客", input.GetProperty("playerPersona").GetString());
        Assert.Equal("text", input.GetProperty("primaryOutput").GetProperty("kind").GetString());
        Assert.Equal(
            "narrative",
            input.GetProperty("primaryOutput").GetProperty("propertyName").GetString());
        Assert.Equal(
            ["timeTags", "actionOptions"],
            input.GetProperty("features").EnumerateArray()
                .Select(static feature => feature.GetProperty("kind").GetString()));
        var wireBucket = input.GetProperty("historyBuckets")[0];
        Assert.Equal("ref", wireBucket.GetProperty("kind").GetString());
        Assert.Equal("b0", wireBucket.GetProperty("bucketId").GetString());

        // Every wire event reached the game callback with its exact payload.
        Assert.Equal(["夜色", "渐深"], chunks);
        Assert.Equal(["【子时】"], startTags);
        Assert.Equal(1, optionsStarted);
        Assert.Equal(["拔剑"], optionValues);
        Assert.Equal(1, optionsCompleted);
        Assert.Equal(["夜色渐深"], completedTexts);
        Assert.Equal("伏笔", result.Metadata!["afterThinking"]);
        Assert.Equal("推理", result.Reasoning);
    }

    [Fact]
    public async Task StreamAsyncRoutesJsonStreamEventsToTheJsonPrimaryOutput()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "jsonStream", """{"kind":"objectStarted","path":""}""") +
            EventSse(1, "jsonStream", """{"kind":"propertyName","path":"","name":"speaker"}""") +
            EventSse(2, "jsonStream", """{"kind":"stringStarted","path":"/speaker"}""") +
            EventSse(3, "jsonStream", """{"kind":"stringChunk","path":"/speaker","value":"道人"}""") +
            EventSse(4, "jsonStream", """{"kind":"stringCompleted","path":"/speaker"}""") +
            EventSse(5, "jsonStream", """{"kind":"objectCompleted","path":""}""") +
            CompletedSse("""{"primary":{"speaker":"道人"}}"""));

        var events = new List<JsonStreamEvent>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("问话")
            .WithPrimaryOutput(new JsonPrimaryOutput(
                "dialogue",
                AiJsonSchema.FromJson(TestSupport.Json("""{"kind":"object","properties":[]}""")),
                (evt, _) => Capture(events, evt.Event)));

        var result = await expert.StreamAsync(TestSupport.CancellationToken);

        var start = ParseBody(handler.CapturedRequests[1]);
        var wireOutput = start.GetProperty("input").GetProperty("primaryOutput");
        Assert.Equal("json", wireOutput.GetProperty("kind").GetString());
        Assert.Equal("dialogue", wireOutput.GetProperty("propertyName").GetString());
        Assert.Equal(
            [
                "ObjectStarted", "PropertyName", "StringStarted", "StringChunk", "StringCompleted", "ObjectCompleted"
            ],
            events.Select(static evt => evt.GetType().Name.Replace("JsonStream", "").Replace("Event", "")));
        Assert.Equal("道人", ((JsonStreamStringChunkEvent)events[3]).Value);
        Assert.Empty(result.Metadata ?? new Dictionary<string, string>());
        Assert.Null(result.Reasoning);
    }

    [Fact]
    public async Task StreamAsyncServesDataRequestsFromTheLocalBucketRegistryWithPaging()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            DataRequestSse(0, "req-desc", "b0", "description") +
            DataRequestSse(1, "req-page-1", "b0", "rawTurns", cursor: 0, limit: 2) +
            DataRequestSse(2, "req-page-2", "b0", "rawTurns", cursor: 2, limit: 2) +
            DataRequestSse(3, "req-view", "b0", "compressedView") +
            EventSse(4, "chunk", """{"text":"ok"}""") +
            CompletedSse("""{"primary":"ok"}"""));
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");

        var bucket = NewBucket("主叙事", "一", "二", "三");
        var chunks = new List<string>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(chunks, evt.Delta)))
            .WithHistoryBuckets(bucket);

        await expert.StreamAsync(TestSupport.CancellationToken);

        var posts = handler.CapturedRequests
            .Where(request => request.Method == HttpMethod.Post && request.Uri.Contains("/data/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, posts.Length);
        Assert.EndsWith("/expert-invocations/" + InvocationId + "/data/req-desc", posts[0].Uri, StringComparison.Ordinal);

        var description = ParseBody(posts[0]);
        Assert.Equal("主叙事", description.GetProperty("description").GetString());

        var page1 = ParseBody(posts[1]);
        Assert.Equal(["一", "二"], page1.GetProperty("items").EnumerateArray()
            .Select(static turn => turn.GetProperty("messages")[0].GetProperty("content").GetString()));
        Assert.Equal(2, page1.GetProperty("nextCursor").GetInt64());

        var page2 = ParseBody(posts[2]);
        Assert.Equal(["三"], page2.GetProperty("items").EnumerateArray()
            .Select(static turn => turn.GetProperty("messages")[0].GetProperty("content").GetString()));
        Assert.False(page2.TryGetProperty("nextCursor", out _));

        var view = ParseBody(posts[3]);
        Assert.Equal(["rawTurn", "rawTurn", "rawTurn"], view.GetProperty("items").EnumerateArray()
            .Select(static entry => entry.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task StreamAsyncServesRawTurnsAcrossDefaultPageSizePages()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            DataRequestSse(0, "req-full", "b0", "rawTurns") +
            DataRequestSse(1, "req-tail", "b0", "rawTurns", cursor: 200) +
            EventSse(2, "chunk", """{"text":"ok"}""") +
            CompletedSse("""{"primary":"ok"}"""));
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");

        // 205 turns: the proxy's default page size (200, used when the request carries no explicit
        // limit) cannot hold them in one response, so the platform follows nextCursor with a
        // continuation request.
        var bucket = NewBucket("长卷", [.. Enumerable.Range(0, 205).Select(static index => "t" + index)]);
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask))
            .WithHistoryBuckets(bucket);

        await expert.StreamAsync(TestSupport.CancellationToken);

        var posts = handler.CapturedRequests
            .Where(request => request.Method == HttpMethod.Post && request.Uri.Contains("/data/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, posts.Length);

        var fullPage = ParseBody(posts[0]);
        Assert.Equal(200, fullPage.GetProperty("items").GetArrayLength());
        Assert.Equal("t0", DataPageItemText(posts[0], 0));
        Assert.Equal("t199", DataPageItemText(posts[0], 199));
        Assert.Equal(200, fullPage.GetProperty("nextCursor").GetInt64());

        var tailPage = ParseBody(posts[1]);
        Assert.Equal(5, tailPage.GetProperty("items").GetArrayLength());
        Assert.Equal("t200", DataPageItemText(posts[1], 0));
        Assert.Equal("t204", DataPageItemText(posts[1], 4));
        Assert.False(tailPage.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task StreamAsyncResumesAfterReconnectAndReservesRedeliveredDataRequests()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        // The first stream delivers a dataRequest and then ends without a terminal frame — the
        // proxy serves the view and reconnects with Last-Event-ID.
        handler.EnqueueSse(DataRequestSse(0, "req-retry", "b0", "description"));
        handler.EnqueueJson("{}");
        // The resumed stream repeats ordinal 0 (deduplicated by the ordinal gate) and re-requests
        // the SAME requestId at a new ordinal (at-least-once delivery): the proxy re-serves the
        // bucket view instead of failing.
        handler.EnqueueSse(
            DataRequestSse(0, "req-retry", "b0", "description") +
            DataRequestSse(1, "req-retry", "b0", "description") +
            EventSse(2, "chunk", """{"text":"ok"}""") +
            CompletedSse("""{"primary":"ok"}"""));
        handler.EnqueueJson("{}");

        var chunks = new List<string>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(chunks, evt.Delta)))
            .WithHistoryBuckets(NewBucket("主叙事", "一"));

        await expert.StreamAsync(TestSupport.CancellationToken);

        // Request order: catalog, start, events#1, data#1, events#2 (resume), data#2.
        Assert.Equal(6, handler.CapturedRequests.Count);
        Assert.Equal("0", handler.CapturedRequests[4].LastEventId);
        var posts = handler.CapturedRequests
            .Where(request => request.Method == HttpMethod.Post && request.Uri.Contains("/data/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, post => Assert.EndsWith("/data/req-retry", post.Uri, StringComparison.Ordinal));
        Assert.All(posts, post =>
            Assert.Equal("主叙事", ParseBody(post).GetProperty("description").GetString()));
        Assert.Equal(["ok"], chunks);
    }

    [Fact]
    public async Task StreamAsyncReServesADataRequestWhenItsResponsePostFailsTransiently()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        // The first stream delivers one dataRequest; the response POST then hits a transient
        // transport failure, which the session must treat as reconnectable.
        handler.EnqueueSse(DataRequestSse(0, "req-flaky", "b0", "description"));
        handler.EnqueueFailure(new HttpRequestException("transient network blip"));
        // The resumed stream replays the SAME ordinal from before the failed serve: the proxy
        // must re-serve the view (the platform memoizes repeated responses), not strand the
        // platform-side waiter until its dataRequestTimeout.
        handler.EnqueueSse(
            DataRequestSse(0, "req-flaky", "b0", "description") +
            EventSse(1, "chunk", """{"text":"ok"}""") +
            CompletedSse("""{"primary":"ok"}"""));
        handler.EnqueueJson("{}");

        var chunks = new List<string>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(chunks, evt.Delta)))
            .WithHistoryBuckets(NewBucket("主叙事", "一"));

        await expert.StreamAsync(TestSupport.CancellationToken);

        // Request order: catalog, start, events#1, data#1 (failed), events#2 (resume), data#2.
        Assert.Equal(6, handler.CapturedRequests.Count);
        Assert.Null(handler.CapturedRequests[4].LastEventId);
        var posts = handler.CapturedRequests
            .Where(request => request.Method == HttpMethod.Post && request.Uri.Contains("/data/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, post =>
            Assert.Equal("主叙事", ParseBody(post).GetProperty("description").GetString()));
        Assert.Equal(["ok"], chunks);
    }

    [Fact]
    public async Task StreamAsyncDispatchesVariableUpdateProposals()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "chunk", """{"text":"正文"}""") +
            EventSse(1, "variableUpdate",
                """{"operations":[{"op":"delta","path":"/courage","value":2},{"op":"replace","path":"/trust","value":80}]}""") +
            CompletedSse("""{"primary":"正文"}"""));

        var proposals = new List<VariableUpdatePatchProposal>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask))
            .WithFeatures(new VariableUpdateFeature((proposal, _) => Capture(proposals, proposal)));

        await expert.StreamAsync(TestSupport.CancellationToken);

        var start = ParseBody(handler.CapturedRequests[1]);
        Assert.Equal(
            ["variableUpdate"],
            start.GetProperty("input").GetProperty("features").EnumerateArray()
                .Select(static feature => feature.GetProperty("kind").GetString()));

        var operations = Assert.Single(proposals).Operations;
        Assert.Equal(2, operations.Count);
        var delta = Assert.IsType<VariableUpdateDelta>(operations[0]);
        Assert.Equal("/courage", delta.Path);
        Assert.Equal(2m, delta.Value);
        var replace = Assert.IsType<VariableUpdateReplace>(operations[1]);
        Assert.Equal("/trust", replace.Path);
        Assert.Equal(80, replace.Value!.GetValue<int>());
    }

    [Fact]
    public async Task StreamAsyncRunsTheGameHelperVariableUpdateTwoPassFlowOverTheWire()
    {
        var handler = new FakeRemoteHttpHandler();
        // Pass 1 — narrative only: the input packet carries no variable-update feature.
        handler.EnqueueJson(OfficialCatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "chunk", """{"text":"正文一"}""") +
            CompletedSse("""{"primary":"正文一"}"""));
        // Pass 2 — the same game flow re-invokes with the GameHelper WithVariableUpdate wiring:
        // currentState + stateSchema + VariableUpdateFeature all travel inside the packet.
        handler.EnqueueJson(OfficialCatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "chunk", """{"text":"正文二"}""") +
            EventSse(1, "variableUpdate",
                """{"operations":[{"op":"delta","path":"/courage","value":2},{"op":"replace","path":"/trust","value":80}]}""") +
            CompletedSse("""{"primary":"正文二"}"""));

        var player = new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "剑客");
        var context = ActionContextTestFactory.Create(
            playerProfile: player,
            state: new GameState(SessionStateExtensions.MaterializeSessionState<TwoPassVariables>(player)));
        var facade = new RemoteExpertFacade(
            new RemoteExpertClient(new HttpClient(handler), new RemoteExpertClientOptions("http://platform.test")),
            OfficialOptions());

        var narrative = new List<string>();
        _ = await facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("仙侠世界")
            .WithPlayerInput("我走出客栈。")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(narrative, evt.Delta)))
            .StreamAsync(TestSupport.CancellationToken);
        _ = await facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("仙侠世界")
            .WithPlayerInput("我走出客栈。")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(narrative, evt.Delta)))
            .WithVariableUpdate<TwoPassVariables>(context)
            .StreamAsync(TestSupport.CancellationToken);

        Assert.Equal(["正文一", "正文二"], narrative);

        // catalog/start/events for each of the two passes.
        Assert.Equal(6, handler.CapturedRequests.Count);
        var firstInput = ParseBody(handler.CapturedRequests[1]).GetProperty("input");
        Assert.False(firstInput.TryGetProperty("features", out _));

        var secondInput = ParseBody(handler.CapturedRequests[4]).GetProperty("input");
        Assert.Equal(
            ["variableUpdate"],
            secondInput.GetProperty("features").EnumerateArray()
                .Select(static feature => feature.GetProperty("kind").GetString()));
        Assert.Contains(
            "courage", secondInput.GetProperty("currentState").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(secondInput.GetProperty("stateSchema").GetString()));

        // The wire proposal reached the GameHelper callback and patched the managed root.
        var variables = context.GetSessionState<TwoPassVariables>();
        Assert.Equal(12, variables.Courage);
        Assert.Equal(80, variables.Trust);
    }

    [Fact]
    public async Task StreamAsyncWithUnknownDataRequestBucketFailsAsAProtocolViolation()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(DataRequestSse(0, "req-1", "b-missing", "description"));

        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask))
            .WithHistoryBuckets(NewBucket("主叙事", "一"));

        var exception = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            expert.StreamAsync(TestSupport.CancellationToken));

        Assert.Contains("b-missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsyncMapsTerminalFailureFramesToTypedExceptions()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse("""data: {"type":"failed","code":"validation","message":"schema violation"}""");

        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask));

        var exception = await Assert.ThrowsAsync<RemoteValidationException>(() =>
            expert.StreamAsync(TestSupport.CancellationToken));

        Assert.Contains("schema violation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsyncMapsAFailureFrameArrivingAfterADataRequest()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        // Mirrors the platform's dataRequestTimeout: the served response arrives, yet the
        // invocation can still terminate via a failed frame on a later ordinal.
        handler.EnqueueSse(
            DataRequestSse(0, "req-1", "b0", "description") +
            """data: {"type":"failed","code":"dataRequestTimeout","message":"no data response"}""" + "\n\n");
        handler.EnqueueJson("""{"description":"主叙事"}""");

        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask))
            .WithHistoryBuckets(NewBucket("主叙事", "一"));

        var exception = await Assert.ThrowsAsync<RemoteExpertException>(() =>
            expert.StreamAsync(TestSupport.CancellationToken));

        Assert.Contains("no data response", exception.Message, StringComparison.Ordinal);
        // The dataRequest was served before the terminal failure: its POST hit the data endpoint.
        Assert.Contains(handler.CapturedRequests, request =>
            request.Method == HttpMethod.Post && request.Uri.Contains("/data/req-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamAsyncMapsTerminalCancelledFrames()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse("""data: {"type":"cancelled"}""");

        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput(static (_, _) => ValueTask.CompletedTask));

        _ = await Assert.ThrowsAsync<RemoteInvocationCancelledException>(() =>
            expert.StreamAsync(TestSupport.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsyncUsesTheSameStreamingWireAndCompletion()
    {
        var handler = new FakeRemoteHttpHandler();
        handler.EnqueueJson(CatalogJson());
        handler.EnqueueJson(SnapshotJson(), System.Net.HttpStatusCode.Accepted);
        handler.EnqueueSse(
            EventSse(0, "chunk", """{"text":"全文"}""") +
            CompletedSse("""{"primary":"全文","reasoning":"r"}"""));

        var chunks = new List<string>();
        var expert = CreateExpert(handler)
            .WithWorldSettings("世界")
            .WithPlayerInput("输入")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) => Capture(chunks, evt.Delta)));

        var result = await expert.CompleteAsync(TestSupport.CancellationToken);

        Assert.Equal(["全文"], chunks);
        Assert.Equal("r", result.Reasoning);
    }

    [Fact]
    public async Task StreamAsyncWithoutCategoryInputsFailsBeforeAnyTraffic()
    {
        var handler = new FakeRemoteHttpHandler();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateExpert(handler).WithPlayerInput("只有输入").StreamAsync(TestSupport.CancellationToken));

        Assert.Empty(handler.CapturedRequests);
    }

    private static ValueTask Capture<T>(List<T> sink, T value)
    {
        sink.Add(value);
        return ValueTask.CompletedTask;
    }

    private static string DataPageItemText(CapturedRemoteRequest request, int index) =>
        ParseBody(request).GetProperty("items")[index]
            .GetProperty("messages")[0].GetProperty("content").GetString()!;

    private sealed class StubBucket(string description) : IHistoryBucket
    {
        private readonly List<HistoryTurn> _turns = [];

        public string Description { get; } = description;

        public void AddMessages(
            string? digest, IReadOnlyDictionary<string, string>? metadata, params ChatMessage[] messages)
        {
            _turns.Add(new HistoryTurn(messages, digest, _turns.Count, metadata));
        }

        public ValueTask<IReadOnlyList<HistoryTurn>> GetRawTurnsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryTurn>>(_turns);
        }

        public ValueTask<IReadOnlyList<HistoryProjectionEntry>> GetCompressedViewAsync(
            CompressedViewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HistoryProjectionEntry>>(
                [.. _turns.Select(static turn => new HistoryProjectionRawTurn(turn))]);
        }
    }
}

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global, UnusedAutoPropertyAccessor.Global, ClassWithVirtualMembersNeverInherited.Global — SessionState 契约要求 virtual 可写属性，由 Castle 跟踪代理写回
[SessionStateRoot]
public class TwoPassVariables
{
    [AiStateMember(0, "主角的勇气值（0-100）。")]
    public virtual int Courage { get; set; } = 10;

    [AiStateMember(1, "主角的信任值（0-100）。")]
    public virtual int Trust { get; set; } = 30;
}
// ReSharper restore AutoPropertyCanBeMadeGetOnly.Global, UnusedAutoPropertyAccessor.Global, ClassWithVirtualMembersNeverInherited.Global
