using System.Globalization;
using System.Text;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class PromptWriterTests
{
    private static (PromptWriter Writer, StringBuilder Buffer) Create()
    {
        var buffer = new StringBuilder();
        var writer = new PromptWriter(buffer);
        return (writer, buffer);
    }

    [Fact]
    public void AppendStringWritesContent()
    {
        var (writer, buffer) = Create();
        writer.Append("hello");
        Assert.Equal("hello", buffer.ToString());
    }

    [Fact]
    public void AppendStringMultipleAppendsConcatenate()
    {
        var (writer, buffer) = Create();
        writer.Append("hello");
        writer.Append(" ");
        writer.Append("world");
        Assert.Equal("hello world", buffer.ToString());
    }

    [Fact]
    public void AppendStringEmptyChangesNothing()
    {
        var (writer, buffer) = Create();
        writer.Append("existing");
        writer.Append("");
        Assert.Equal("existing", buffer.ToString());
    }

    [Fact]
    public void AppendReadOnlySpanWritesContent()
    {
        var (writer, buffer) = Create();
        var span = "span-content".AsSpan();
        writer.Append(span);
        Assert.Equal("span-content", buffer.ToString());
    }

    [Fact]
    public void AppendReadOnlySpanEmptyChangesNothing()
    {
        var (writer, buffer) = Create();
        writer.Append("existing");
        var empty = ReadOnlySpan<char>.Empty;
        writer.Append(empty);
        Assert.Equal("existing", buffer.ToString());
    }

    [Fact]
    public void AppendInterpolatedWritesContent()
    {
        var (writer, buffer) = Create();
        const string Name = "Alice";
        writer.Append($"Hello, {Name}!");
        Assert.Equal("Hello, Alice!", buffer.ToString());
    }

    [Fact]
    public void AppendInterpolatedMultipleHolesWriteAll()
    {
        var (writer, buffer) = Create();
        const int A = 1;
        const int B = 2;
        const int C = 3;
        writer.Append($"{A}+{B}={C}");
        Assert.Equal("1+2=3", buffer.ToString());
    }

    [Fact]
    public void AppendInterpolatedWithFormatSpecifierRespectsTheFormat()
    {
        var (writer, buffer) = Create();
        const double Value = 3.14159;
        writer.Append(string.Create(CultureInfo.InvariantCulture, $"Pi={Value:F2}"));
        Assert.Equal("Pi=3.14", buffer.ToString());
    }

    [Fact]
    public void AppendInterpolatedMixedLiteralsAndFormattedWriteAll()
    {
        var (writer, buffer) = Create();
        const string Mode = "first-person";
        writer.Append($"Narration: {Mode}, done.");
        Assert.Equal("Narration: first-person, done.", buffer.ToString());
    }

    [Fact]
    public void LengthIsInitiallyZero()
    {
        var (writer, _) = Create();
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void LengthReflectsAppendedContent()
    {
        var (writer, _) = Create();
        writer.Append("abc");
        Assert.Equal(3, writer.Length);
    }

    [Fact]
    public void BeginBlocksReturnsAUsableScope()
    {
        var (writer, _) = Create();
        using var scope = writer.BeginBlocks();
        scope.Next().Append("A");
    }

    [Fact]
    public void BeginBlocksUsesACustomSeparator()
    {
        var (writer, buffer) = Create();
        using var scope = writer.BeginBlocks("---");
        scope.Next().Append("A");
        scope.Next().Append("B");
        Assert.Equal("A---B", buffer.ToString());
    }

    [Fact]
    public void WrapRangeWrapsContentWithTags()
    {
        var (writer, buffer) = Create();
        var start = writer.Length;
        writer.Append("inner");
        writer.WrapRange(start, "<Tag>", "</Tag>");
        Assert.Equal("<Tag>inner</Tag>", buffer.ToString());
    }

    [Fact]
    public void WrapRangeSkipsWrappingWhenNoContentStartsAtStart()
    {
        var (writer, buffer) = Create();
        writer.Append("prefix");
        var start = writer.Length;
        writer.WrapRange(start, "<Tag>", "</Tag>");
        Assert.Equal("prefix", buffer.ToString());
    }

    [Fact]
    public void WrapRangeWrapsContentAddedAfterStart()
    {
        var (writer, buffer) = Create();
        writer.Append("before|");
        var start = writer.Length;
        writer.Append("middle");
        writer.WrapRange(start, "[", "]");
        Assert.Equal("before|[middle]", buffer.ToString());
    }
}
