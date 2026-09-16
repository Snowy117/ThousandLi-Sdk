using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ThousandLi.Testing;

namespace ThousandLi.DevHost;

/// <summary>Storage for semantic expert invocation recordings produced by the Playground.</summary>
public interface IExpertRecordingStore
{
    /// <summary>Saves one recording and returns its recording id. Failures throw <see cref="ExpertRecordingException"/>.</summary>
    ValueTask<string> SaveAsync(ExpertInvocationRecording recording, CancellationToken cancellationToken = default);

    /// <summary>Loads one recording by id, or null when it does not exist.</summary>
    ValueTask<ExpertInvocationRecording?> LoadAsync(string recordingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists saved recordings, oldest first. Any unreadable or incompatible recording file fails
    /// the listing with an explicit error instead of being silently skipped.
    /// </summary>
    ValueTask<IReadOnlyList<ExpertInvocationRecordingSummary>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>The list projection of a saved recording.</summary>
public sealed record ExpertInvocationRecordingSummary(
    string RecordingId,
    string ContractId,
    string? Executor,
    string? ScenarioId,
    string? ChannelKey,
    string Status,
    DateTimeOffset RecordedAtUtc,
    long DurationMs);

/// <summary>The <c>--ephemeral</c> recording store: recordings live only for the process lifetime.</summary>
public sealed class InMemoryExpertRecordingStore : IExpertRecordingStore
{
    private readonly ConcurrentDictionary<string, ExpertInvocationRecording> _recordings = new(StringComparer.Ordinal);
    private long _sequence;

    public ValueTask<string> SaveAsync(
        ExpertInvocationRecording recording,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        cancellationToken.ThrowIfCancellationRequested();
        var recordingId = ExpertRecordingIds.Create(recording.ChannelKey, ref _sequence);
        _recordings[recordingId] = recording;
        return ValueTask.FromResult(recordingId);
    }

    public ValueTask<ExpertInvocationRecording?> LoadAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_recordings.GetValueOrDefault(recordingId));
    }

    public ValueTask<IReadOnlyList<ExpertInvocationRecordingSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OrderedSummaries(_recordings));
    }

    private static IReadOnlyList<ExpertInvocationRecordingSummary> OrderedSummaries(
        IEnumerable<KeyValuePair<string, ExpertInvocationRecording>> recordings) =>
        [.. recordings
            .Select(pair => ExpertRecordingSummaries.Create(pair.Key, pair.Value))
            .OrderBy(summary => summary.RecordedAtUtc)
            .ThenBy(summary => summary.RecordingId, StringComparer.Ordinal)];
}

/// <summary>
/// File-backed recording store under the DevHost data root, scoped per workspace so parallel
/// DevHost sessions never mix recordings. Each recording is one file, written through a
/// temporary file with an atomic replace (the <see cref="FileLocalSessionStore"/> precedent), so
/// concurrent invocations never contend for shared files and a crashed write never leaves a
/// partially visible recording behind.
/// </summary>
public sealed class FileExpertRecordingStore : IExpertRecordingStore
{
    public string RecordingsDirectory { get; }
    private long _sequence;

    public FileExpertRecordingStore(string workspaceId, string? userDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateSegment(workspaceId, nameof(workspaceId));
        RecordingsDirectory = Path.Combine(
            ExpertComposition.ResolveDataRoot(userDataRoot), "recordings", workspaceId);
    }

    public async ValueTask<string> SaveAsync(
        ExpertInvocationRecording recording,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var recordingId = ExpertRecordingIds.Create(recording.ChannelKey, ref _sequence);
        try
        {
            Directory.CreateDirectory(RecordingsDirectory);
            var path = GetRecordingPath(recordingId);
            var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var bytes = Encoding.UTF8.GetBytes(recording.ToJson());
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return recordingId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ExpertRecordingException(
                $"Failed to save expert recording '{recordingId}' under '{RecordingsDirectory}': {exception.Message}",
                exception);
        }
    }

    public async ValueTask<ExpertInvocationRecording?> LoadAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        var path = GetRecordingPath(recordingId);
        if (!File.Exists(path)) return null;
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExpertRecordingException($"Expert recording '{path}' is unreadable: {exception.Message}", exception);
        }

        return LoadText(path, json);
    }

    public async ValueTask<IReadOnlyList<ExpertInvocationRecordingSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RecordingsDirectory)) return [];
        var summaries = new List<ExpertInvocationRecordingSummary>();
        foreach (var path in Directory.EnumerateFiles(RecordingsDirectory, "*.json"))
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExpertRecordingException($"Expert recording '{path}' is unreadable: {exception.Message}", exception);
            }

            var recording = LoadText(path, json);
            summaries.Add(ExpertRecordingSummaries.Create(Path.GetFileNameWithoutExtension(path), recording));
        }

        return [.. summaries
            .OrderBy(summary => summary.RecordedAtUtc)
            .ThenBy(summary => summary.RecordingId, StringComparer.Ordinal)];
    }

    private static ExpertInvocationRecording LoadText(string path, string json)
    {
        try
        {
            return ExpertInvocationRecording.FromJson(json);
        }
        catch (ExpertRecordingException exception)
        {
            throw new ExpertRecordingException($"Expert recording '{path}' could not be loaded: {exception.Message}", exception);
        }
    }

    private string GetRecordingPath(string recordingId)
    {
        ValidateSegment(recordingId, nameof(recordingId));
        return Path.Combine(RecordingsDirectory, recordingId + ".json");
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value is "." or ".." ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Recording ids and workspace ids must be a single safe path segment.", parameterName);
        }
    }
}

internal static class ExpertRecordingSummaries
{
    public static ExpertInvocationRecordingSummary Create(string recordingId, ExpertInvocationRecording recording) => new(
        recordingId,
        recording.ContractId,
        recording.Executor,
        recording.ScenarioId,
        recording.ChannelKey,
        recording.Terminal.Status,
        recording.RecordedAtUtc,
        recording.DurationMs);
}

internal static class ExpertRecordingIds
{
    public static string Create(string? channelKey, ref long sequence)
    {
        var correlation = string.IsNullOrWhiteSpace(channelKey)
            ? $"rec-{Interlocked.Increment(ref sequence):D8}"
            : channelKey;
        if (correlation.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            correlation = $"rec-{Interlocked.Increment(ref sequence):D8}";
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "_" + correlation;
    }
}
