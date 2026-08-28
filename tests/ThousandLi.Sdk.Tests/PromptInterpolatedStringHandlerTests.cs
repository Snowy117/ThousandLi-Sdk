using System.Globalization;
using System.Text;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class PromptInterpolatedStringHandlerTests
{
    private static (PromptWriter Writer, StringBuilder Buffer) Create()
    {
        var buffer = new StringBuilder();
        var writer = new PromptWriter(buffer);
        return (writer, buffer);
    }

    [Fact]
    public void AppendLiteralOnlyWritesDirectly()
    {
        var (writer, buffer) = Create();
        writer.Append("hello world");
        Assert.Equal("hello world", buffer.ToString());
    }

    [Fact]
    public void AppendSingleIntWritesFormatted()
    {
        var (writer, buffer) = Create();
        const int N = 42;
        writer.Append($"answer={N}");
        Assert.Equal("answer=42", buffer.ToString());
    }

    [Fact]
    public void AppendStringInterpolationWritesTheValue()
    {
        var (writer, buffer) = Create();
        const string Name = "Bob";
        writer.Append($"Hello, {Name}!");
        Assert.Equal("Hello, Bob!", buffer.ToString());
    }

    [Fact]
    public void AppendMultipleHolesWritesAll()
    {
        var (writer, buffer) = Create();
        const int X = 1;
        const int Y = 2;
        const int Z = 3;
        writer.Append($"{X},{Y},{Z}");
        Assert.Equal("1,2,3", buffer.ToString());
    }

    [Fact]
    public void AppendFormatSpecifierIsRespected()
    {
        var (writer, buffer) = Create();
        const double Value = 3.14159;
        writer.Append(string.Create(CultureInfo.InvariantCulture, $"pi={Value:F2}"));
        Assert.Equal("pi=3.14", buffer.ToString());
    }

    [Fact]
    public void AppendNullStringWritesEmpty()
    {
        var (writer, buffer) = Create();
#pragma warning disable RCS1118 // Intentionally not const: a const local would trigger IDE1006 PascalCase demands
        string? value = null;
#pragma warning restore RCS1118
        writer.Append($"value={value}");
        Assert.Equal("value=", buffer.ToString());
    }

    [Fact]
    public void AppendEmptyInterpolatedWritesNothing()
    {
        var (writer, buffer) = Create();
        writer.Append("");
        Assert.Equal("", buffer.ToString());
    }

    [Fact]
    public void AppendUnicodeContentWritesCorrectly()
    {
        var (writer, buffer) = Create();
        const string Label = "中文测试";
        writer.Append($"标签：{Label}");
        Assert.Equal("标签：中文测试", buffer.ToString());
    }

    [Fact]
    public void AppendReadOnlySpanWritesContent()
    {
        var (writer, buffer) = Create();
        writer.Append("prefix|");
        var span = "span-content".AsSpan();
        writer.Append(span);
        Assert.Equal("prefix|span-content", buffer.ToString());
    }

    [Fact]
    public void InterpolatedAppendsWithInsideBlockScopeWork()
    {
        var (writer, buffer) = Create();
        const string Name = "Alice";
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append($"Hello, {Name}!");
            blocks.Next().Append($"Goodbye, {Name}!");
        }

        Assert.Equal("Hello, Alice!\n\nGoodbye, Alice!", buffer.ToString());
    }
}
