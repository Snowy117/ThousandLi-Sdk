using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertInvocationRecordingTests
{
    [Fact]
    public void ToJsonFromJsonRoundTripsEveryField()
    {
        var recording = TestSupport.CreateRecording(
            scenarioId: "advance",
            channelKey: "playground-00000042",
            invocationId: "fake-00000007",
            executor: "local",
            expertPackageId: "official_dreamseek@1.0.0",
            recordedAtUtc: new DateTimeOffset(2026, 8, 24, 9, 30, 5, TimeSpan.Zero),
            durationMs: 1234);

        var roundTripped = ExpertInvocationRecording.FromJson(recording.ToJson());

        Assert.Equal(recording.Contract.Id, roundTripped.Contract.Id);
        Assert.Equal(recording.Contract.Version, roundTripped.Contract.Version);
        Assert.Equal(recording.Contract.Fingerprint, roundTripped.Contract.Fingerprint);
        Assert.True(JsonElement.DeepEquals(recording.Input, roundTripped.Input));
        var recordedEvent = Assert.Single(roundTripped.Events);
        Assert.Equal("narrative", recordedEvent.EventType);
        Assert.True(JsonElement.DeepEquals(recording.Events[0].Payload, recordedEvent.Payload));
        Assert.Equal(ExpertRecordingTerminal.Committed, roundTripped.Terminal.Status);
        Assert.True(JsonElement.DeepEquals(recording.Terminal.Output!.Value, roundTripped.Terminal.Output!.Value));
        Assert.Null(roundTripped.Terminal.Error);
        Assert.Equal("advance", roundTripped.ScenarioId);
        Assert.Equal("playground-00000042", roundTripped.ChannelKey);
        Assert.Equal("fake-00000007", roundTripped.InvocationId);
        Assert.Equal("local", roundTripped.Executor);
        Assert.Equal("official_dreamseek@1.0.0", roundTripped.ExpertPackageId);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 30, 5, TimeSpan.Zero), roundTripped.RecordedAtUtc);
        Assert.Equal(1234, roundTripped.DurationMs);
        Assert.Equal(ExpertInvocationRecording.CurrentFormatMajor, roundTripped.FormatMajor);
    }

    [Fact]
    public void ToJsonFromJsonRoundTripsAbortedTerminalWithEmptyEventList()
    {
        var recording = TestSupport.CreateRecording(
            events: [],
            terminalStatus: ExpertRecordingTerminal.Aborted,
            terminalError: "The operation was canceled.");

        var roundTripped = ExpertInvocationRecording.FromJson(recording.ToJson());

        Assert.Empty(roundTripped.Events);
        Assert.Equal(ExpertRecordingTerminal.Aborted, roundTripped.Terminal.Status);
        Assert.Null(roundTripped.Terminal.Output);
        Assert.Equal("The operation was canceled.", roundTripped.Terminal.Error);
    }

    [Fact]
    public void ToJsonUsesCurrentFormatMajor()
    {
        Assert.Contains(
            $"""
             "formatMajor": {ExpertInvocationRecording.CurrentFormatMajor}
             """,
            TestSupport.CreateRecording().ToJson(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonRejectsUnsupportedFormatMajorWithResetGuidance()
    {
        var json = TestSupport.CreateRecording().ToJson().Replace(
            $"""
             "formatMajor": {ExpertInvocationRecording.CurrentFormatMajor}
             """,
            """
            "formatMajor": 99
            """);

        var exception = Assert.Throws<ExpertRecordingException>(
            () => ExpertInvocationRecording.FromJson(json));

        Assert.Contains("format major 99", exception.Message, StringComparison.Ordinal);
        Assert.Contains("supports", exception.Message, StringComparison.Ordinal);
        Assert.Contains("delete it and record a new one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonRejectsMalformedJsonWithResetGuidance()
    {
        var exception = Assert.Throws<ExpertRecordingException>(
            () => ExpertInvocationRecording.FromJson("{ not json"));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Delete the recording file and record a new one", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public void FromJsonRejectsEmptyDocumentAndBlankInput()
    {
        Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson("null"));
        Assert.Throws<ArgumentException>(() => ExpertInvocationRecording.FromJson("  "));
        Assert.Throws<ArgumentException>(() => ExpertInvocationRecording.FromJson(""));
    }

    [Theory]
    [InlineData("formatMajor")]
    [InlineData("contract")]
    [InlineData("input")]
    [InlineData("terminal")]
    public void FromJsonRejectsDocumentsWithMissingRequiredSections(string missingSection)
    {
        var json = RemoveProperty(TestSupport.CreateRecording().ToJson(), missingSection);

        var exception = Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson(json));

        Assert.Contains(missingSection, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonRejectsBlankContractIdentityAndMissingContractVersion()
    {
        var blankId = SetProperty(TestSupport.CreateRecording().ToJson(), "contract", node => node["id"] = " ");
        var missingMajor = SetProperty(
            TestSupport.CreateRecording().ToJson(),
            "contract",
            node => ((JsonObject)node["version"]!).Remove("major"));

        var blankException = Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson(blankId));
        Assert.Contains("contract.id", blankException.Message, StringComparison.Ordinal);

        var versionException = Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson(missingMajor));
        Assert.Contains("contract.version.major", versionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonRejectsNullEventEntriesAndEventsWithoutPayload()
    {
        var nullEntry = MutateArray(TestSupport.CreateRecording().ToJson(), events => events[0] = null);
        var missingPayload = MutateArray(
            TestSupport.CreateRecording().ToJson(),
            events => ((JsonObject)events[0]!).Remove("payload"));

        var nullException = Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson(nullEntry));
        Assert.Contains("null event entry", nullException.Message, StringComparison.Ordinal);

        var payloadException = Assert.Throws<ExpertRecordingException>(
            () => ExpertInvocationRecording.FromJson(missingPayload));
        Assert.Contains("payload", payloadException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("committed", false, false, "requires a JSON output")]
    [InlineData("committed", true, true, "cannot carry an error message")]
    [InlineData("aborted", true, false, "Only a 'committed' terminal carries an output")]
    [InlineData("error", true, false, "Only a 'committed' terminal carries an output")]
    [InlineData("pending", true, false, "Terminal status must be")]
    public void FromJsonRejectsInvalidTerminalShapesAsActionableExceptions(
        string status,
        bool includeOutput,
        bool includeError,
        string expectedFragment)
    {
        var json = SetProperty(
            TestSupport.CreateRecording().ToJson(),
            "terminal",
            node =>
            {
                node["status"] = status;
                if (!includeOutput) node.Remove("output");
                switch (includeError)
                {
                    case true:
                        node["error"] = "boom";
                        break;
                    case false:
                        node.Remove("error");
                        break;
                }
            });

        var exception = Assert.Throws<ExpertRecordingException>(() => ExpertInvocationRecording.FromJson(json));

        Assert.Contains("invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorValidatesNullAndMalformedArguments()
    {
        var terminal = new ExpertRecordingTerminal(ExpertRecordingTerminal.Committed, TestSupport.Json("""{"ok":true}"""));
        var events = new[] { new ExpertSemanticEvent("narrative", TestSupport.Json("{}")) };

        Assert.Throws<ArgumentNullException>(
            () => new ExpertInvocationRecording(null!, TestSupport.Json("{}"), events, terminal));
        Assert.Throws<ArgumentException>(
            () => new ExpertInvocationRecording(TestSupport.Contract, default, events, terminal));
        Assert.Throws<ArgumentNullException>(
            () => new ExpertInvocationRecording(TestSupport.Contract, TestSupport.Json("{}"), null!, terminal));
        Assert.Throws<ArgumentException>(
            () => new ExpertInvocationRecording(
                TestSupport.Contract, TestSupport.Json("{}"),
                [events[0], null!], terminal));
        Assert.Throws<ArgumentNullException>(
            () => new ExpertInvocationRecording(TestSupport.Contract, TestSupport.Json("{}"), events, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExpertInvocationRecording(
                TestSupport.Contract, TestSupport.Json("{}"), events, terminal, durationMs: -1));
        Assert.Throws<ArgumentException>(
            () => new ExpertInvocationRecording(
                TestSupport.Contract, TestSupport.Json("{}"), events, terminal, formatMajor: 0));
    }

    [Fact]
    public void ConstructorClonesInputAndEventPayloads()
    {
        using var inputDocument = JsonDocument.Parse("""{"prompt":"hello"}""");
        using var payloadDocument = JsonDocument.Parse("""{"text":"hi"}""");
        var events = new[] { new ExpertSemanticEvent("narrative", payloadDocument.RootElement) };

        var recording = new ExpertInvocationRecording(
            TestSupport.Contract,
            inputDocument.RootElement,
            events,
            new ExpertRecordingTerminal(ExpertRecordingTerminal.Committed, payloadDocument.RootElement));

        Assert.True(JsonElement.DeepEquals(inputDocument.RootElement, recording.Input));
        Assert.True(JsonElement.DeepEquals(payloadDocument.RootElement, recording.Events[0].Payload));
    }

    private static string RemoveProperty(string json, string name) => Mutate(json, node => node.Remove(name));

    private static string SetProperty(string json, string name, Action<JsonObject> mutate) =>
        Mutate(json, node => mutate((JsonObject)node[name]!));

    private static string MutateArray(string json, Action<JsonArray> mutate) =>
        Mutate(json, node => mutate((JsonArray)node["events"]!));

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var node = (JsonObject)JsonNode.Parse(json)!;
        mutate(node);
        return node.ToJsonString();
    }
}
