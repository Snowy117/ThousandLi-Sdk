using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class RecordingReplayExpertRunnerTests
{
    [Fact]
    public async Task WriteReadReplayRoundTripProducesDeterministicResult()
    {
        var recording = TestSupport.CreateRecording(
            events: [("narrative", """{"text":"once"}"""), ("options", """{"items":["a","b"]}""")],
            channelKey: "playground-00000001");
        var runner = new RecordingReplayExpertRunner(
            [ExpertInvocationRecording.FromJson(recording.ToJson())]);

        var sink = new RecordingSemanticSink();
        var result = await runner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, "advance", TestSupport.Json("""{"prompt":"hello"}"""), "live-channel"),
            sink,
            TestSupport.CancellationToken);

        Assert.Equal(0, runner.RemainingRecordings);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("advance", invocation.ScenarioId);
        Assert.Equal("live-channel", invocation.ChannelKey);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("narrative", sink.Events[0].EventType);
        Assert.True(JsonElement.DeepEquals(recording.Events[0].Payload, sink.Events[0].Payload));
        Assert.Equal("options", sink.Events[1].EventType);
        Assert.True(JsonElement.DeepEquals(recording.Events[1].Payload, sink.Events[1].Payload));
        Assert.Matches("""^replay-\d{8}$""", result.InvocationId);
        Assert.True(JsonElement.DeepEquals(recording.Terminal.Output!.Value, result.Output));
    }

    [Fact]
    public async Task EmptyCommittedRecordingReplaysWithNoEventsAndTheRecordedOutput()
    {
        var recording = TestSupport.CreateRecording(events: [], output: """{"text":"done"}""");
        var runner = new RecordingReplayExpertRunner([recording]);
        var sink = new RecordingSemanticSink();

        var result = await runner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
            sink,
            TestSupport.CancellationToken);

        Assert.Empty(sink.Events);
        Assert.True(JsonElement.DeepEquals(recording.Terminal.Output!.Value, result.Output));
        Assert.Equal(0, runner.RemainingRecordings);
    }

    [Fact]
    public async Task RecordingsAreConsumedInRegistrationOrder()
    {
        var runner = new RecordingReplayExpertRunner(
        [
            TestSupport.CreateRecording(
                events: [("first", "{}")],
                channelKey: "channel-a",
                recordedAtUtc: new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero)),
            TestSupport.CreateRecording(
                events: [("second", "{}")],
                channelKey: "channel-b",
                recordedAtUtc: new DateTimeOffset(2026, 8, 24, 8, 0, 1, TimeSpan.Zero))
        ]);

        var firstSink = new RecordingSemanticSink();
        var secondSink = new RecordingSemanticSink();
        await runner.ExecuteAsync(Request(), firstSink, TestSupport.CancellationToken);
        await runner.ExecuteAsync(Request(), secondSink, TestSupport.CancellationToken);

        Assert.Equal("first", Assert.Single(firstSink.Events).EventType);
        Assert.Equal("second", Assert.Single(secondSink.Events).EventType);
        Assert.Equal(0, runner.RemainingRecordings);
        return;

        static ExpertInvocationRequest Request()
        {
            return new ExpertInvocationRequest(
                TestSupport.Contract, null, TestSupport.Json("{}"), "channel");
        }
    }

    [Fact]
    public async Task ExhaustingRecordingsFailsFastWithContractDiagnostic()
    {
        var runner = new RecordingReplayExpertRunner([TestSupport.CreateRecording()]);
        await runner.ExecuteAsync(Request(), new RecordingSemanticSink(), TestSupport.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ExecuteAsync(Request(), new RecordingSemanticSink(), TestSupport.CancellationToken).AsTask());

        Assert.Contains("No recorded invocations remain", exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestSupport.Contract.Id, exception.Message, StringComparison.Ordinal);
        return;

        static ExpertInvocationRequest Request()
        {
            return new ExpertInvocationRequest(
                TestSupport.Contract, null, TestSupport.Json("{}"), "channel");
        }
    }

    [Fact]
    public async Task ContractMismatchFailsWithBothSidesInTheDiagnostic()
    {
        var recording = TestSupport.CreateRecording(
            contract: new ExpertContractDescriptor("tests/other", new ContractVersion(1, 0), "other-fp"));
        var runner = new RecordingReplayExpertRunner([recording]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ExecuteAsync(Request(), new RecordingSemanticSink(), TestSupport.CancellationToken).AsTask());

        Assert.Contains("tests/other", exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestSupport.Contract.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("other-fp", exception.Message, StringComparison.Ordinal);
        return;

        static ExpertInvocationRequest Request()
        {
            return new ExpertInvocationRequest(
                TestSupport.Contract, null, TestSupport.Json("{}"), "channel");
        }
    }

    [Fact]
    public async Task ContractVersionFollowsSupportsSemantics()
    {
        var olderRequestRunner = new RecordingReplayExpertRunner(
        [
            TestSupport.CreateRecording(contract: new ExpertContractDescriptor(
                TestSupport.Contract.Id, new ContractVersion(1, 1), TestSupport.Contract.Fingerprint))
        ]);
        await olderRequestRunner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken);

        var newerRequestRunner = new RecordingReplayExpertRunner(
        [
            TestSupport.CreateRecording(contract: new ExpertContractDescriptor(
                TestSupport.Contract.Id, new ContractVersion(1, 0), TestSupport.Contract.Fingerprint))
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newerRequestRunner.ExecuteAsync(
            new ExpertInvocationRequest(
                new ExpertContractDescriptor(TestSupport.Contract.Id, new ContractVersion(1, 1), TestSupport.Contract.Fingerprint),
                null, TestSupport.Json("{}"), "channel"),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(ExpertRecordingTerminal.Aborted)]
    [InlineData(ExpertRecordingTerminal.ErrorStatus)]
    public async Task FailedRecordingsReproduceTheRecordedFailureWithoutEmittingEvents(string status)
    {
        var recording = TestSupport.CreateRecording(
            events: [("never", "{}")],
            terminalStatus: status,
            terminalError: "recorded boom");
        var runner = new RecordingReplayExpertRunner([recording]);
        var sink = new RecordingSemanticSink();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ExecuteAsync(
                new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
                sink,
                TestSupport.CancellationToken).AsTask());

        Assert.Contains("failed as recorded", exception.Message, StringComparison.Ordinal);
        Assert.Contains(status, exception.Message, StringComparison.Ordinal);
        Assert.Contains("recorded boom", exception.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Events);
        Assert.Equal(0, runner.RemainingRecordings);
    }

    [Fact]
    public async Task CancellationPropagatesAndEmitsNothingWhenAlreadyCancelled()
    {
        var runner = new RecordingReplayExpertRunner([TestSupport.CreateRecording()]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var sink = new RecordingSemanticSink();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
            sink,
            cancelled.Token).AsTask());

        Assert.Empty(sink.Events);
        Assert.Empty(runner.Invocations);
        Assert.Equal(1, runner.RemainingRecordings);
    }

    [Fact]
    public async Task CancellationMidReplayPropagates()
    {
        var recording = TestSupport.CreateRecording(
            events: [("first", "{}"), ("second", "{}")]);
        var runner = new RecordingReplayExpertRunner([recording]);
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingSink(cancellation, cancelAfterEvents: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
            sink,
            cancellation.Token).AsTask());

        Assert.Single(sink.Delivered);
        Assert.Equal(0, runner.RemainingRecordings);
    }

    [Fact]
    public async Task ConstructorAndExecuteValidateNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RecordingReplayExpertRunner([null!]));
        Assert.Throws<ArgumentNullException>(
            () => new RecordingReplayExpertRunner(null!));

        var runner = new RecordingReplayExpertRunner([]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(
            null!, new RecordingSemanticSink(), TestSupport.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.ExecuteAsync(
            new ExpertInvocationRequest(TestSupport.Contract, null, TestSupport.Json("{}"), "channel"),
            null!,
            TestSupport.CancellationToken).AsTask());
    }

    private sealed class CancellingSink(CancellationTokenSource source, int cancelAfterEvents) : IExpertSemanticEventSink
    {
        public List<string> Delivered { get; } = [];

        public ValueTask WriteAsync(
            ExpertSemanticEvent semanticEvent,
            CancellationToken cancellationToken = default)
        {
            Delivered.Add(semanticEvent.EventType);
            if (Delivered.Count >= cancelAfterEvents)
                source.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
