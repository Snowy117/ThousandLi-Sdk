using System.Runtime.CompilerServices;
using System.Text;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// A C# 10 interpolated-string handler that writes interpolation holes directly into the
/// <see cref="StringBuilder"/> behind a <see cref="PromptWriter"/>, avoiding intermediate string
/// allocations.
/// </summary>
[InterpolatedStringHandler]
public readonly ref struct PromptInterpolatedStringHandler
{
    private readonly StringBuilder _sb;

#pragma warning disable RCS1163 // Conventional signature: InterpolatedStringHandler requires literalLength/formattedCount
    public PromptInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        PromptWriter writer,
        out bool handlerIsValid)
    {
        _sb = writer._sb;
        handlerIsValid = true;
    }
#pragma warning restore RCS1163

    public void AppendLiteral(string s)
    {
        _sb.Append(s);
    }

    public void AppendFormatted<T>(T value)
    {
        _sb.Append(value);
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        if (value is IFormattable formattable)
            _sb.Append(formattable.ToString(format, formatProvider: null));
        else
            _sb.Append(value);
    }

    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        _sb.Append(value);
    }

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        _ = alignment;
        _ = format;
        _sb.Append(value);
    }

    public void AppendFormatted(string? value)
    {
        _sb.Append(value);
    }
}
