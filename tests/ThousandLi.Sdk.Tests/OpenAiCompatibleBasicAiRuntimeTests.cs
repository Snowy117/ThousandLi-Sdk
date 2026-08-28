using System.Net;
using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class OpenAiCompatibleBasicAiRuntimeTests
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

    private static OpenAiCompatibleBasicAi CreateAdapter(FakeGatewayHandler handler)
    {
        var options = new OpenAiCompatibleBasicAiOptions(
            Endpoint,
            ["deepseek-v4-pro", "deepseek-v4-flash"],
            () => ApiKey);
        return new OpenAiCompatibleBasicAi(new HttpClient(handler), options);
    }

    private static BasicAiRequest RuntimeRequest(
        string modelId = "deepseek-v4-pro",
        IReadOnlyList<BasicAiToolDescriptor>? tools = null,
        BasicAiSamplingParameters? sampling = null) =>
        new(
            modelId,
            [BasicAiMessage.System("You narrate."), BasicAiMessage.User("hi")],
            AiJsonSchema.Object(AiJsonSchema.Required("narrative", AiJsonSchema.String())),
            tools,
            sampling);

    private static HttpResponseMessage RuntimeCompletionResponse(string content, string? reasoning = null)
    {
        var message = new Dictionary<string, object?> { ["content"] = content };
        if (reasoning is not null)
            message["reasoning_content"] = reasoning;
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new[] { new Dictionary<string, object?> { ["message"] = message } }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage RuntimeSseResponse(params string[] rawFrames)
    {
        var builder = new StringBuilder();
        foreach (var frame in rawFrames)
        {
            builder.Append("data: ");
            builder.Append(frame);
            builder.Append("\n\n");
        }

        builder.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static string DeltaFrame(string? content, string? reasoning)
    {
        var delta = new Dictionary<string, object?>();
        if (content is not null)
            delta["content"] = content;
        if (reasoning is not null)
            delta["reasoning_content"] = reasoning;
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new[] { new Dictionary<string, object?> { ["delta"] = delta } }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static HttpResponseMessage StatusResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<IReadOnlyList<BasicAiStreamEvent>> CollectAsync(IAsyncEnumerable<BasicAiStreamEvent> source)
    {
        var events = new List<BasicAiStreamEvent>();
        await foreach (var streamEvent in source)
            events.Add(streamEvent);
        return events;
    }

    [Fact]
    public async Task CompletionParsesJsonContentAndReasoning()
    {
        var handler = CreateHandler(RuntimeCompletionResponse("""{"narrative":"hello"}""", "thinking hard"));
        var adapter = CreateAdapter(handler);

        var result = await adapter.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        Assert.Equal("hello", result.Json.GetProperty("narrative").GetString());
        Assert.Equal("thinking hard", result.Reasoning);
    }

    [Fact]
    public async Task CompletionWithoutReasoningReturnsNullReasoning()
    {
        var handler = CreateHandler(RuntimeCompletionResponse("""{"narrative":"hello"}"""));
        var adapter = CreateAdapter(handler);

        var result = await adapter.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        Assert.Null(result.Reasoning);
    }

    [Fact]
    public async Task CompletionRejectsNonContainerJsonRoots()
    {
        var handler = CreateHandler(RuntimeCompletionResponse("42"));
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));

        Assert.Contains("JSON object or array root", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain narrative")]
    [InlineData("")]
    public async Task CompletionRejectsNonJsonContentAsTransportFailures(string content)
    {
        var handler = CreateHandler(RuntimeCompletionResponse(content));
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(
            () => adapter.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));

        Assert.Contains("is not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionSendsJsonObjectResponseFormatAndLowercaseRoles()
    {
        var handler = CreateHandler(RuntimeCompletionResponse("{}"));
        var adapter = CreateAdapter(handler);
        var sampling = new BasicAiSamplingParameters { Temperature = 0.7f, TopP = 0.9f };

        await adapter.CompleteAsync(RuntimeRequest(sampling: sampling), TestSupport.CancellationToken);

        var captured = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Endpoint), captured.Uri);
        Assert.Equal($"Bearer {ApiKey}", captured.Authorization);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("deepseek-v4-pro", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0.7f, body.RootElement.GetProperty("temperature").GetSingle());
        Assert.Equal(0.9f, body.RootElement.GetProperty("top_p").GetSingle());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You narrate.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task CompletionOmitsSamplingKeysWhenUnset()
    {
        var handler = CreateHandler(RuntimeCompletionResponse("{}"));
        var adapter = CreateAdapter(handler);

        await adapter.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task ToolRequestsAreRejectedBeforeAnyNetworkCall()
    {
        var handler = CreateHandler();
        var adapter = CreateAdapter(handler);
        var tools = new[]
        {
            new BasicAiToolDescriptor("roll_dice", "Rolls a die", AiJsonSchema.Object())
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.CompleteAsync(RuntimeRequest(tools: tools), TestSupport.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await CollectAsync(adapter.StreamAsync(RuntimeRequest(tools: tools), TestSupport.CancellationToken)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamEmitsJsonEventsAndReasoningDeltasInFrameOrder()
    {
        var handler = CreateHandler(RuntimeSseResponse(
            DeltaFrame(content: null, reasoning: "thinking"),
            DeltaFrame(content: """{"na""", reasoning: null),
            DeltaFrame(content: """rrative":"he""", reasoning: " more"),
            DeltaFrame(content: """llo"}""", reasoning: null)));
        var adapter = CreateAdapter(handler);

        var events = await CollectAsync(adapter.StreamAsync(RuntimeRequest("deepseek-v4-flash"), TestSupport.CancellationToken));

        var reasoning = events.OfType<BasicAiReasoningStreamEvent>().Select(delta => delta.Delta).ToArray();
        Assert.Equal(["thinking", " more"], reasoning);
        var jsonEvents = events.OfType<BasicAiJsonStreamEvent>().Select(streamEvent => streamEvent.Event).ToArray();
        Assert.IsType<JsonStreamObjectStartedEvent>(jsonEvents[0]);
        Assert.Equal("", jsonEvents[0].Path);
        Assert.Equal(
            "hello",
            string.Concat(jsonEvents.OfType<JsonStreamStringChunkEvent>().Select(chunk => chunk.Value)));
        Assert.IsType<JsonStreamObjectCompletedEvent>(jsonEvents[^1]);
        Assert.Equal("", jsonEvents[^1].Path);
    }

    [Fact]
    public async Task StreamReasoningBeforeContentStaysOrdered()
    {
        var handler = CreateHandler(RuntimeSseResponse(
            DeltaFrame(content: null, reasoning: "first"),
            DeltaFrame(content: "{}", reasoning: null)));
        var adapter = CreateAdapter(handler);

        var events = await CollectAsync(adapter.StreamAsync(RuntimeRequest(), TestSupport.CancellationToken));

        Assert.IsType<BasicAiReasoningStreamEvent>(events[0]);
        Assert.IsType<BasicAiJsonStreamEvent>(events[1]);
    }

    [Fact]
    public async Task MalformedModelJsonInRuntimeStreamThrowsParserException()
    {
        var handler = CreateHandler(RuntimeSseResponse(DeltaFrame(content: """{"a": tru""", reasoning: null)));
        var adapter = CreateAdapter(handler);

        await Assert.ThrowsAsync<ExpertJsonStreamException>(async () =>
            await CollectAsync(adapter.StreamAsync(RuntimeRequest(), TestSupport.CancellationToken)));
    }

    [Fact]
    public async Task UnknownModelsAreRejectedBeforeAnyNetworkCall()
    {
        var handler = CreateHandler();
        var adapter = CreateAdapter(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CompleteAsync(RuntimeRequest("gpt-unknown"), TestSupport.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(adapter.StreamAsync(RuntimeRequest("gpt-unknown"), TestSupport.CancellationToken)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamingHttpErrorFailsBeforeAnyEvents()
    {
        var handler = CreateHandler(StatusResponse(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<LocalBasicAiException>(async () =>
            await CollectAsync(adapter.StreamAsync(RuntimeRequest(), TestSupport.CancellationToken)));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task StreamingCancellationPropagates()
    {
        var handler = CreateHandler(RuntimeSseResponse(DeltaFrame(content: "{}", reasoning: null)));
        var adapter = CreateAdapter(handler);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(adapter.StreamAsync(RuntimeRequest(), cancellationSource.Token)));
    }

    [Fact]
    public void RuntimeAvailableModelsProjectFromOptions()
    {
        var adapter = CreateAdapter(CreateHandler());
        IRuntimeBasicAi runtime = adapter;

        Assert.Equal(
            ["deepseek-v4-pro", "deepseek-v4-flash"],
            runtime.AvailableModels.Select(descriptor => descriptor.ModelId));
    }
}
