using System.Collections.Concurrent;
using System.Diagnostics;
using JetBrains.Annotations;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts;
using ThousandLi.Testing;

namespace ThousandLi.DevHost;

/// <summary>An independent Playground invocation command, not routed through a game action.</summary>
public sealed record PlaygroundInvokeCommand(
    string ContractId,
    string Executor,
    JsonElement Input,
    string? ScenarioId = null,
    string? ExpertPackageId = null,
    bool Record = false);

/// <summary>Replays a saved recording through a chosen executor and compares it deterministically.</summary>
public sealed record PlaygroundReplayCommand(
    string RecordingId,
    string Executor,
    string? ExpertPackageId = null,
    bool StrictPayloads = false,
    string? ScenarioId = null);

/// <summary>A contract the Playground can invoke, with the executors and Expert Packages serving it.</summary>
/// <remarks>Serialized to the Playground UI through <c>Results.Json</c>; members are consumed client-side.</remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PlaygroundContractInfo(
    string ContractId,
    string Version,
    string Fingerprint,
    IReadOnlyList<string> Executors,
    IReadOnlyList<string> ExpertPackageIds);

/// <summary>
/// The minimal diagnostics set recorded for every Playground invocation: the correlation key
/// (channel key plus invocation id when available), duration, termination status, and the
/// semantic recording outcome when the invocation opted into recording.
/// </summary>
public sealed record PlaygroundInvocationDiagnostics(
    string ChannelKey,
    string? InvocationId,
    string ContractId,
    string Executor,
    DateTimeOffset StartedUtc,
    long DurationMs,
    string Status,
    string? Error,
    string? RecordingId = null,
    string? RecordingError = null);

/// <summary>The terminal outcome of a Playground invocation together with its diagnostics.</summary>
public sealed record PlaygroundInvocationOutcome(
    PlaygroundInvocationDiagnostics Diagnostics,
    ExpertInvocationResult? Result,
    Exception? Error);

/// <summary>The layered deterministic comparison of one replayed invocation against its recording.</summary>
public sealed record PlaygroundReplayReport(
    PlaygroundInvocationDiagnostics Diagnostics,
    bool Matches,
    bool StrictPayloads,
    IReadOnlyList<RecordingComparisonDivergence> Divergences);

public sealed class PlaygroundService(
    ScriptedFakeExpertExecutor fake,
    LocalExpertExecutor? local,
    ExpertContractRegistry registry,
    IExpertRecordingStore recordings,
    ILogger? logger = null)
{
    public const string StatusCommitted = "committed";
    public const string StatusAborted = "aborted";
    public const string StatusError = "error";
    private const int HistoryLimit = 100;

    private readonly ScriptedFakeExpertExecutor _fake = fake ?? throw new ArgumentNullException(nameof(fake));
    private readonly ExpertContractRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IExpertRecordingStore _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ConcurrentQueue<PlaygroundInvocationDiagnostics> _history = new();
    private long _sequence;

    public IReadOnlyList<PlaygroundContractInfo> GetContracts()
    {
        var contracts = new Dictionary<string, PlaygroundContractInfo>(StringComparer.Ordinal);
        foreach (var descriptor in _fake.Contracts)
            contracts.Add(descriptor.Id, new PlaygroundContractInfo(
                descriptor.Id,
                descriptor.Version.ToString(),
                descriptor.Fingerprint,
                [DevHostOptions.FakeExecutorName],
                []));
        foreach (var descriptor in local?.ContractDescriptors ?? [])
        {
            var executors = new List<string>();
            if (contracts.TryGetValue(descriptor.Id, out var fakeEntry))
                executors.AddRange(fakeEntry.Executors);
            executors.Add(DevHostOptions.LocalExecutorName);
            contracts[descriptor.Id] = new PlaygroundContractInfo(
                descriptor.Id,
                descriptor.Version.ToString(),
                descriptor.Fingerprint,
                [.. executors.Order(StringComparer.Ordinal)],
                local?.PackageIdsForContract(descriptor.Id) ?? []);
        }

        return [.. contracts.Values.OrderBy(contract => contract.ContractId, StringComparer.Ordinal)];
    }

    public IReadOnlyList<PlaygroundInvocationDiagnostics> History
    {
        get
        {
            var entries = _history.ToArray();
            return [.. entries.OrderBy(entry => entry.StartedUtc).ThenBy(entry => entry.ChannelKey, StringComparer.Ordinal)];
        }
    }

    public ValueTask<IReadOnlyList<ExpertInvocationRecordingSummary>> ListRecordingsAsync(
        CancellationToken cancellationToken = default) =>
        _recordings.ListAsync(cancellationToken);

    public ValueTask<ExpertInvocationRecording?> LoadRecordingAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        return _recordings.LoadAsync(recordingId, cancellationToken);
    }

    public async Task<PlaygroundInvocationOutcome> InvokeAsync(
        PlaygroundInvokeCommand command,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(events);
        var descriptor = ResolveDescriptor(command);
        var channelKey = $"playground-{Interlocked.Increment(ref _sequence):D8}";
        var request = new ExpertInvocationRequest(descriptor, command.ScenarioId, command.Input, channelKey);
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var collector = command.Record ? new RecordingSemanticEventSink(events) : null;
        try
        {
            var result = command.Executor == DevHostOptions.LocalExecutorName
                ? await local!.ExecuteAsync(request, collector ?? events, command.ExpertPackageId, cancellationToken)
                    .ConfigureAwait(false)
                : await _fake.ExecuteAsync(request, collector ?? events, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var (persistRecordingId, persistRecordingError) = await PersistRecordingAsync(
                descriptor,
                command,
                channelKey,
                result.InvocationId,
                collector?.Events ?? [],
                new ExpertRecordingTerminal(ExpertRecordingTerminal.Committed, result.Output),
                started,
                stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            return Record(new PlaygroundInvocationDiagnostics(
                channelKey, result.InvocationId, descriptor.Id, command.Executor, started,
                stopwatch.ElapsedMilliseconds, StatusCommitted, null,
                persistRecordingId, persistRecordingError), result, null);
        }
        catch (OperationCanceledException exception)
        {
            stopwatch.Stop();
            var (persistRecordingId, persistRecordingError) = await PersistRecordingAsync(
                descriptor,
                command,
                channelKey,
                null,
                collector?.Events ?? [],
                new ExpertRecordingTerminal(ExpertRecordingTerminal.Aborted, error: exception.Message),
                started,
                stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            return Record(new PlaygroundInvocationDiagnostics(
                channelKey, null, descriptor.Id, command.Executor, started,
                stopwatch.ElapsedMilliseconds, StatusAborted, exception.Message,
                persistRecordingId, persistRecordingError), null, exception);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                exception,
                "Playground invocation {ChannelKey} on contract {ContractId} through executor {Executor} failed.",
                channelKey,
                descriptor.Id,
                command.Executor);
            var (persistRecordingId, persistRecordingError) = await PersistRecordingAsync(
                descriptor,
                command,
                channelKey,
                null,
                collector?.Events ?? [],
                new ExpertRecordingTerminal(ExpertRecordingTerminal.ErrorStatus, error: exception.Message),
                started,
                stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            return Record(new PlaygroundInvocationDiagnostics(
                channelKey, null, descriptor.Id, command.Executor, started,
                stopwatch.ElapsedMilliseconds, StatusError, exception.Message,
                persistRecordingId, persistRecordingError), null, exception);
        }
    }

    /// <summary>
    /// Replays a saved recording through the chosen executor with the recorded input and compares
    /// the live invocation against the recording using layered determinism (exact payloads only in
    /// strict mode). Replay invocations are never recorded again.
    /// </summary>
    public async Task<PlaygroundReplayReport> ReplayAsync(
        PlaygroundReplayCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RecordingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Executor);
        var expected = await _recordings.LoadAsync(command.RecordingId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"Recording '{command.RecordingId}' does not exist. List available recordings via the playground recordings endpoint.");
        var scenarioId = command.ScenarioId ?? expected.ScenarioId;
        var invokeCommand = new PlaygroundInvokeCommand(
            expected.Contract.Id,
            command.Executor,
            expected.Input,
            scenarioId,
            command.ExpertPackageId,
            Record: false);
        var descriptor = ResolveDescriptor(invokeCommand);
        var collector = new RecordingSemanticEventSink(DiscardingSemanticEventSink.Instance);
        var outcome = await InvokeAsync(invokeCommand, collector, cancellationToken).ConfigureAwait(false);
        var actual = new ExpertInvocationRecording(
            descriptor,
            invokeCommand.Input,
            collector.Events,
            DescribeTerminal(outcome),
            scenarioId,
            outcome.Diagnostics.ChannelKey,
            outcome.Result?.InvocationId,
            command.Executor,
            command.ExpertPackageId,
            outcome.Diagnostics.StartedUtc,
            outcome.Diagnostics.DurationMs);
        var comparison = ExpertRecordingComparison.Compare(expected, actual, command.StrictPayloads);
        return new PlaygroundReplayReport(
            outcome.Diagnostics,
            comparison.Matches,
            command.StrictPayloads,
            comparison.Divergences);
    }

    private static ExpertRecordingTerminal DescribeTerminal(PlaygroundInvocationOutcome outcome) =>
        outcome.Result is not null
            ? new ExpertRecordingTerminal(ExpertRecordingTerminal.Committed, outcome.Result.Output)
            : new ExpertRecordingTerminal(
                outcome.Diagnostics.Status == StatusAborted
                    ? ExpertRecordingTerminal.Aborted
                    : ExpertRecordingTerminal.ErrorStatus,
                error: outcome.Error?.Message ?? outcome.Diagnostics.Status);

    /// <summary>
    /// Persists the semantic recording for an opted-in invocation. Saving runs under
    /// <see cref="CancellationToken.None"/> so an aborted request still records its aborted
    /// terminal, and a storage failure surfaces through the diagnostics instead of failing the
    /// invocation it describes.
    /// </summary>
    private async Task<(string? RecordingId, string? RecordingError)> PersistRecordingAsync(
        ExpertContractDescriptor descriptor,
        PlaygroundInvokeCommand command,
        string channelKey,
        string? invocationId,
        IReadOnlyList<ExpertSemanticEvent> events,
        ExpertRecordingTerminal terminal,
        DateTimeOffset startedUtc,
        long durationMs)
    {
        if (!command.Record)
            return (null, null);
        try
        {
            var recording = new ExpertInvocationRecording(
                descriptor,
                command.Input,
                events,
                terminal,
                command.ScenarioId,
                channelKey,
                invocationId,
                command.Executor,
                command.ExpertPackageId,
                startedUtc,
                durationMs);
            var recordingId = await _recordings.SaveAsync(recording, CancellationToken.None).ConfigureAwait(false);
            return (recordingId, null);
        }
        catch (Exception exception)
        {
            return (null, "Recording failed: " + exception.Message);
        }
    }

    private ExpertContractDescriptor ResolveDescriptor(PlaygroundInvokeCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ContractId);
        switch (command.Executor)
        {
            case DevHostOptions.LocalExecutorName:
                {
                    if (local is null)
                        throw new ArgumentException(
                            "The local expert executor is not configured; start DevHost with " +
                            $"'--expert-executor {DevHostOptions.LocalExecutorName}' and expert artifacts.");
                    if (!_registry.TryGetContract(command.ContractId, out var registered))
                        throw new ArgumentException(
                            $"Contract '{command.ContractId}' is not registered. Registered contracts: {FormatIds(_registry.Contracts.Select(contract => contract.Id))}.");
                    return registered.ToDescriptor();
                }

            case DevHostOptions.FakeExecutorName:
                {
                    if (string.IsNullOrWhiteSpace(command.ScenarioId))
                        throw new ArgumentException("Fake executor invocations require a scenario id.");
                    return _fake.Contracts.FirstOrDefault(descriptor =>
                                string.Equals(descriptor.Id, command.ContractId, StringComparison.Ordinal))
                        ?? throw new ArgumentException(
                            $"Contract '{command.ContractId}' has no Fake scenario contract. Fake contracts: {FormatIds(_fake.Contracts.Select(descriptor => descriptor.Id))}.");
                }

            default:
                throw new ArgumentException(
                    $"Executor must be '{DevHostOptions.FakeExecutorName}' or '{DevHostOptions.LocalExecutorName}', but was '{command.Executor}'.");
        }
    }

    private PlaygroundInvocationOutcome Record(
        PlaygroundInvocationDiagnostics diagnostics,
        ExpertInvocationResult? result,
        Exception? error)
    {
        _history.Enqueue(diagnostics);
        while (_history.Count > HistoryLimit)
            _history.TryDequeue(out _);
        return new PlaygroundInvocationOutcome(diagnostics, result, error);
    }

    private static string FormatIds(IEnumerable<string> ids)
    {
        var ordered = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return ordered.Length == 0 ? "none" : string.Join(", ", ordered.Select(id => $"'{id}'"));
    }

    /// <summary>
    /// Tees semantic events: every event is forwarded to the caller's sink first, then captured
    /// for the recording, so recordings describe exactly what was delivered.
    /// </summary>
    private sealed class RecordingSemanticEventSink(IExpertSemanticEventSink inner) : IExpertSemanticEventSink
    {
        private readonly List<ExpertSemanticEvent> _events = [];

        public IReadOnlyList<ExpertSemanticEvent> Events => [.. _events];

        public async ValueTask WriteAsync(
            ExpertSemanticEvent semanticEvent,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(semanticEvent, cancellationToken).ConfigureAwait(false);
            _events.Add(new ExpertSemanticEvent(semanticEvent.EventType, semanticEvent.Payload));
        }
    }

    private sealed class DiscardingSemanticEventSink : IExpertSemanticEventSink
    {
        public static DiscardingSemanticEventSink Instance { get; } = new();

        public ValueTask WriteAsync(
            ExpertSemanticEvent semanticEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(semanticEvent);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
