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
/// Deterministic BasicAi replay for tests and offline development: interactions are consumed in
/// registration order, the requested model must match the recording, and exhaustion fails fast.
/// </summary>
public sealed class RecordedBasicAi : ILocalBasicAi
{
    private readonly Queue<RecordedBasicAiInteraction> _interactions;
    private readonly List<LocalBasicAiRequest> _invocations = [];

    public RecordedBasicAi(IReadOnlyList<string> availableModels, IEnumerable<RecordedBasicAiInteraction> interactions)
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
        AvailableModels = [.. availableModels];
        _interactions = new Queue<RecordedBasicAiInteraction>(materialized);
    }

    public IReadOnlyList<string> AvailableModels { get; }

    /// <summary>Requests received so far, in call order.</summary>
    public IReadOnlyList<LocalBasicAiRequest> Invocations => [.. _invocations];

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

    private static InvalidOperationException MismatchedShape(LocalBasicAiRequest request, bool streaming) =>
        new(
            $"The next recorded interaction for model '{request.ModelId}' is {(streaming ? "a completion" : "a stream")}, but the invocation was {(streaming ? "streaming" : "non-streaming")}.");
}
