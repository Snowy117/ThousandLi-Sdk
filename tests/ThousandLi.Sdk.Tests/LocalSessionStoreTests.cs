using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalSessionStoreTests
{
    [Fact]
    public async Task FileStorePersistsStateAndSequencesAcrossInstances()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstStore = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            var firstRuntime = await TestSupport.CreateRuntimeAsync(
                new DelegateBackend(), firstStore, sessionId: TestSupport.SessionId);
            var first = await TestSupport.CollectAsync(firstRuntime.HandleActionAsync(
                TestSupport.Action(), TestSupport.CancellationToken));

            var secondStore = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            var secondRuntime = await TestSupport.CreateRuntimeAsync(
                new DelegateBackend(), secondStore, sessionId: TestSupport.SessionId);
            var second = await TestSupport.CollectAsync(secondRuntime.HandleActionAsync(
                TestSupport.Action(), TestSupport.CancellationToken));

            Assert.Equal("action-00000001", first[0].ActionId.Value);
            Assert.Equal("action-00000002", second[0].ActionId.Value);
            Assert.Equal(2, secondRuntime.CommittedActions.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResetRemovesOnlySelectedSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            var first = Document(new SessionId("first"));
            var second = Document(new SessionId("second"));
            await store.SaveAsync(first, TestSupport.CancellationToken);
            await store.SaveAsync(second, TestSupport.CancellationToken);

            await store.ResetAsync(first.SessionId, TestSupport.CancellationToken);

            Assert.Null(await store.LoadAsync(first.SessionId, TestSupport.CancellationToken));
            Assert.NotNull(await store.LoadAsync(second.SessionId, TestSupport.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InMemoryStoreIsIsolatedAndDisposable()
    {
        var first = new InMemoryLocalSessionStore();
        await first.SaveAsync(Document(TestSupport.SessionId), TestSupport.CancellationToken);

        var second = new InMemoryLocalSessionStore();

        Assert.NotNull(await first.LoadAsync(TestSupport.SessionId, TestSupport.CancellationToken));
        Assert.Null(await second.LoadAsync(TestSupport.SessionId, TestSupport.CancellationToken));
    }

    [Fact]
    public async Task MalformedFileReportsResetGuidance()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            Directory.CreateDirectory(store.ScopeDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(store.ScopeDirectory, "session-1.json"),
                "not-json",
                TestSupport.CancellationToken);

            var exception = await Assert.ThrowsAsync<LocalDataException>(async () =>
                await store.LoadAsync(TestSupport.SessionId, TestSupport.CancellationToken));

            Assert.Contains("unreadable", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Reset", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentStoreInstancesSerializeWritesWithinOneScope()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstStore = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            var secondStore = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = Enumerable.Range(1, 20).Select(index => Task.Run(async () =>
            {
                await start.Task;
                var store = index % 2 == 0 ? firstStore : secondStore;
                await store.SaveAsync(Document(TestSupport.SessionId, index), TestSupport.CancellationToken);
            }, TestSupport.CancellationToken)).ToArray();

            start.SetResult();
            await Task.WhenAll(writes);

            var loaded = await firstStore.LoadAsync(TestSupport.SessionId, TestSupport.CancellationToken);
            Assert.NotNull(loaded);
            Assert.InRange(loaded.CommittedState.GetProperty("turn").GetInt32(), 1, 20);
            Assert.Empty(Directory.EnumerateFiles(firstStore.ScopeDirectory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentFromAnotherSessionIsRejectedWithResetGuidance()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new FileLocalSessionStore("tests_game@1.0.0", "workspace", root);
            await store.SaveAsync(Document(new SessionId("other")), TestSupport.CancellationToken);
            File.Move(
                Path.Combine(store.ScopeDirectory, "other.json"),
                Path.Combine(store.ScopeDirectory, "session-1.json"));

            var exception = await Assert.ThrowsAsync<LocalDataException>(async () =>
                await store.LoadAsync(TestSupport.SessionId, TestSupport.CancellationToken));

            Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Reset", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void UnsafeScopeIsRejected(string packageId)
    {
        Assert.Throws<ArgumentException>(() => new FileLocalSessionStore(packageId, "workspace"));
    }

    private static LocalSessionDocument Document(SessionId sessionId, int turn = 0) => new(
        "tests_game@1.0.0",
        sessionId,
        LocalGameRuntime.MainBranchId,
        headActionId: null,
        TestSupport.Json($"{{\"turn\":{turn}}}"),
        committedActions: []);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thousandli-sdk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
