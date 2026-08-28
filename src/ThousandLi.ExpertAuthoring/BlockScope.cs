using System.Text;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// A block scope that inserts a separator between non-empty blocks and ignores empty blocks by
/// rolling back. Each <see cref="Next"/> call first checks whether the previous block stayed empty
/// and rolls its separator back; <see cref="Dispose"/> performs the final rollback.
/// </summary>
public ref struct BlockScope : IDisposable
{
    private readonly StringBuilder? _sb;
    private readonly string _separator;
    private bool _hasPriorContent;
    private int _beforeSepStart;
    private int _afterSepStart;

    /// <summary>Whether actual content exists before the current block's separator.</summary>
    private bool _hasContentBeforeCurrentBlock;

    private bool _active;

    internal BlockScope(StringBuilder sb, string separator)
    {
        _sb = sb;
        _separator = separator;
        _hasPriorContent = false;
        _beforeSepStart = sb.Length;
        _afterSepStart = sb.Length;
        _hasContentBeforeCurrentBlock = false;
        _active = true;
    }

    /// <summary>
    /// Gets the writer of the next block, rolling back the previous block's separator when that
    /// block stayed empty.
    /// </summary>
    public PromptWriter Next()
    {
        if (!_active) return default;

        RollbackIfEmpty();

        _beforeSepStart = _sb!.Length;
        _hasContentBeforeCurrentBlock = _hasPriorContent;

        if (_hasPriorContent)
            _sb.Append(_separator);

        _afterSepStart = _sb.Length;
        _hasPriorContent = true;

        return new PromptWriter(_sb);
    }

    /// <summary>Final rollback: removes the last block's separator when that block stayed empty.</summary>
    public void Dispose()
    {
        if (!_active) return;

        RollbackIfEmpty();
        _active = false;
    }

    private void RollbackIfEmpty()
    {
        if (_sb is null) return;

        if (!_hasPriorContent || _sb.Length != _afterSepStart) return;

        _sb.Length = _beforeSepStart;
        _hasPriorContent = _hasContentBeforeCurrentBlock;
    }
}
