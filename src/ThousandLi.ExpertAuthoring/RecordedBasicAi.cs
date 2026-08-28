using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// One deterministic recorded interaction: either a completion result or a stream event sequence
/// for one model id. Exactly one of <see cref="Completion"/> and <see cref="StreamEvents"/> is set.
/// </summary>
public sealed record RecordedBasicAiInteraction
{
    public RecordedBasicAiInteraction(
        string modelId,
        LocalBasicAiCompletionResult? completion = null,
        IReadOnlyList<ExpertStreamEvent>? streamEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (completion is null == streamEvents is null)
            throw new ArgumentException(
                "Exactly one of the completion result and the stream events must be provided.", nameof(completion));
        if (streamEvents is not null)
        {
            foreach (var streamEvent in streamEvents)
                ArgumentNullException.ThrowIfNull(streamEvent);
        }

        ModelId = modelId;
        Completion = completion;
        StreamEvents = streamEvents is null ? null : new ReadOnlyCollection<ExpertStreamEvent>([.. streamEvents]);
    }

    public string ModelId { get; }

    public LocalBasicAiCompletionResult? Completion { get; }

    public IReadOnlyList<ExpertStreamEvent>? StreamEvents { get; }
}

/// <summary>
/// One deterministic recorded interaction of the schema-driven runtime surface: either a
/// <see cref="BasicAiCompletionResult"/> or a <see cref="BasicAiStreamEvent"/> sequence for one
/// model id. Exactly one of <see cref="Completion"/> and <see cref="StreamEvents"/> is set.
/// </summary>
public sealed record RecordedRuntimeBasicAiInteraction
{
    public RecordedRuntimeBasicAiInteraction(
        string modelId,
        BasicAiCompletionResult? completion = null,
        IReadOnlyList<BasicAiStreamEvent>? streamEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (completion is null == streamEvents is null)
            throw new ArgumentException(
                "Exactly one of the completion result and the stream events must be provided.", nameof(completion));
        if (streamEvents is not null)
        {
            foreach (var streamEvent in streamEvents)
                ArgumentNullException.ThrowIfNull(streamEvent);
        }

        ModelId = modelId;
        Completion = completion;
        StreamEvents = streamEvents is null ? null : new ReadOnlyCollection<BasicAiStreamEvent>([.. streamEvents]);
    }

    public string ModelId { get; }

    public BasicAiCompletionResult? Completion { get; }

    public IReadOnlyList<BasicAiStreamEvent>? StreamEvents { get; }
}

/// <summary>
/// Deterministic BasicAi replay for tests and offline development: interactions are consumed in
/// registration order, the requested model must match the recording, and exhaustion fails fast.
/// The local <see cref="ILocalBasicAi"/> surface and the schema-driven <see cref="IRuntimeBasicAi"/>
/// surface replay from independent queues; each surface fails fast once its own queue is empty.
/// </summary>
public sealed class RecordedBasicAi : ILocalBasicAi, IRuntimeBasicAi
{
    private readonly Queue<RecordedBasicAiInteraction> _interactions;
    private readonly Queue<RecordedRuntimeBasicAiInteraction> _runtimeInteractions;
    private readonly List<LocalBasicAiRequest> _invocations = [];
    private readonly List<BasicAiRequest> _runtimeInvocations = [];

    public RecordedBasicAi(
        IReadOnlyList<string> availableModels,
        IEnumerable<RecordedBasicAiInteraction> interactions,
        IEnumerable<RecordedRuntimeBasicAiInteraction>? runtimeInteractions = null)
    {
        ArgumentNullException.ThrowIfNull(availableModels);
        if (availableModels.Count == 0)
            throw new ArgumentException("At least one model must be configured.", nameof(availableModels));
        foreach (var model in availableModels)
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(interactions);
        var materialized = interactions.ToArray();
        foreach (var interaction in materialized)
            ArgumentNullException.ThrowIfNull(interaction);
        var materializedRuntime = runtimeInteractions?.ToArray() ?? [];
        foreach (var interaction in materializedRuntime)
            ArgumentNullException.ThrowIfNull(interaction);
        AvailableModels = [.. availableModels];
        _interactions = new Queue<RecordedBasicAiInteraction>(materialized);
        _runtimeInteractions = new Queue<RecordedRuntimeBasicAiInteraction>(materializedRuntime);
    }

    public IReadOnlyList<string> AvailableModels { get; }

    IReadOnlyList<BasicAiModelDescriptor> IRuntimeBasicAi.AvailableModels =>
        [.. AvailableModels.Select(model => new BasicAiModelDescriptor(model))];

    /// <summary>Local-surface requests received so far, in call order.</summary>
    public IReadOnlyList<LocalBasicAiRequest> Invocations => [.. _invocations];

    /// <summary>Runtime-surface requests received so far, in call order.</summary>
    public IReadOnlyList<BasicAiRequest> RuntimeInvocations => [.. _runtimeInvocations];

    public Task<LocalBasicAiCompletionResult> CompleteAsync(
        LocalBasicAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var interaction = DequeueFor(request);
        return interaction.Completion is { } completion
            ? Task.FromResult(new LocalBasicAiCompletionResult(completion.Text))
            : throw MismatchedShape(request, streaming: false);
    }

    public Task<BasicAiCompletionResult> CompleteAsync(
        BasicAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var interaction = DequeueFor(request);
        return interaction.Completion is { } completion
            ? Task.FromResult(new BasicAiCompletionResult(completion.Json, completion.Reasoning))
            : throw MismatchedShape(request, streaming: false);
    }

    // Deliberately a synchronous replay iterator: every recorded event is already materialized,
    // so no asynchronous operation exists to await. Cancellation is honored between events.
    [SuppressMessage("ReSharper", "AsyncMethodWithoutAwait",
        Justification = "The recorded sequence is fully materialized; iteration is intentionally synchronous.")]
    public async IAsyncEnumerable<ExpertStreamEvent> StreamAsync(
        LocalBasicAiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var interaction = DequeueFor(request);
        if (interaction.StreamEvents is null)
            throw MismatchedShape(request, streaming: true);
        foreach (var streamEvent in interaction.StreamEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    [SuppressMessage("ReSharper", "AsyncMethodWithoutAwait",
        Justification = "The recorded sequence is fully materialized; iteration is intentionally synchronous.")]
    public async IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
        BasicAiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var interaction = DequeueFor(request);
        if (interaction.StreamEvents is null)
            throw MismatchedShape(request, streaming: true);
        foreach (var streamEvent in interaction.StreamEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private RecordedBasicAiInteraction DequeueFor(LocalBasicAiRequest request)
    {
        _invocations.Add(request);
        if (_interactions.Count == 0)
            throw new InvalidOperationException(
                $"No recorded interactions remain; the invocation for model '{request.ModelId}' exceeds the recording.");
        var interaction = _interactions.Dequeue();
        if (!string.Equals(interaction.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recorded interaction {_invocations.Count} targets model '{interaction.ModelId}', but the invocation requested '{request.ModelId}'.");
        }

        return interaction;
    }

    private RecordedRuntimeBasicAiInteraction DequeueFor(BasicAiRequest request)
    {
        _runtimeInvocations.Add(request);
        if (_runtimeInteractions.Count == 0)
            throw new InvalidOperationException(
                $"No recorded runtime interactions remain; the invocation for model '{request.ModelId}' exceeds the recording.");
        var interaction = _runtimeInteractions.Dequeue();
        if (!string.Equals(interaction.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recorded runtime interaction {_runtimeInvocations.Count} targets model '{interaction.ModelId}', but the invocation requested '{request.ModelId}'.");
        }

        return interaction;
    }

    private static InvalidOperationException MismatchedShape(LocalBasicAiRequest request, bool streaming) =>
        new(
            $"The next recorded interaction for model '{request.ModelId}' is {(streaming ? "a completion" : "a stream")}, but the invocation was {(streaming ? "streaming" : "non-streaming")}.");

    private static InvalidOperationException MismatchedShape(BasicAiRequest request, bool streaming) =>
        new(
            $"The next recorded runtime interaction for model '{request.ModelId}' is {(streaming ? "a completion" : "a stream")}, but the invocation was {(streaming ? "streaming" : "non-streaming")}.");
}
