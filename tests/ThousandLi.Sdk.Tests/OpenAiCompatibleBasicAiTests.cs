using System.Text;
using System.Net;
using System.Text.Json;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class OpenAiCompatibleBasicAiTests
{
    private const string ApiKey = "sk-test-secret-key-123";
    private const string Endpoint = "https://gateway.example/v1/chat/completions";

    private sealed record CapturedRequest(Uri? Uri, string? Authorization, string Body);

    private sealed class FakeGatewayHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(",", values)
                : null;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri, authorization, body));
            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("The fake gateway received more requests than queued responses.");
        }
    }

    private static FakeGatewayHandler CreateHandler(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>();
        foreach (var response in responses)
            queue.Enqueue(response);
        return new FakeGatewayHandler(queue);
    }

    private static OpenAiCompatibleBasicAi CreateAdapter(
        FakeGatewayHandler handler,
        Func<string?>? apiKeyProvider,
        bool defaultToTestKey)
    {
        var resolvedProvider = defaultToTestKey ? apiKeyProvider ?? (() => ApiKey) : apiKeyProvider;
        var options = new OpenAiCompatibleBasicAiOptions(
            Endpoint,
            ["deepseek-v4-pro", "deepseek-v4-flash"],
            resolvedProvider);
        return new OpenAiCompatibleBasicAi(new HttpClient(handler), options);
    }

    private static HttpResponseMessage CompletionResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content, role = "assistant" } } }
            }),
            Encoding.UTF8,
            "application/json")
    };

    private static HttpResponseMessage SseResponse(params string[] deltas)
    {
        var builder = new StringBuilder();
        foreach (var delta in deltas)
        {
            builder.Append("data: ");
            builder.Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = delta } } }
            }));
            builder.Append("\n\n");
        }

        builder.Append(": keep-alive comment\n");
        builder.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage RawSseResponse(string raw) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "text/event-stream")
        };

    private static HttpResponseMessage StatusResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LocalBasicAiRequest TextRequest(string modelId = "deepseek-v4-pro") =>
        new(modelId, [new LocalBasicAiMessage("system", "You narrate."), new LocalBasicAiMessage("user", "hi")]);

    private static async Task<IReadOnlyList<ExpertStreamEvent>> CollectAsync(IAsyncEnumerable<ExpertStreamEvent> source)
    {
        var events = new List<ExpertStreamEvent>();
        await foreach (var streamEvent in source)
            events.Add(streamEvent);
        return events;
    }

    [Fact]
    public async Task CompletionPostsChatCompletionsBodyWithBearerHeader()
    {
        var handler = CreateHandler(CompletionResponse("narrated"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var result = await adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken);

        Assert.Equal("narrated", result.Text);
        var captured = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Endpoint), captured.Uri);
        Assert.Equal($"Bearer {ApiKey}", captured.Authorization);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("deepseek-v4-pro", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You narrate.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hi", messages[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrEmptyApiKeyOmitsTheAuthorizationHeader(bool omitProvider)
    {
        var handler = CreateHandler(CompletionResponse("ok"));
        var provider = omitProvider ? null : (Func<string?>?)(() => "   ");
        var adapter = CreateAdapter(handler, provider, defaultToTestKey: false);

        await adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task HttpErrorStatusSurfacesAsLocalBasicAiExceptionWithStatusCode()
    {
        var handler = CreateHandler(StatusResponse(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("deepseek-v4-pro", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonCompletionBodyIsWrapped()
    {
        var handler = CreateHandler(StatusResponse(HttpStatusCode.OK, "<html>gateway exploded</html>"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.IsType<JsonException>(exception.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task CompletionWithoutChoicesIsRejected()
    {
        var handler = CreateHandler(StatusResponse(HttpStatusCode.OK, """{"choices":[]}"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        await Assert.ThrowsAsync<LocalBasicAiException>(() => adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken));
    }

    [Fact]
    public async Task JsonModeCompletionRejectsNonJsonRoot()
    {
        var handler = CreateHandler(CompletionResponse("just plain text"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        var request = new LocalBasicAiRequest(
            "deepseek-v4-pro",
            [new LocalBasicAiMessage("user", "give json")],
            LocalBasicAiResponseMode.Json);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(request, TestSupport.CancellationToken));

        Assert.Contains("JSON object or array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextStreamingEmitsDeltasUntilDone()
    {
        var handler = CreateHandler(SseResponse("Hello", " ", "world", "!"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var events = await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.All(events, @event => Assert.IsType<ExpertTextDeltaEvent>(@event));
        Assert.Equal(
            "Hello world!",
            string.Concat(events.Cast<ExpertTextDeltaEvent>().Select(delta => delta.Delta)));
    }

    [Fact]
    public async Task JsonStreamingEmitsStructuredEventsAcrossChunkBoundaries()
    {
        var handler = CreateHandler(SseResponse("""{"na""", """rrative":"he""", """llo"}"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        var request = new LocalBasicAiRequest(
            "deepseek-v4-flash",
            [new LocalBasicAiMessage("user", "give json")],
            LocalBasicAiResponseMode.Json);

        var events = await CollectAsync(adapter.StreamAsync(request, TestSupport.CancellationToken));

        Assert.IsType<ExpertJsonObjectStartedEvent>(events[0]);
        Assert.IsType<ExpertJsonPropertyNameEvent>(events[1]);
        Assert.IsType<ExpertJsonStringStartedEvent>(events[2]);
        Assert.IsType<ExpertJsonStringChunkEvent>(events[3]);
        Assert.IsType<ExpertJsonStringChunkEvent>(events[4]);
        Assert.IsType<ExpertJsonStringCompletedEvent>(events[5]);
        Assert.IsType<ExpertJsonObjectCompletedEvent>(events[^1]);
        Assert.Equal(
            "hello",
            string.Concat(events.OfType<ExpertJsonStringChunkEvent>().Select(chunk => chunk.Value)));
    }

    [Fact]
    public async Task MalformedModelJsonInJsonModeThrowsParserException()
    {
        var handler = CreateHandler(SseResponse("""{"a": tru"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        var request = new LocalBasicAiRequest(
            "deepseek-v4-pro",
            [new LocalBasicAiMessage("user", "give json")],
            LocalBasicAiResponseMode.Json);

        await Assert.ThrowsAsync<ExpertJsonStreamException>(async () =>
            await CollectAsync(adapter.StreamAsync(request, TestSupport.CancellationToken)));
    }

    [Fact]
    public async Task StreamingHttpErrorFailsBeforeAnyEvents()
    {
        var handler = CreateHandler(StatusResponse(HttpStatusCode.InternalServerError, "boom"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        await Assert.ThrowsAsync<LocalBasicAiException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken)));
    }

    [Fact]
    public async Task UnknownModelsAreRejectedBeforeAnyNetworkCall()
    {
        var handler = CreateHandler();
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CompleteAsync(TextRequest("gpt-unknown"), TestSupport.CancellationToken));

        Assert.Contains("not configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CredentialsNeverAppearInResultsOrStreamedEvents()
    {
        var handler = CreateHandler(CompletionResponse("done"), SseResponse("streamed"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var completion = await adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken);
        var streamed = await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.DoesNotContain(ApiKey, completion.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, string.Concat(streamed.OfType<ExpertTextDeltaEvent>().Select(delta => delta.Delta)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingCancellationPropagates()
    {
        var handler = CreateHandler(SseResponse("never consumed"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest(), cancellationSource.Token)));
    }

    [Fact]
    public void AvailableModelsProjectFromOptions()
    {
        var adapter = CreateAdapter(CreateHandler(), null, defaultToTestKey: true);
        Assert.Equal(["deepseek-v4-pro", "deepseek-v4-flash"], adapter.AvailableModels);
    }

    [Theory]
    [InlineData("ftp://gateway.example/v1/chat/completions")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void InvalidEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleBasicAiOptions(endpoint, ["model"], () => ApiKey));
    }

    [Fact]
    public void DuplicateOrEmptyModelsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleBasicAiOptions(Endpoint, ["m1", "m1"]));
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleBasicAiOptions(Endpoint, ["m1", " "]));
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleBasicAiOptions(Endpoint, []));
    }

    [Fact]
    public void ConstructorRejectsNullArguments()
    {
        var options = new OpenAiCompatibleBasicAiOptions(Endpoint, ["m1"], () => ApiKey);
        Assert.Throws<ArgumentNullException>(() => new OpenAiCompatibleBasicAi(null!, options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiCompatibleBasicAi(new HttpClient(CreateHandler()), null!));
    }

    [Fact]
    public void OptionsModelsAreDefensivelyCopied()
    {
        List<string> models = ["m1", "m2"];
        var options = new OpenAiCompatibleBasicAiOptions(Endpoint, models, () => ApiKey);
        models.Add("m3-mutated");

        Assert.Equal(["m1", "m2"], options.Models);
        Assert.Equal(["m1", "m2"], new OpenAiCompatibleBasicAi(new HttpClient(CreateHandler()), options).AvailableModels);
    }

    [Fact]
    public async Task GatewayErrorBodiesEchoingTheApiKeyAreRedacted()
    {
        var handler = CreateHandler(StatusResponse(
            HttpStatusCode.Unauthorized,
            $$$"""{"error":{"message":"Incorrect API key provided: {{{ApiKey}}}. You can find your API key at ..."}}"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key provided", exception.Message, StringComparison.Ordinal);
        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task StreamErrorBodiesEchoingTheApiKeyAreRedacted()
    {
        var handler = CreateHandler(StatusResponse(
            HttpStatusCode.Forbidden,
            $$"""{"error":"key {{ApiKey}} rejected"}"""));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken)));

        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task RedactedErrorMessagesStayBoundedWhenBodiesAreLong()
    {
        var longBody = new string('x', 4096) + ApiKey + new string('y', 4096);
        var handler = CreateHandler(StatusResponse(HttpStatusCode.BadGateway, longBody));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(TextRequest(), TestSupport.CancellationToken));

        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length < 1024, $"Message was not truncated: {exception.Message.Length}");
    }

    [Fact]
    public async Task MalformedDataFramesFailAsTransportErrors()
    {
        var handler = CreateHandler(RawSseResponse("data: {oops\n\ndata: [DONE]\n\n"));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken)));

        Assert.Contains("deepseek-v4-pro", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException, exactMatch: false);
    }

    [Theory]
    [InlineData("data: {}\n\n")]
    [InlineData("data: {\"choices\":[]}\n\n")]
    [InlineData("data: {\"choices\":{}}\n\n")]
    public async Task DataFramesWithoutAUsableChoicesArrayFail(string raw)
    {
        var handler = CreateHandler(RawSseResponse(raw));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        await Assert.ThrowsAsync<LocalBasicAiException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken)));
    }

    [Fact]
    public async Task FramesWithANonObjectDeltaAreToleratedAndSkipped()
    {
        var handler = CreateHandler(RawSseResponse(
            """
            data: {"choices":[{"delta":42}]}

            data: {"choices":[{"delta":{"content":"kept"}}]}

            data: [DONE]

            """));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var events = await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken));

        var delta = Assert.Single(events.OfType<ExpertTextDeltaEvent>());
        Assert.Equal("kept", delta.Delta);
    }

    [Fact]
    public async Task FramesCarryingOnlyRoleOrReasoningContentAreSkipped()
    {
        var handler = CreateHandler(RawSseResponse(
            """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"reasoning_content":"thinking hard"}}]}

            data: {"choices":[{"delta":{"reasoning_content":"more","content":"answer"}}]}

            data: {"choices":[{"delta":{"content":""}}]}

            data: [DONE]

            """));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var events = await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken));

        var delta = Assert.Single(events.OfType<ExpertTextDeltaEvent>());
        Assert.Equal("answer", delta.Delta);
    }

    [Fact]
    public async Task TheDoneTerminatorEndsTheStreamEvenWithTrailingFrames()
    {
        var handler = CreateHandler(RawSseResponse(
            """
            event: message
            data: {"choices":[{"delta":{"content":"first"}}]}

            data: [DONE]

            data: {"choices":[{"delta":{"content":"after done"}}]}

            """));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var events = await CollectAsync(adapter.StreamAsync(TextRequest(), TestSupport.CancellationToken));

        var delta = Assert.Single(events.OfType<ExpertTextDeltaEvent>());
        Assert.Equal("first", delta.Delta);
    }

    [Theory]
    [InlineData("[1,2,3]", true)]
    [InlineData("{\"a\":1}", true)]
    [InlineData("  {\"a\":1}  ", true)]
    [InlineData("42", false)]
    [InlineData("true", false)]
    [InlineData("null", false)]
    [InlineData("\"quoted\"", false)]
    [InlineData("plain narrative", false)]
    public async Task JsonModeCompletionsRequireAContainerRoot(string content, bool accepted)
    {
        var handler = CreateHandler(CompletionResponse(content));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        var request = new LocalBasicAiRequest(
            "deepseek-v4-pro",
            [new LocalBasicAiMessage("user", "give json")],
            LocalBasicAiResponseMode.Json);

        if (accepted)
        {
            var result = await adapter.CompleteAsync(request, TestSupport.CancellationToken);
            Assert.Equal(content, result.Text);
        }
        else
        {
            await Assert.ThrowsAsync<LocalBasicAiException>(
                () => adapter.CompleteAsync(request, TestSupport.CancellationToken));
        }
    }

    [Fact]
    public async Task UnknownModelStreamsAreRejectedBeforeAnyNetworkCall()
    {
        var handler = CreateHandler();
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(adapter.StreamAsync(TextRequest("gpt-unknown"), TestSupport.CancellationToken)));

        Assert.Contains("not configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MidStreamCancellationStopsTheSseLoop()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 4000; i++)
            builder.Append("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n");
        builder.Append("data: [DONE]\n\n");
        var handler = CreateHandler(RawSseResponse(builder.ToString()));
        var adapter = CreateAdapter(handler, null, defaultToTestKey: true);
        using var cancellationSource = new CancellationTokenSource();

        var received = new List<ExpertStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in adapter.StreamAsync(TextRequest(), cancellationSource.Token))
            {
                received.Add(streamEvent);
                if (received.Count == 1)
                    await cancellationSource.CancelAsync();
            }
        });

        Assert.True(received.Count < 4000, "The stream drained fully despite mid-stream cancellation.");
    }
}
