namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Read-only history projection an expert consumes during an invocation. The interface shape is
/// read-only by construction: experts can project history into prompts but cannot append turns.
/// </summary>
public interface IExpertHistoryBucket
{
    string Description { get; }

    ValueTask<string> GetCompressedViewAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExpertHistoryTurn>> GetRawTurnsAsync(CancellationToken cancellationToken = default);
}
