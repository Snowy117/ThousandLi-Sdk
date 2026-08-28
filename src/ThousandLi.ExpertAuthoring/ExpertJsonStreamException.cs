namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Failure of the incremental JSON stream parser while consuming model output in Json response
/// mode: malformed structure, incomplete values at stream end, scalar roots, or invalid escapes.
/// The position refers to the character offset inside the concatenated model output.
/// </summary>
public sealed class ExpertJsonStreamException(
    string message,
    long line,
    long column,
    Exception? innerException = null)
    : Exception($"{message} (line {line}, column {column}.)", innerException)
{
    public long Line { get; } = line;

    public long Column { get; } = column;
}
