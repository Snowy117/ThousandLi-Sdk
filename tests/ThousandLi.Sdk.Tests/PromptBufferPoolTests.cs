using System.Globalization;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class PromptBufferPoolTests
{
    [Fact]
    public void RentReturnsANonNullBuffer()
    {
        using var lease = PromptBufferPool.Rent();
        Assert.NotNull(lease.Buffer);
    }

    [Fact]
    public void RentReturnsAnEmptyBuffer()
    {
        using var lease = PromptBufferPool.Rent();
        Assert.Equal(0, lease.Buffer.Length);
    }

    [Fact]
    public void WriterAppendWritesToTheRentedBuffer()
    {
        using var lease = PromptBufferPool.Rent();
        lease.Writer.Append("hello");
        Assert.Equal("hello", lease.Buffer.ToString());
    }

    [Fact]
    public void DisposeMakesTheBufferUnusable()
    {
        var lease = PromptBufferPool.Rent();
        lease.Writer.Append("some content");
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Buffer);
    }

    [Fact]
    public void RentAfterReturnGivesAnEmptyBuffer()
    {
        using (var first = PromptBufferPool.Rent())
        {
            first.Writer.Append("first");
        }

        using var second = PromptBufferPool.Rent();
        Assert.Equal(0, second.Buffer.Length);
    }

    [Fact]
    public void MultipleConcurrentRentsStayIndependent()
    {
        using var first = PromptBufferPool.Rent();
        using var second = PromptBufferPool.Rent();

        first.Writer.Append("A");
        second.Writer.Append("B");

        Assert.Equal("A", first.Buffer.ToString());
        Assert.Equal("B", second.Buffer.ToString());
    }

    [Fact]
    public void DisposeCalledTwiceKeepsThePoolUsable()
    {
        var lease = PromptBufferPool.Rent();
        lease.Writer.Append("x");
        lease.Dispose();
        lease.Dispose();

        using var another = PromptBufferPool.Rent();
        Assert.Equal(0, another.Buffer.Length);
    }

    [Fact]
    public void FullWorkflowRentWriteDisposeRentAndWriteAgain()
    {
        string firstResult;
        using (var lease = PromptBufferPool.Rent())
        {
            var writer = lease.Writer;
            using (var blocks = writer.BeginBlocks())
            {
                blocks.Next().Append("Block1");
                blocks.Next().Append("Block2");
            }

            firstResult = lease.Buffer.ToString();
        }

        Assert.Equal("Block1\n\nBlock2", firstResult);

        string secondResult;
        using (var lease = PromptBufferPool.Rent())
        {
            lease.Writer.Append("fresh");
            secondResult = lease.Buffer.ToString();
        }

        Assert.Equal("fresh", secondResult);
    }

    [Fact]
    public async Task ConcurrentRentAndReturnDoesNotCorruptBuffers()
    {
        var tasks = new Task[10];
        var results = new string[10];

        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks[index] = Task.Run(() =>
            {
                using var lease = PromptBufferPool.Rent();
                lease.Writer.Append(string.Create(CultureInfo.InvariantCulture, $"task-{index}"));
                results[index] = lease.Buffer.ToString();
            }, TestSupport.CancellationToken);
        }

        await Task.WhenAll(tasks);

        for (var i = 0; i < 10; i++)
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"task-{i}"), results[i]);
    }
}
