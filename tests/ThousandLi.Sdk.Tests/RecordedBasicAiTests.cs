using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class RecordedBasicAiTests
{
    private static LocalBasicAiRequest Request(string modelId = "m1") =>
        new(modelId, [new LocalBasicAiMessage("user", "hello")]);

    [Fact]
    public async Task CompletionsReplayInRegistrationOrder()
    {
        var basicAi = new RecordedBasicAi(["m1"], [
            new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("first")),
            new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("second"))
        ]);

        var first = await basicAi.CompleteAsync(Request(), TestSupport.CancellationToken);
        var second = await basicAi.CompleteAsync(Request(), TestSupport.CancellationToken);

        Assert.Equal("first", first.Text);
        Assert.Equal("second", second.Text);
        Assert.Equal(2, basicAi.Invocations.Count);
    }

    [Fact]
    public async Task StreamEventsReplayVerbatim()
    {
        ExpertStreamEvent[] recorded =
        [
            new ExpertTextDeltaEvent("a"),
            new ExpertTextDeltaEvent("b")
        ];
        var basicAi = new RecordedBasicAi(["m1"], [new RecordedBasicAiInteraction("m1", streamEvents: recorded)]);

        var events = new List<ExpertStreamEvent>();
        await foreach (var streamEvent in basicAi.StreamAsync(Request(), TestSupport.CancellationToken))
            events.Add(streamEvent);

        Assert.Equal(recorded, events);
    }

    [Fact]
    public async Task ModelMismatchFailsWithBothIdentifiers()
    {
        var basicAi = new RecordedBasicAi(["m1", "m2"], [
            new RecordedBasicAiInteraction("m2", completion: new LocalBasicAiCompletionResult("x"))
        ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(Request(), TestSupport.CancellationToken));

        Assert.Contains("'m2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'m1'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExhaustingTheRecordingFailsFast()
    {
        var basicAi = new RecordedBasicAi(["m1"], [
            new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("only"))
        ]);

        await basicAi.CompleteAsync(Request(), TestSupport.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(Request(), TestSupport.CancellationToken));

        Assert.Contains("No recorded interactions remain", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionAndStreamShapesMustMatchTheInvocation(bool recordedAsStream)
    {
        var interaction = recordedAsStream
            ? new RecordedBasicAiInteraction("m1", streamEvents: [new ExpertTextDeltaEvent("x")])
            : new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("x"));
        var basicAi = new RecordedBasicAi(["m1"], [interaction]);

        if (recordedAsStream)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => basicAi.CompleteAsync(Request(), TestSupport.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in basicAi.StreamAsync(Request(), TestSupport.CancellationToken))
                {
                }
            });
        }
    }

    [Fact]
    public void InteractionsRequireExactlyOneShape()
    {
        Assert.Throws<ArgumentException>(() => new RecordedBasicAiInteraction("m1"));
        Assert.Throws<ArgumentException>(() => new RecordedBasicAiInteraction(
            "m1",
            completion: new LocalBasicAiCompletionResult("x"),
            streamEvents: [new ExpertTextDeltaEvent("y")]));
    }

    [Fact]
    public void AvailableModelsAreTakenFromConstruction()
    {
        var basicAi = new RecordedBasicAi(["a", "b"], []);
        Assert.Equal(["a", "b"], basicAi.AvailableModels);
    }

    [Fact]
    public void ConstructorValidatesModelAndInteractionInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordedBasicAi(null!, []));
        Assert.Throws<ArgumentException>(() => new RecordedBasicAi([], []));
        Assert.Throws<ArgumentException>(() => new RecordedBasicAi([" "], []));
        Assert.Throws<ArgumentNullException>(() => new RecordedBasicAi(["m1"], null!));
        Assert.Throws<ArgumentNullException>(() => new RecordedBasicAi(["m1"], [null!]));
    }

    [Fact]
    public async Task PreCanceledStreamEnumerationFailsFast()
    {
        var basicAi = new RecordedBasicAi(["m1"], [
            new RecordedBasicAiInteraction("m1", streamEvents: [new ExpertTextDeltaEvent("x")])
        ]);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in basicAi.StreamAsync(Request(), cancellationSource.Token))
            {
            }
        });
    }

    [Fact]
    public async Task ShapeMismatchesConsumeTheInteractionAndNameBothShapes()
    {
        var basicAi = new RecordedBasicAi(["m1"], [
            new RecordedBasicAiInteraction("m1", streamEvents: [new ExpertTextDeltaEvent("streamed")]),
            new RecordedBasicAiInteraction("m1", completion: new LocalBasicAiCompletionResult("next"))
        ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => basicAi.CompleteAsync(Request(), TestSupport.CancellationToken));
        Assert.Contains("stream", exception.Message, StringComparison.Ordinal);
        Assert.Contains("non-streaming", exception.Message, StringComparison.Ordinal);

        var next = await basicAi.CompleteAsync(Request(), TestSupport.CancellationToken);
        Assert.Equal("next", next.Text);
    }
}
