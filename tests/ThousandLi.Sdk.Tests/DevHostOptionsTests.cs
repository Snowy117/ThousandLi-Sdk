using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class DevHostOptionsTests
{
    [Fact]
    public void ParseDefaultsFrontendUrlToTheGameIndexDocument()
    {
        var options = DevHostOptions.Parse(["--artifact", "samples"]);

        Assert.Equal("/game/index.html", options.FrontendUrl);
    }

    [Fact]
    public void ParseKeepsAnExplicitFrontendUrl()
    {
        var options = DevHostOptions.Parse(["--artifact", "samples", "--frontend-url", "/custom/index.html"]);

        Assert.Equal("/custom/index.html", options.FrontendUrl);
    }

    [Fact]
    public void ParseDefaultsExpertsToFake()
    {
        var options = DevHostOptions.Parse(["--artifact", "samples"]);

        Assert.Equal(DevHostOptions.FakeExecutorName, options.Experts);
    }

    [Theory]
    [InlineData(DevHostOptions.FakeExecutorName)]
    [InlineData(DevHostOptions.LocalExecutorName)]
    [InlineData(DevHostOptions.RemoteExecutorName)]
    public void ParseAcceptsEveryExpertsMode(string mode)
    {
        var options = DevHostOptions.Parse(["--artifact", "samples", "--experts", mode]);

        Assert.Equal(mode, options.Experts);
    }

    [Fact]
    public void ParseRejectsAnUnknownExpertsValueWithTheAllowedModes()
    {
        var exception = Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", "samples", "--experts", "cloud"]));

        Assert.Contains("'--experts'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'fake'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'local'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'remote'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'cloud'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsTheRemovedExpertExecutorAliasWithoutASilentFallback()
    {
        var exception = Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", "samples", "--expert-executor", "local"]));

        Assert.Contains("'--expert-executor'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--experts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsUnknownArguments()
    {
        var exception = Assert.Throws<ArgumentException>(() => DevHostOptions.Parse(
            ["--artifact", "samples", "--nonsense", "value"]));

        Assert.Contains("'--nonsense'", exception.Message, StringComparison.Ordinal);
    }
}
