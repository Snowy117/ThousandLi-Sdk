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
        const int n = 42;
        writer.Append($"answer={n}");
        Assert.Equal("answer=42", buffer.ToString());
    }

    [Fact]
    public void AppendStringInterpolationWritesTheValue()
    {
        var (writer, buffer) = Create();
        const string name = "Bob";
        writer.Append($"Hello, {name}!");
        Assert.Equal("Hello, Bob!", buffer.ToString());
    }

    [Fact]
    public void AppendMultipleHolesWritesAll()
    {
        var (writer, buffer) = Create();
        const int x = 1;
        const int y = 2;
        const int z = 3;
        writer.Append($"{x},{y},{z}");
        Assert.Equal("1,2,3", buffer.ToString());
    }

    [Fact]
    public void AppendFormatSpecifierIsRespected()
    {
        var (writer, buffer) = Create();
        const double value = 3.14159;
        writer.Append(string.Create(CultureInfo.InvariantCulture, $"pi={value:F2}"));
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
        const string label = "中文测试";
        writer.Append($"标签：{label}");
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
        const string name = "Alice";
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append($"Hello, {name}!");
            blocks.Next().Append($"Goodbye, {name}!");
        }

        Assert.Equal("Hello, Alice!\n\nGoodbye, Alice!", buffer.ToString());
    }
}
