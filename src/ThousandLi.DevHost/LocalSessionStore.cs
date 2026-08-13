using System.Collections.Concurrent;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

public sealed record LocalSessionDocument
{
    public const int CurrentStorageMajor = 1;

    public LocalSessionDocument(
        string packageId,
        SessionId sessionId,
        BranchId branchId,
        ActionId? headActionId,
        JsonElement committedState,
        IReadOnlyList<CommittedActionRecord> committedActions,
        long nextActionSequence = 1,
        long nextRunSequence = 1,
        int storageMajor = CurrentStorageMajor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (committedState.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Committed state must be a JSON object.", nameof(committedState));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextActionSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextRunSequence);
        PackageId = packageId;
        SessionId = sessionId;
        BranchId = branchId;
        HeadActionId = headActionId;
        CommittedState = committedState.Clone();
        CommittedActions = [.. committedActions ?? throw new ArgumentNullException(nameof(committedActions))];
        NextActionSequence = nextActionSequence;
        NextRunSequence = nextRunSequence;
        StorageMajor = storageMajor;
    }

    public int StorageMajor { get; }
    public string PackageId { get; }
    public SessionId SessionId { get; }
    public BranchId BranchId { get; }
    public ActionId? HeadActionId { get; }
    public JsonElement CommittedState { get; }
    public IReadOnlyList<CommittedActionRecord> CommittedActions { get; }
    public long NextActionSequence { get; }
    public long NextRunSequence { get; }

    public LocalSessionDocument Copy() => new(
        PackageId, SessionId, BranchId, HeadActionId, CommittedState, CommittedActions,
        NextActionSequence, NextRunSequence, StorageMajor);

    public LocalSessionDocument WithReservedSequences() => new(
        PackageId, SessionId, BranchId, HeadActionId, CommittedState, CommittedActions,
        checked(NextActionSequence + 1), checked(NextRunSequence + 1), StorageMajor);
}

public interface ILocalSessionStore
{
    ValueTask<LocalSessionDocument?> LoadAsync(SessionId sessionId, CancellationToken cancellationToken = default);
    ValueTask SaveAsync(LocalSessionDocument session, CancellationToken cancellationToken = default);
    ValueTask ResetAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryLocalSessionStore : ILocalSessionStore
{
    private readonly ConcurrentDictionary<SessionId, LocalSessionDocument> _sessions = new();

    public ValueTask<LocalSessionDocument?> LoadAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session.Copy() : null);
    }

    public ValueTask SaveAsync(LocalSessionDocument session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        _sessions[session.SessionId] = session.Copy();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed class LocalDataException : InvalidOperationException
{
    public LocalDataException(string message)
        : base(message)
    {
    }

    public LocalDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FileLocalSessionStore : ILocalSessionStore
{
    private static readonly JsonSerializerOptions SJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SScopeGates =
        new(StringComparer.Ordinal);

    private readonly string _packageId;
    private readonly SemaphoreSlim _gate;

    public FileLocalSessionStore(string packageId, string workspaceId, string? userDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateScopeSegment(packageId, nameof(packageId));
        ValidateScopeSegment(workspaceId, nameof(workspaceId));
        var root = userDataRoot ?? GetDefaultUserDataRoot();
        _packageId = packageId;
        ScopeDirectory = Path.Combine(Path.GetFullPath(root), packageId, workspaceId);
        _gate = SScopeGates.GetOrAdd(ScopeDirectory, static _ => new SemaphoreSlim(1, 1));
    }

    public string ScopeDirectory { get; }

    public async ValueTask<LocalSessionDocument?> LoadAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scopeLock = await AcquireScopeLockAsync(cancellationToken).ConfigureAwait(false);
            var path = GetSessionPath(sessionId);
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var session = await JsonSerializer.DeserializeAsync<LocalSessionDocument>(stream, SJsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new JsonException("Session document was empty.");
            if (session.StorageMajor != LocalSessionDocument.CurrentStorageMajor)
            {
                throw new LocalDataException(
                    $"Local session data '{path}' uses storage major {session.StorageMajor}; DevHost supports " +
                    $"{LocalSessionDocument.CurrentStorageMajor}. Reset this scoped session data before retrying.");
            }
            if (session.SessionId != sessionId || !string.Equals(session.PackageId, _packageId, StringComparison.Ordinal))
            {
                throw new LocalDataException(
                    $"Local session data '{path}' does not match its requested session and package scope. " +
                    "Reset this scoped session data before retrying.");
            }
            return session.Copy();
        }
        catch (LocalDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            var path = GetSessionPath(sessionId);
            throw new LocalDataException(
                $"Local session data '{path}' is unreadable. Reset this scoped session data before retrying.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(LocalSessionDocument session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.PackageId, _packageId, StringComparison.Ordinal))
            throw new ArgumentException("Session package does not match the local data scope.", nameof(session));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            await using var scopeLock = await AcquireScopeLockAsync(cancellationToken).ConfigureAwait(false);
            var path = GetSessionPath(session.SessionId);
            temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, session, SJsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (temporaryPath is not null)
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scopeLock = await AcquireScopeLockAsync(cancellationToken).ConfigureAwait(false);
            File.Delete(GetSessionPath(sessionId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetSessionPath(SessionId sessionId)
    {
        ValidateScopeSegment(sessionId.Value, nameof(sessionId));
        return Path.Combine(ScopeDirectory, sessionId.Value + ".json");
    }

    private async ValueTask<FileStream> AcquireScopeLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ScopeDirectory);
        var lockPath = Path.Combine(ScopeDirectory, ".store.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string GetDefaultUserDataRoot()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDirectory, "ThousandLi", "DevHost", "v1");
    }

    private static void ValidateScopeSegment(string value, string parameterName)
    {
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value is "." or ".." ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Local data scope must be a single safe path segment.", parameterName);
        }
    }
}
