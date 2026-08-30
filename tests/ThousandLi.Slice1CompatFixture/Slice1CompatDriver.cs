using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Slice1CompatFixture;

// Compiled against the Slice 1 Contracts (3-parameter ExpertInvocationRequest constructor only).
// The compiled DLL under tests/ThousandLi.Sdk.Tests/Assets/Slice1Compat/ is the binary-compatibility
// gate asset; see ThousandLi.Slice1CompatFixture.csproj for regeneration instructions.
public static class Slice1CompatDriver
{
    public static string[] RunSlice1CompatScenario()
    {
        var contract = new ExpertContractDescriptor(
            "thousandli.fixture/long-text-writing",
            new ContractVersion(1, 0),
            "fixture-long-text-writing-v1");
        var request = new ExpertInvocationRequest(contract, "default", Json("{\"value\":42}"));
        var runner = new FakeExpertRunner([
            new FakeExpertScenario(
                "default",
                contract,
                [
                    new ExpertSemanticEvent("progress", Json("{\"step\":1}")),
                    new ExpertSemanticEvent("progress", Json("{\"step\":2}"))
                ],
                Json("{\"text\":\"fixture-complete\"}"))
        ]);
        var sink = new CollectingSink();
        var result = runner.ExecuteAsync(request, sink, CancellationToken.None).GetAwaiter().GetResult();
        var invocation = runner.Invocations.Single();
        return
        [
            $"scenario:{invocation.Request.ScenarioId}",
            $"invocation:{invocation.InvocationId}",
            $"events:{string.Join("|", sink.Events.Select(item => item.EventType))}",
            $"result:{result.Output.GetProperty("text").GetString()}",
            $"input:{invocation.Request.Input.GetProperty("value").GetInt32()}"
        ];
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CollectingSink : IExpertSemanticEventSink
    {
        public List<ExpertSemanticEvent> Events { get; } = [];

        public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(semanticEvent);
            return ValueTask.CompletedTask;
        }
    }
}
