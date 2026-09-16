using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Fake 场景文件的契约解析规则：场景按纯契约 id 声明（无版本协商、无指纹比对），
/// 重复的 (契约 id, 场景 id) 组合在装载期即失败。
/// </summary>
public sealed class ScriptedFakeScenarioContractTests
{
    [Fact]
    public void ScenariosAreKeyedByContractId()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"thousandli-fake-scenarios-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            [
              {
                "contract": "thousandli.expert/long-text-writing",
                "scenarioId": "advance",
                "events": [{ "eventType": "chunk", "payload": { "text": "hi" } }],
                "result": { "done": true }
              }
            ]
            """);
        try
        {
            var runner = ScriptedFakeExpertRunner.Load(path);

            var contractId = Assert.Single(runner.Contracts);
            Assert.Equal(AbstractLongTextWritingExpert.ContractId, contractId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecutionResolvesScenariosByContractId()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"thousandli-fake-scenarios-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            """
            [
              {
                "contract": "tests/third-party",
                "scenarioId": "advance",
                "result": { "done": true }
              }
            ]
            """,
            TestSupport.CancellationToken);
        try
        {
            var runner = ScriptedFakeExpertRunner.Load(path);

            var result = await runner.ExecuteAsync(
                new ExpertInvocationRequest("tests/third-party", "advance", TestSupport.Json("{}")),
                new RecordingSemanticSink(),
                TestSupport.CancellationToken);

            Assert.True(result.Output.GetProperty("done").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateScenarioKeysForOneContractAreRejectedAtLoadTime()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"thousandli-fake-scenarios-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            [
              { "contract": "tests/dup", "scenarioId": "advance", "result": {} },
              { "contract": "tests/dup", "scenarioId": "advance", "result": {} }
            ]
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ScriptedFakeExpertRunner.Load(path));

            Assert.Contains("Duplicate Fake scenario", exception.Message, StringComparison.Ordinal);
            Assert.Contains("tests/dup", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
