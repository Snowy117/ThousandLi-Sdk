using ThousandLi.DevHost;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertRecordingStoreTests
{
    [Fact]
    public async Task InMemoryStoreSavesListsAndLoadsPerProcessLifetime()
    {
        var store = new InMemoryExpertRecordingStore();
        var first = TestSupport.CreateRecording(
            channelKey: "channel-a",
            recordedAtUtc: new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero));
        var second = TestSupport.CreateRecording(
            channelKey: "channel-b",
            recordedAtUtc: new DateTimeOffset(2026, 8, 24, 8, 0, 1, TimeSpan.Zero));

        var firstId = await store.SaveAsync(first, TestSupport.CancellationToken);
        var secondId = await store.SaveAsync(second, TestSupport.CancellationToken);

        var summaries = await store.ListAsync(TestSupport.CancellationToken);
        Assert.Equal(2, summaries.Count);
        Assert.Equal(firstId, summaries[0].RecordingId);
        Assert.Equal(secondId, summaries[1].RecordingId);
        Assert.Equal(first.Contract.Id, summaries[0].ContractId);
        Assert.Equal("1.0", summaries[0].ContractVersion);
        Assert.Equal(first.Contract.Fingerprint, summaries[0].Fingerprint);
        Assert.Equal("fake", summaries[0].Executor);
        Assert.Equal("advance", summaries[0].ScenarioId);
        Assert.Equal("channel-a", summaries[0].ChannelKey);
        Assert.Equal(ExpertRecordingTerminal.Committed, summaries[0].Status);
        Assert.Equal(42, summaries[0].DurationMs);

        var loaded = await store.LoadAsync(firstId, TestSupport.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(first.ChannelKey, loaded.ChannelKey);
        Assert.Null(await store.LoadAsync("missing", TestSupport.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.LoadAsync(" ", TestSupport.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SaveAsync(null!, TestSupport.CancellationToken).AsTask());
    }

    [Fact]
    public async Task FileStoreSaveLoadRoundTripsAndStoresOneFilePerInvocation()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-a", root.FullName);
            var recording = TestSupport.CreateRecording(
                channelKey: "playground-00000001",
                invocationId: "fake-00000009",
                executor: "local",
                expertPackageId: "official_dreamseek@1.0.0");

            var recordingId = await store.SaveAsync(recording, TestSupport.CancellationToken);

            Assert.Equal(
                Path.Combine(root.FullName, "recordings", "workspace-a"),
                store.RecordingsDirectory);
            var files = Directory.GetFiles(store.RecordingsDirectory);
            var file = Assert.Single(files);
            Assert.Equal(recordingId + ".json", Path.GetFileName(file));

            var loaded = await store.LoadAsync(recordingId, TestSupport.CancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal(recording.Contract.Id, loaded.Contract.Id);
            Assert.Equal(recording.ChannelKey, loaded.ChannelKey);
            Assert.Equal(recording.InvocationId, loaded.InvocationId);
            Assert.Equal("local", loaded.Executor);
            Assert.Equal("official_dreamseek@1.0.0", loaded.ExpertPackageId);
            Assert.Equal(ExpertRecordingTerminal.Committed, loaded.Terminal.Status);

            Assert.Null(await store.LoadAsync("missing", TestSupport.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreAtomicReplaceLeavesNoTemporaryFiles()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-b", root.FullName);

            for (var index = 0; index < 5; index++)
                await store.SaveAsync(
                    TestSupport.CreateRecording(channelKey: $"channel-{index:D2}"),
                    TestSupport.CancellationToken);

            var files = Directory.GetFiles(store.RecordingsDirectory);
            Assert.Equal(5, files.Length);
            Assert.All(files, file => Assert.EndsWith(".json", file, StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(store.RecordingsDirectory, "*.tmp"));
            Assert.Equal(5, (await store.ListAsync(TestSupport.CancellationToken)).Count);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreChannelLessSavesWithinTheSameSecondKeepDistinctRecordings()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-c", root.FullName);

            var firstId = await store.SaveAsync(TestSupport.CreateRecording(channelKey: null), TestSupport.CancellationToken);
            var secondId = await store.SaveAsync(TestSupport.CreateRecording(channelKey: null), TestSupport.CancellationToken);

            Assert.NotEqual(firstId, secondId);
            var summaries = await store.ListAsync(TestSupport.CancellationToken);
            Assert.Equal(2, summaries.Count);
            Assert.Equal([firstId, secondId], summaries.Select(summary => summary.RecordingId).Order(StringComparer.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreConcurrentSavesDoNotCorruptRecordings()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-d", root.FullName);
            const int count = 8;

            var recordingIds = await Task.WhenAll(Enumerable.Range(0, count).Select(index => store.SaveAsync(
                TestSupport.CreateRecording(
                    channelKey: $"playground-{index:D8}",
                    events: [("narrative", $$"""{"text":"story {{index}}"}""")]),
                TestSupport.CancellationToken).AsTask()));

            Assert.Equal(count, recordingIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(count, Directory.GetFiles(store.RecordingsDirectory, "*.json").Length);
            Assert.Empty(Directory.GetFiles(store.RecordingsDirectory, "*.tmp"));
            var summaries = await store.ListAsync(TestSupport.CancellationToken);
            Assert.Equal(count, summaries.Count);
            foreach (var recordingId in recordingIds)
            {
                var loaded = await store.LoadAsync(recordingId, TestSupport.CancellationToken);
                Assert.NotNull(loaded);
                Assert.Equal(ExpertRecordingTerminal.Committed, loaded.Terminal.Status);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreChannelKeysWithUnsafeCharactersFallBackToSafeIds()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-e", root.FullName);

            var recordingId = await store.SaveAsync(
                TestSupport.CreateRecording(channelKey: "bad/key:with*chars"),
                TestSupport.CancellationToken);

            Assert.Matches("""^\d{8}T\d{6}Z_rec-\d{8}$""", recordingId);
            Assert.DoesNotContain("/", recordingId, StringComparison.Ordinal);
            Assert.DoesNotContain(":", recordingId, StringComparison.Ordinal);
            Assert.NotNull(await store.LoadAsync(recordingId, TestSupport.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreRejectsUnsafeWorkspaceAndRecordingIdSegments()
    {
        Assert.Throws<ArgumentException>(() => new FileExpertRecordingStore(".."));
        Assert.Throws<ArgumentException>(() => new FileExpertRecordingStore("a/b"));
        Assert.Throws<ArgumentException>(() => new FileExpertRecordingStore(" "));

        var store = new FileExpertRecordingStore("workspace-f", Path.GetTempPath());
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.LoadAsync("../evil", TestSupport.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.LoadAsync("a/b", TestSupport.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.LoadAsync(".", TestSupport.CancellationToken).AsTask());
    }

    [Fact]
    public async Task FileStoreListFailsOnMalformedRecordingWithPathAndResetGuidance()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-g", root.FullName);
            await store.SaveAsync(TestSupport.CreateRecording(), TestSupport.CancellationToken);
            var malformedPath = Path.Combine(store.RecordingsDirectory, "malformed.json");
            await File.WriteAllTextAsync(malformedPath, "{ not json", TestSupport.CancellationToken);

            var exception = await Assert.ThrowsAsync<ExpertRecordingException>(
                () => store.ListAsync(TestSupport.CancellationToken).AsTask());

            Assert.Contains(malformedPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Delete the recording file", exception.Message, StringComparison.Ordinal);

            var loadException = await Assert.ThrowsAsync<ExpertRecordingException>(
                () => store.LoadAsync("malformed", TestSupport.CancellationToken).AsTask());
            Assert.Contains("malformed.json", loadException.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreRestoresRecordingsAcrossRestart()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var firstSession = new FileExpertRecordingStore("workspace-i", root.FullName);
            var firstId = await firstSession.SaveAsync(
                TestSupport.CreateRecording(channelKey: "playground-00000001"),
                TestSupport.CancellationToken);
            await firstSession.SaveAsync(
                TestSupport.CreateRecording(channelKey: "playground-00000002"),
                TestSupport.CancellationToken);

            var restarted = new FileExpertRecordingStore("workspace-i", root.FullName);
            var summaries = await restarted.ListAsync(TestSupport.CancellationToken);

            Assert.Equal(2, summaries.Count);
            var loaded = await restarted.LoadAsync(firstId, TestSupport.CancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal("playground-00000001", loaded.ChannelKey);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreCancelledSaveLeavesNoPartialRecording()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-j", root.FullName);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
                TestSupport.CreateRecording(), cancelled.Token).AsTask());

            Assert.Empty(Directory.GetFiles(store.RecordingsDirectory, "*.json"));
            Assert.Empty(Directory.GetFiles(store.RecordingsDirectory, "*.tmp"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreListFailsOnIncompatibleFormatMajor()
    {
        var root = Directory.CreateTempSubdirectory("thousandli-recording-tests-");
        try
        {
            var store = new FileExpertRecordingStore("workspace-h", root.FullName);
            var incompatible = TestSupport.CreateRecording()
                .ToJson()
                .Replace(
                    $"""
                     "formatMajor": {ExpertInvocationRecording.CurrentFormatMajor}
                     """,
                    """
                    "formatMajor": 7
                    """);
            await File.WriteAllTextAsync(
                Path.Combine(Directory.CreateDirectory(store.RecordingsDirectory).FullName, "future.json"),
                incompatible,
                TestSupport.CancellationToken);

            var exception = await Assert.ThrowsAsync<ExpertRecordingException>(
                () => store.ListAsync(TestSupport.CancellationToken).AsTask());

            Assert.Contains("format major 7", exception.Message, StringComparison.Ordinal);
            Assert.Contains("delete it and record a new one", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
