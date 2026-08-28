using System.Text;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class BlockScopeTests
{
    private static (PromptWriter Writer, StringBuilder Buffer) Create()
    {
        var buffer = new StringBuilder();
        var writer = new PromptWriter(buffer);
        return (writer, buffer);
    }

    [Fact]
    public void SingleBlockWritesContent()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
        }

        Assert.Equal("A", buffer.ToString());
    }

    [Fact]
    public void TwoBlocksAreSeparatedByADoubleNewline()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next().Append("B");
        }

        Assert.Equal("A\n\nB", buffer.ToString());
    }

    [Fact]
    public void ThreeBlocksAreSeparatedCorrectly()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next().Append("B");
            blocks.Next().Append("C");
        }

        Assert.Equal("A\n\nB\n\nC", buffer.ToString());
    }

    [Fact]
    public void EmptyFirstBlockIsIgnored()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            _ = blocks.Next();
            blocks.Next().Append("B");
        }

        Assert.Equal("B", buffer.ToString());
    }

    [Fact]
    public void EmptyMiddleBlockIsIgnored()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            _ = blocks.Next();
            blocks.Next().Append("C");
        }

        Assert.Equal("A\n\nC", buffer.ToString());
    }

    [Fact]
    public void EmptyLastBlockIsIgnored()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next();
        }

        Assert.Equal("A", buffer.ToString());
    }

    [Fact]
    public void AllBlocksEmptyProduceNoOutput()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next();
            blocks.Next();
            blocks.Next();
        }

        Assert.Equal("", buffer.ToString());
    }

    [Fact]
    public void TwoAdjacentEmptyBlocksProduceNoDoubleSeparator()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next();
            blocks.Next();
            blocks.Next().Append("D");
        }

        Assert.Equal("A\n\nD", buffer.ToString());
    }

    [Fact]
    public void TrailingEmptyBlockLeavesNoTrailingSeparator()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next().Append("B");
            blocks.Next();
        }

        Assert.Equal("A\n\nB", buffer.ToString());
    }

    [Fact]
    public void CustomSeparatorIsUsedBetweenBlocks()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks("---"))
        {
            blocks.Next().Append("X");
            blocks.Next().Append("Y");
        }

        Assert.Equal("X---Y", buffer.ToString());
    }

    [Fact]
    public void EmptySeparatorConcatenatesDirectly()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks(""))
        {
            blocks.Next().Append("X");
            blocks.Next().Append("Y");
        }

        Assert.Equal("XY", buffer.ToString());
    }

    [Fact]
    public void PreExistingContentIsPreservedBeforeBlocks()
    {
        var (writer, buffer) = Create();
        writer.Append("prefix|");
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next().Append("B");
        }

        Assert.Equal("prefix|A\n\nB", buffer.ToString());
    }

    [Fact]
    public void NestedBlockScopesSeparateInnerBlocks()
    {
        var (writer, buffer) = Create();
        var outerStart = writer.Length;
        using (var innerBlocks = writer.BeginBlocks())
        {
            innerBlocks.Next().Append("sub1");
            innerBlocks.Next().Append("sub2");
        }

        writer.WrapRange(outerStart, "<Utility>\n", "\n</Utility>");
        Assert.Equal("<Utility>\nsub1\n\nsub2\n</Utility>", buffer.ToString());
    }

    [Fact]
    public void NestedBlockScopesWithAllInnerEmptyProduceNoWrapOutput()
    {
        var (writer, buffer) = Create();
        writer.Append("prefix|");
        var outerStart = writer.Length;
        using (var innerBlocks = writer.BeginBlocks())
        {
            innerBlocks.Next();
            innerBlocks.Next();
        }

        writer.WrapRange(outerStart, "<Utility>\n", "\n</Utility>");
        Assert.Equal("prefix|", buffer.ToString());
    }

    [Fact]
    public void NestedBlockScopesWithOneEmptyBlockWriteTheOther()
    {
        var (writer, buffer) = Create();
        var outerStart = writer.Length;
        using (var innerBlocks = writer.BeginBlocks())
        {
            innerBlocks.Next();
            innerBlocks.Next().Append("sub2");
        }

        writer.WrapRange(outerStart, "<Tag>", "</Tag>");
        Assert.Equal("<Tag>sub2</Tag>", buffer.ToString());
    }

    [Fact]
    public void NextWriterSupportsInterpolatedAppends()
    {
        var (writer, buffer) = Create();
        const string name = "world";
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append($"Hello, {name}!");
        }

        Assert.Equal("Hello, world!", buffer.ToString());
    }

    [Fact]
    public void DisposeCalledMultipleTimesKeepsThePoolUsable()
    {
        var (writer, buffer) = Create();
        var blocks = writer.BeginBlocks();
        blocks.Next().Append("A");
        blocks.Dispose();
        blocks.Dispose();
        _ = blocks.Next();
        Assert.Equal("A", buffer.ToString());
    }

    [Fact]
    public void RegressionEmptyMiddleBlockPreservesSeparatorBetweenNonEmptyBlocks()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next();
            blocks.Next().Append("C");
        }

        Assert.Equal("A\n\nC", buffer.ToString());
    }

    [Fact]
    public void RegressionMultipleEmptyMiddleBlocksPreserveSeparator()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next();
            blocks.Next();
            blocks.Next();
            blocks.Next().Append("E");
        }

        Assert.Equal("A\n\nE", buffer.ToString());
    }

    [Fact]
    public void RegressionAlternatingEmptyAndNonEmptyBlocksStaySeparated()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("A");
            blocks.Next();
            blocks.Next().Append("C");
            blocks.Next();
            blocks.Next().Append("E");
        }

        Assert.Equal("A\n\nC\n\nE", buffer.ToString());
    }

    [Fact]
    public void IntegrationFullBuildPromptPatternProducesAllSections()
    {
        var (writer, buffer) = Create();
        using (var blocks = writer.BeginBlocks())
        {
            blocks.Next().Append("<WorldInfo>\ncontent\n</WorldInfo>");
            blocks.Next().Append("<ChatHistory>\nhistory\n</ChatHistory>");
            blocks.Next().Append("<ReaderInput>\ninput\n</ReaderInput>");

            var utilStart = writer.Length;
            using (var innerBlocks = writer.BeginBlocks())
            {
                innerBlocks.Next().Append("time tag instruction");
                innerBlocks.Next().Append("action options");
            }

            writer.WrapRange(utilStart, "<Utility>\n", "\n</Utility>");

            blocks.Next().Append("<ProseStyle>\nstyle\n</ProseStyle>");
            blocks.Next().Append("<InspectionProcess>checklist</InspectionProcess>");
        }

        var result = buffer.ToString();
        Assert.Contains("<WorldInfo>", result, StringComparison.Ordinal);
        Assert.Contains("<ChatHistory>", result, StringComparison.Ordinal);
        Assert.Contains("<ReaderInput>", result, StringComparison.Ordinal);
        Assert.Contains("<Utility>", result, StringComparison.Ordinal);
        Assert.Contains("</Utility>", result, StringComparison.Ordinal);
        Assert.Contains("<ProseStyle>", result, StringComparison.Ordinal);
        Assert.Contains("<InspectionProcess>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", result, StringComparison.Ordinal);
    }
}
