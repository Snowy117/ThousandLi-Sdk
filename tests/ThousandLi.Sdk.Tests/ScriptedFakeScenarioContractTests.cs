using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Fake 场景文件的契约解析规则：官方类别契约可省略 fingerprint（从锚 Descriptor 解析补全），
/// 第三方契约必须显式声明；显式声明走 Supports+指纹比对，同契约场景必须版本与指纹一致。
/// </summary>
public sealed class ScriptedFakeScenarioContractTests
{
    [Fact]
    public void OfficialContractOmittingTheFingerprintResolvesItFromTheAnchorDescriptor()
    {
        var runner = Load(
            """
            [
              {
                "contract": { "id": "thousandli.expert/long-text-writing", "version": { "major": 1, "minor": 0 } },
                "scenarioId": "advance",
                "events": [],
                "result": { "text": "resolved" }
              }
            ]
            """);

        var contract = Assert.Single(runner.Contracts);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Id, contract.Id);
        Assert.Equal(AbstractLongTextWritingExpert.Descriptor.Fingerprint, contract.Fingerprint);
    }

    [Fact]
    public async Task OmittedOfficialFingerprintExecutesAgainstTheAnchorDescriptor()
    {
        var runner = Load(
            """
            [
              {
                "contract": { "id": "thousandli.expert/long-text-writing", "version": { "major": 1, "minor": 0 } },
                "scenarioId": "advance",
                "events": [],
                "result": { "text": "resolved" }
              }
            ]
            """);

        var result = await runner.ExecuteAsync(
            new ExpertInvocationRequest(AbstractLongTextWritingExpert.Descriptor, "advance", TestSupport.Json("{}")),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken);

        Assert.Equal("resolved", result.Output.GetProperty("text").GetString());
    }

    [Fact]
    public void ThirdPartyContractOmittingTheFingerprintIsRejectedAtLoadTime()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load(
            """
            [
              {
                "contract": { "id": "tests/third-party", "version": { "major": 1, "minor": 0 } },
                "scenarioId": "advance",
                "events": [],
                "result": { "text": "no" }
              }
            ]
            """));

        Assert.Contains("tests/third-party", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must declare an explicit fingerprint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitFingerprintMismatchIsRejectedAtExecutionTime()
    {
        var runner = Load(
            """
            [
              {
                "contract": {
                  "id": "thousandli.expert/long-text-writing",
                  "version": { "major": 1, "minor": 0 },
                  "fingerprint": "deadbeef"
                },
                "scenarioId": "advance",
                "events": [],
                "result": { "text": "never" }
              }
            ]
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.ExecuteAsync(
            new ExpertInvocationRequest(AbstractLongTextWritingExpert.Descriptor, "advance", TestSupport.Json("{}")),
            new RecordingSemanticSink(),
            TestSupport.CancellationToken));

        Assert.Contains("contract mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedVersionsOrFingerprintsForOneContractAreRejectedAtLoadTime()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load(
            """
            [
              {
                "contract": {
                  "id": "tests/story",
                  "version": { "major": 1, "minor": 0 },
                  "fingerprint": "fp-a"
                },
                "scenarioId": "advance",
                "events": [],
                "result": {}
              },
              {
                "contract": {
                  "id": "tests/story",
                  "version": { "major": 1, "minor": 1 },
                  "fingerprint": "fp-a"
                },
                "scenarioId": "reflect",
                "events": [],
                "result": {}
              }
            ]
            """));

        Assert.Contains("tests/story", exception.Message, StringComparison.Ordinal);
        Assert.Contains("one version and fingerprint", exception.Message, StringComparison.Ordinal);
    }

    private static ScriptedFakeExpertRunner Load(string json)
    {
        var directory = Directory.CreateTempSubdirectory("thousandli-fake-scenarios-");
        try
        {
            var path = Path.Combine(directory.FullName, "scenarios.json");
            File.WriteAllText(path, json);
            return ScriptedFakeExpertRunner.Load(path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
