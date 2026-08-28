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
}
