using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class RecordedBasicAiRuntimeSurfaceTests
{
    private static BasicAiRequest RuntimeRequest(string modelId = "m1") =>
        new(modelId, [BasicAiMessage.User("hello")], AiJsonSchema.Object());

    private static BasicAiStreamEvent[] MixedEvents() =>
    [
        new BasicAiReasoningStreamEvent("thinking"),
        new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")),
        new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "hello")),
        new BasicAiReasoningStreamEvent(" more")
    ];

    [Fact]
    public async Task CompletionsReplayInRegistrationOrder()
    {
        var basicAi = new RecordedBasicAi(["m1"], [],
        [
            new RecordedRuntimeBasicAiInteraction("m1", completion: new BasicAiCompletionResult(TestSupport.Json("""{"first":1}"""))),
            new RecordedRuntimeBasicAiInteraction("m1", completion: new BasicAiCompletionResult(TestSupport.Json("""{"second":2}""")))
        ]);

        var first = await basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);
        var second = await basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        Assert.Equal(1, first.Json.GetProperty("first").GetInt32());
        Assert.Equal(2, second.Json.GetProperty("second").GetInt32());
        Assert.Equal(2, basicAi.RuntimeInvocations.Count);
    }

    [Fact]
    public async Task CompletionReplayPreservesReasoning()
    {
        var basicAi = new RecordedBasicAi(["m1"], [],
        [
            new RecordedRuntimeBasicAiInteraction(
                "m1",
                completion: new BasicAiCompletionResult(TestSupport.Json("""{"answer":42}"""), "because"))
        ]);

        var result = await basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        Assert.Equal("because", result.Reasoning);
        Assert.Equal(42, result.Json.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task StreamEventsReplayVerbatimIncludingReasoning()
    {
        var recorded = MixedEvents();
        var basicAi = new RecordedBasicAi(["m1"], [],
        [
            new RecordedRuntimeBasicAiInteraction("m1", streamEvents: recorded)
        ]);

        var events = new List<BasicAiStreamEvent>();
        await foreach (var streamEvent in basicAi.StreamAsync(RuntimeRequest(), TestSupport.CancellationToken))
            events.Add(streamEvent);

        Assert.Equal(recorded, events);
    }

    [Fact]
    public async Task ModelMismatchFailsWithBothIdentifiers()
    {
        var basicAi = new RecordedBasicAi(["m1", "m2"], [],
        [
            new RecordedRuntimeBasicAiInteraction("m2", completion: new BasicAiCompletionResult(TestSupport.Json("{}")))
        ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));

        Assert.Contains("'m2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'m1'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExhaustingTheRecordingFailsFast()
    {
        var basicAi = new RecordedBasicAi(["m1"], [],
        [
            new RecordedRuntimeBasicAiInteraction("m1", completion: new BasicAiCompletionResult(TestSupport.Json("{}")))
        ]);

        await basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));

        Assert.Contains("No recorded runtime interactions remain", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSurfaceWithoutRuntimeInteractionsFailsFast()
    {
        var basicAi = new RecordedBasicAi(["m1"], [
            new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("x"))
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionAndStreamShapesMustMatchTheInvocation(bool recordedAsStream)
    {
        var interaction = recordedAsStream
            ? new RecordedRuntimeBasicAiInteraction("m1", streamEvents: MixedEvents())
            : new RecordedRuntimeBasicAiInteraction("m1", completion: new BasicAiCompletionResult(TestSupport.Json("{}")));
        var basicAi = new RecordedBasicAi(["m1"], [], [interaction]);

        if (recordedAsStream)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in basicAi.StreamAsync(RuntimeRequest(), TestSupport.CancellationToken))
                {
                }
            });
        }
    }

    [Fact]
    public void InteractionsRequireExactlyOneShape()
    {
        Assert.Throws<ArgumentException>(() => new RecordedRuntimeBasicAiInteraction("m1"));
        Assert.Throws<ArgumentException>(() => new RecordedRuntimeBasicAiInteraction(
            "m1",
            completion: new BasicAiCompletionResult(TestSupport.Json("{}")),
            streamEvents: MixedEvents()));
    }

    [Fact]
    public void InteractionsRejectBlankModelIdsAndNullEvents()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RecordedRuntimeBasicAiInteraction(" "));
        Assert.Throws<ArgumentNullException>(() => new RecordedRuntimeBasicAiInteraction(
            "m1",
            streamEvents: [new BasicAiReasoningStreamEvent("x"), null!]));
    }

    [Fact]
    public void ConstructorRejectsNullRuntimeInteractions()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordedBasicAi(
            ["m1"],
            [],
            [null!]));
    }

    [Fact]
    public void RuntimeAvailableModelsProjectFromConfiguration()
    {
        var basicAi = new RecordedBasicAi(["a", "b"], []);
        IRuntimeBasicAi runtime = basicAi;

        Assert.Equal(["a", "b"], runtime.AvailableModels.Select(descriptor => descriptor.ModelId));
    }

    [Fact]
    public async Task LocalAndRuntimeSurfacesReplayFromIndependentQueues()
    {
        var basicAi = new RecordedBasicAi(
            ["m1"],
            [new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("local"))],
            [new RecordedRuntimeBasicAiInteraction("m1", completion: new BasicAiCompletionResult(TestSupport.Json("""{"runtime":true}""")))]);

        var localResult = await basicAi.CompleteAsync(
            new LocalBasicAiRequest("m1", [new LocalBasicAiMessage("user", "hi")]),
            TestSupport.CancellationToken);
        var runtimeResult = await basicAi.CompleteAsync(RuntimeRequest(), TestSupport.CancellationToken);

        Assert.Equal("local", localResult.Text);
        Assert.True(runtimeResult.Json.GetProperty("runtime").GetBoolean());
        Assert.Single(basicAi.Invocations);
        Assert.Single(basicAi.RuntimeInvocations);
    }

    [Fact]
    public async Task PreCanceledRuntimeEnumerationFailsFast()
    {
        var basicAi = new RecordedBasicAi(["m1"], [],
        [
            new RecordedRuntimeBasicAiInteraction("m1", streamEvents: MixedEvents())
        ]);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in basicAi.StreamAsync(RuntimeRequest(), cancellationSource.Token))
            {
            }
        });
    }
}
