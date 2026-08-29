using ThousandLi.Contracts;
using ThousandLi.DevHost;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts;
using ThousandLi.ExpertContracts.Narration;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class PlaygroundServiceRecordingTests
{
    private const string MultiEventScenario = """
        [
          {
            "contract": {
              "id": "tests/playground",
              "version": { "major": 1, "minor": 0 },
              "fingerprint": "playground-fp-1"
            },
            "scenarioId": "multi",
            "events": [
              { "eventType": "first", "payload": { "n": 1 } },
              { "eventType": "second", "payload": { "n": 2 } },
              { "eventType": "third", "payload": { "n": 3 } }
            ],
            "result": { "done": true }
          }
        ]
        """;

    private static ExpertContractRegistry Registry() =>
        new([typeof(AbstractNarratorExpert).Assembly]);

    private static ScriptedFakeExpertExecutor MultiEventFake(DirectoryInfo root)
    {
        var scenarioPath = Path.Combine(root.FullName, "scenarios.json");
        File.WriteAllText(scenarioPath, MultiEventScenario);
        return ScriptedFakeExpertExecutor.Load(scenarioPath);
    }

    [Fact]
    public async Task CancelledMidStreamInvocationIsRecordedAsAbortedWithDeliveredEventsOnly()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var store = new InMemoryExpertRecordingStore();
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), store);
            using var cancellation = new CancellationTokenSource();
            var sink = new CancellingSink(cancellation, cancelAfterEvents: 1);

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi", Record: true),
                sink,
                cancellation.Token);

            Assert.Equal(PlaygroundService.StatusAborted, outcome.Diagnostics.Status);
            Assert.Null(outcome.Result);
            Assert.NotNull(outcome.Error);
            Assert.Equal("first", Assert.Single(sink.Delivered));

            Assert.NotNull(outcome.Diagnostics.RecordingId);
            var recording = await store.LoadAsync(outcome.Diagnostics.RecordingId!, TestSupport.CancellationToken);
            Assert.NotNull(recording);
            Assert.Equal(ExpertRecordingTerminal.Aborted, recording.Terminal.Status);
            Assert.Null(recording.Terminal.Output);
            Assert.NotNull(recording.Terminal.Error);
            Assert.Equal("first", Assert.Single(recording.Events).EventType);
            Assert.Equal(PlaygroundService.StatusAborted, Assert.Single(playground.History).Status);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PreCancelledInvocationIsRecordedAsAbortedWithNoEvents()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var store = new InMemoryExpertRecordingStore();
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), store);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi", Record: true),
                new RecordingSemanticSink(),
                cancelled.Token);

            Assert.Equal(PlaygroundService.StatusAborted, outcome.Diagnostics.Status);
            var recording = await store.LoadAsync(outcome.Diagnostics.RecordingId!, TestSupport.CancellationToken);
            Assert.NotNull(recording);
            Assert.Empty(recording.Events);
            Assert.Equal(ExpertRecordingTerminal.Aborted, recording.Terminal.Status);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RecordingSaveFailureSurfacesThroughDiagnosticsWithoutFailingTheInvocation()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), new FailingRecordingStore());

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi", Record: true),
                new RecordingSemanticSink(),
                TestSupport.CancellationToken);

            Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status);
            Assert.NotNull(outcome.Result);
            Assert.Null(outcome.Diagnostics.RecordingId);
            Assert.Contains("Recording failed:", outcome.Diagnostics.RecordingError, StringComparison.Ordinal);
            var historyEntry = Assert.Single(playground.History);
            Assert.Contains("Recording failed:", historyEntry.RecordingError, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnrecordedInvocationsProduceNoRecording()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var store = new InMemoryExpertRecordingStore();
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), store);

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi"),
                new RecordingSemanticSink(),
                TestSupport.CancellationToken);

            Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status);
            Assert.Null(outcome.Diagnostics.RecordingId);
            Assert.Null(outcome.Diagnostics.RecordingError);
            Assert.Empty(await store.ListAsync(TestSupport.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReplayUsesTheRecordedScenarioAndNeverRecordsItself()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var store = new InMemoryExpertRecordingStore();
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), store);
            var recorded = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi", Record: true),
                new RecordingSemanticSink(),
                TestSupport.CancellationToken);
            Assert.NotNull(recorded.Diagnostics.RecordingId);

            var report = await playground.ReplayAsync(
                new PlaygroundReplayCommand(recorded.Diagnostics.RecordingId!, DevHostOptions.FakeExecutorName, StrictPayloads: true),
                TestSupport.CancellationToken);

            Assert.True(report.Matches);
            Assert.True(report.StrictPayloads);
            Assert.Empty(report.Divergences);
            Assert.Equal(PlaygroundService.StatusCommitted, report.Diagnostics.Status);
            var recordings = await store.ListAsync(TestSupport.CancellationToken);
            Assert.Single(recordings);
            Assert.Equal(2, playground.History.Count);
            Assert.Null(playground.History[1].RecordingId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailingMidStreamInvocationIsRecordedAsErrorWithDeliveredEventsOnly()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var store = new InMemoryExpertRecordingStore();
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), store);

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand("tests/playground", DevHostOptions.FakeExecutorName, TestSupport.Json("{}"), "multi", Record: true),
                new ThrowingSink(afterEvents: 1),
                TestSupport.CancellationToken);

            Assert.Equal(PlaygroundService.StatusError, outcome.Diagnostics.Status);
            Assert.Null(outcome.Result);
            Assert.NotNull(outcome.Error);

            Assert.NotNull(outcome.Diagnostics.RecordingId);
            var recording = await store.LoadAsync(outcome.Diagnostics.RecordingId!, TestSupport.CancellationToken);
            Assert.NotNull(recording);
            Assert.Equal(ExpertRecordingTerminal.ErrorStatus, recording.Terminal.Status);
            Assert.Null(recording.Terminal.Output);
            Assert.NotNull(recording.Terminal.Error);
            Assert.Equal("first", Assert.Single(recording.Events).EventType);
            var historyEntry = Assert.Single(playground.History);
            Assert.Equal(PlaygroundService.StatusError, historyEntry.Status);
            Assert.NotNull(historyEntry.Error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PlaygroundServesBothExecutorsForALocalContractAndInvokesLocally()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var narrator = AbstractNarratorExpert.Descriptor;
            var scenarioPath = Path.Combine(root.FullName, "scenarios.json");
            await File.WriteAllTextAsync(scenarioPath, $$"""
                [
                  {
                    "contract": {
                      "id": "{{narrator.Id}}",
                      "version": {
                        "major": {{narrator.Version.Major}},
                        "minor": {{narrator.Version.Minor}}
                      },
                      "fingerprint": "{{narrator.Fingerprint}}"
                    },
                    "scenarioId": "narrate",
                    "events": [],
                    "result": { "text": "fake" }
                  }
                ]
                """, TestSupport.CancellationToken);
            using var package = ExpertPackageLoader.Load(Path.Combine(
                TestSupport.FindRepositoryRoot(),
                "tests", "ThousandLi.LocalExpertFixture", "bin", "Debug", "net10.0", "PackageArtifact"));
            var playground = new PlaygroundService(
                ScriptedFakeExpertExecutor.Load(scenarioPath),
                new LocalExpertExecutor(
                    Registry(),
                    [package],
                    null,
                    new LocalExpertExecutorOptions(
                        new RecordedBasicAi(
                            ["test-model"],
                            [],
                            [new RecordedRuntimeBasicAiInteraction(
                                "test-model",
                                streamEvents: [new BasicAiJsonStreamEvent(
                                    JsonStreamEvent.StringChunk("/narrative", "Hello playground"))])]),
                        new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "Curious explorer"))),
                Registry(),
                new InMemoryExpertRecordingStore());

            var contracts = await playground.GetContractsAsync(TestSupport.CancellationToken);
            var entry = Assert.Single(contracts);
            Assert.Equal(narrator.Id, entry.ContractId);
            Assert.Equal(
                [DevHostOptions.FakeExecutorName, DevHostOptions.LocalExecutorName],
                entry.Executors);
            Assert.Equal(["thousandli_local-expert-fixture@0.1.0"], entry.ExpertPackageIds);

            var outcome = await playground.InvokeAsync(
                new PlaygroundInvokeCommand(
                    narrator.Id,
                    DevHostOptions.LocalExecutorName,
                    TestSupport.Json("""{"turn":1,"player":"Creator","action":{"choice":"advance"}}""")),
                new RecordingSemanticSink(),
                TestSupport.CancellationToken);

            Assert.Equal(PlaygroundService.StatusCommitted, outcome.Diagnostics.Status);
            Assert.Equal("Hello playground", outcome.Result!.Output.GetProperty("text").GetString());
            var historyEntry = Assert.Single(playground.History);
            Assert.Equal(DevHostOptions.LocalExecutorName, historyEntry.Executor);
            Assert.Equal(PlaygroundService.StatusCommitted, historyEntry.Status);
            Assert.NotNull(historyEntry.InvocationId);
            Assert.Matches("""^local-\d{8}$""", historyEntry.InvocationId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReplayRejectsUnknownRecordingsWithAnActionableDiagnostic()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-playground-tests-");
        try
        {
            var playground = new PlaygroundService(MultiEventFake(root), null, Registry(), new InMemoryExpertRecordingStore());

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => playground.ReplayAsync(
                    new PlaygroundReplayCommand("no-such-recording", DevHostOptions.FakeExecutorName),
                    TestSupport.CancellationToken));

            Assert.Contains("no-such-recording", exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
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

    private sealed class ThrowingSink(int afterEvents) : IExpertSemanticEventSink
    {
        private int _delivered;

        public ValueTask WriteAsync(
            ExpertSemanticEvent semanticEvent,
            CancellationToken cancellationToken = default)
        {
            return ++_delivered > afterEvents
                ? throw new InvalidOperationException("simulated sink failure")
                : ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRecordingStore : IExpertRecordingStore
    {
        public ValueTask<string> SaveAsync(
            ExpertInvocationRecording recording,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<string>(new ExpertRecordingException("simulated recording store failure"));

        public ValueTask<ExpertInvocationRecording?> LoadAsync(
            string recordingId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ExpertInvocationRecording?>(null);

        public ValueTask<IReadOnlyList<ExpertInvocationRecordingSummary>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ExpertInvocationRecordingSummary>>([]);
    }
}
