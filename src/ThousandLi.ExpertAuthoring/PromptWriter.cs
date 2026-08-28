using System.Runtime.CompilerServices;
using System.Text;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// A zero-intermediate-string prompt writer wrapping a <see cref="StringBuilder"/>. Interpolated
/// strings passed to <see cref="Append(ref PromptInterpolatedStringHandler)"/> write their holes
/// directly into the buffer through <see cref="PromptInterpolatedStringHandler"/>.
/// </summary>
public readonly struct PromptWriter
{
    // ReSharper disable once InconsistentNaming
    // dotnet format IDE1006 misreports the _ prefix on struct fields (it only recognizes class instance fields).
#pragma warning disable IDE1006
    internal readonly StringBuilder _sb;
#pragma warning restore IDE1006

    internal PromptWriter(StringBuilder sb)
    {
        _sb = sb;
    }

    /// <summary>The current buffer length.</summary>
    public int Length => _sb.Length;

    /// <summary>Appends text.</summary>
    public void Append(string value)
    {
        _sb.Append(value);
    }

    /// <summary>Appends text.</summary>
    public void Append(ReadOnlySpan<char> value)
    {
        _sb.Append(value);
    }

    /// <summary>Zero-allocation interpolated append. The compiler writes interpolation holes directly into the buffer.</summary>
    // ReSharper disable once MemberCanBeMadeStatic.Global
    // ReSharper disable once UnusedParameter.Global
    // The C# 10 interpolated-string-handler pattern binds this via [InterpolatedStringHandlerArgument("")];
    // the handler has already written its content to the buffer by the time the method body runs.
#pragma warning disable CA1822, IDE0060, RCS1163 // The interpolated-string-handler pattern requires an instance method with the conventional signature
    public void Append([InterpolatedStringHandlerArgument("")] ref PromptInterpolatedStringHandler handler)
#pragma warning restore CA1822, IDE0060, RCS1163
    {
    }

    /// <summary>Starts a block scope that joins non-empty blocks with a separator and ignores empty blocks.</summary>
    public BlockScope BeginBlocks(string separator = "\n\n")
    {
        return new BlockScope(_sb, separator);
    }

    /// <summary>
    /// Wraps the content from <paramref name="start"/> to the end of the buffer with the given tags.
    /// Nothing is written when no content starts at <paramref name="start"/>.
    /// </summary>
    public void WrapRange(int start, string before, string after)
    {
        if (_sb.Length == start) return;

        _sb.Insert(start, before);
        _sb.Append(after);
    }
}
