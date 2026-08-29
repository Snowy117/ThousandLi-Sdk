using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class JsonStreamExceptionTests
{
    [Fact]
    public void Constructor_SetsMessageWithPosition()
    {
        var ex = new JsonStreamException("test error", 42);
        Assert.Equal("test error (position 42).", ex.Message);
    }

    [Fact]
    public void Position_Property_ReturnsPosition()
    {
        var ex = new JsonStreamException("err", 99);
        Assert.Equal(99, ex.Position);
    }

    [Fact]
    public void Position_Zero_ReturnsZero()
    {
        var ex = new JsonStreamException("err", 0);
        Assert.Equal(0, ex.Position);
        Assert.Equal("err (position 0).", ex.Message);
    }
}
