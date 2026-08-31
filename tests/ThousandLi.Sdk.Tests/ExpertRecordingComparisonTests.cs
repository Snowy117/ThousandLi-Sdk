using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertRecordingComparisonTests
{
    private const string OtherContractId = "tests/other";

    [Fact]
    public void IdenticalRecordingsMatchInDefaultAndStrictMode()
    {
        var expected = TestSupport.CreateRecording();
        var actual = TestSupport.CreateRecording();

        var defaultResult = ExpertRecordingComparison.Compare(expected, actual);
        var strictResult = ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true);

        Assert.True(RecordingComparisonResult.Match.Matches);
        Assert.True(defaultResult.Matches);
        Assert.Empty(defaultResult.Divergences);
        Assert.True(strictResult.Matches);
        Assert.Empty(strictResult.Divergences);
    }

    [Fact]
    public void EventTypeSequenceMismatchFailsWithFirstDivergenceDiagnostic()
    {
        var expected = TestSupport.CreateRecording(events: [("a", "{}"), ("b", "{}"), ("c", "{}")]);
        var actual = TestSupport.CreateRecording(events: [("a", "{}"), ("x", "{}"), ("y", "{}")]);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.EventTypeSequenceLayer, divergence.Layer);
        Assert.Contains("first divergence at event 1", divergence.Detail, StringComparison.Ordinal);
        Assert.Contains("'b'", divergence.Detail, StringComparison.Ordinal);
        Assert.Contains("'x'", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EventCountMismatchFailsTheSequenceLayer()
    {
        var expected = TestSupport.CreateRecording();
        var actual = TestSupport.CreateRecording(events: [("narrative", "{}"), ("narrative", "{}")]);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.EventTypeSequenceLayer, divergence.Layer);
        Assert.Contains("expected 1 events, actual 2", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadValueDifferencePassesDefaultModeButFailsStrictMode()
    {
        var expected = TestSupport.CreateRecording();
        var actual = TestSupport.CreateRecording(output: """{"text":"a different story"}""");

        var defaultResult = ExpertRecordingComparison.Compare(expected, actual);
        var strictResult = ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true);

        Assert.True(defaultResult.Matches);
        Assert.False(strictResult.Matches);
        var divergence = Assert.Single(strictResult.Divergences);
        Assert.Equal(ExpertRecordingComparison.StrictPayloadLayer, divergence.Layer);
        Assert.Contains("terminal.output differs", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EventPayloadValueDifferencePassesDefaultModeButFailsStrictMode()
    {
        var expected = TestSupport.CreateRecording(events: [("narrative", """{"text":"one"}""")]);
        var actual = TestSupport.CreateRecording(events: [("narrative", """{"text":"two"}""")]);

        var defaultResult = ExpertRecordingComparison.Compare(expected, actual);
        var strictResult = ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true);

        Assert.True(defaultResult.Matches);
        Assert.False(strictResult.Matches);
        var divergence = Assert.Single(strictResult.Divergences);
        Assert.Equal(ExpertRecordingComparison.StrictPayloadLayer, divergence.Layer);
        Assert.Contains("events[0].payload differs", divergence.Detail, StringComparison.Ordinal);
        Assert.Contains("one", divergence.Detail, StringComparison.Ordinal);
        Assert.Contains("two", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EventPayloadKeySetDifferenceFailsDefaultMode()
    {
        var expected = TestSupport.CreateRecording(events: [("narrative", """{"text":"a","mood":"calm"}""")]);
        var actual = TestSupport.CreateRecording(events: [("narrative", """{"text":"a","tone":"calm"}""")]);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.EventShapeLayer, divergence.Layer);
        Assert.Contains("missing: [mood]", divergence.Detail, StringComparison.Ordinal);
        Assert.Contains("extra: [tone]", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EventPayloadKindAndNestedShapeDifferencesFailDefaultMode()
    {
        var expectedKind = TestSupport.CreateRecording(events: [("narrative", """{"meta":{"depth":1}}""")]);
        var actualKind = TestSupport.CreateRecording(events: [("narrative", "\"plain\"")]);
        var kindResult = ExpertRecordingComparison.Compare(expectedKind, actualKind);
        Assert.False(kindResult.Matches);
        var kindDivergence = Assert.Single(kindResult.Divergences);
        Assert.Equal(ExpertRecordingComparison.EventShapeLayer, kindDivergence.Layer);
        Assert.Contains("expected kind Object, actual String", kindDivergence.Detail, StringComparison.Ordinal);

        var expectedNested = TestSupport.CreateRecording(events: [("narrative", """{"meta":{"depth":1}}""")]);
        var actualNested = TestSupport.CreateRecording(events: [("narrative", """{"meta":{"width":1}}""")]);
        var nestedResult = ExpertRecordingComparison.Compare(expectedNested, actualNested);
        Assert.False(nestedResult.Matches);
        var nestedDivergence = Assert.Single(nestedResult.Divergences);
        Assert.Contains("events[0].payload.meta", nestedDivergence.Detail, StringComparison.Ordinal);

        var expectedArray = TestSupport.CreateRecording(events: [("options", """{"items":[1,2,3]}""")]);
        var actualArray = TestSupport.CreateRecording(events: [("options", """{"items":[1]}""")]);
        var arrayResult = ExpertRecordingComparison.Compare(expectedArray, actualArray);
        Assert.False(arrayResult.Matches);
        var arrayDivergence = Assert.Single(arrayResult.Divergences);
        Assert.Contains("expected 3 array elements, actual 1", arrayDivergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectKeyOrderIsNotAShapeDifference()
    {
        var expected = TestSupport.CreateRecording(events: [("narrative", """{"a":1,"b":2}""")]);
        var actual = TestSupport.CreateRecording(events: [("narrative", """{"b":2,"a":1}""")]);

        var result = ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true);

        Assert.True(result.Matches);
    }

    [Fact]
    public void TerminalStatusDifferenceFailsDefaultMode()
    {
        var expected = TestSupport.CreateRecording();
        var actual = TestSupport.CreateRecording(terminalStatus: ExpertRecordingTerminal.Aborted);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.TerminalLayer, divergence.Layer);
        Assert.Contains("expected terminal 'committed', actual 'aborted'", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalOutputShapeDifferenceFailsDefaultMode()
    {
        var expected = TestSupport.CreateRecording(output: """{"text":"done","extra":1}""");
        var actual = TestSupport.CreateRecording(output: """{"text":"done"}""");

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.TerminalLayer, divergence.Layer);
        Assert.Contains("terminal.output", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractDifferenceFailsContractLayerAlone()
    {
        var expected = TestSupport.CreateRecording(contractId: OtherContractId);
        var actual = TestSupport.CreateRecording(events: [("other", "{}"), ("more", "{}")]);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal(ExpertRecordingComparison.ContractLayer, divergence.Layer);
        Assert.Contains(OtherContractId, divergence.Detail, StringComparison.Ordinal);
        Assert.Contains(TestSupport.ContractId, divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictModeComparesInputs()
    {
        var expected = TestSupport.CreateRecording(input: """{"prompt":"a"}""");
        var actual = TestSupport.CreateRecording(input: """{"prompt":"b"}""");

        var defaultResult = ExpertRecordingComparison.Compare(expected, actual);
        var strictResult = ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true);

        Assert.True(defaultResult.Matches);
        Assert.False(strictResult.Matches);
        var divergence = Assert.Single(strictResult.Divergences);
        Assert.Equal(ExpertRecordingComparison.StrictInputLayer, divergence.Layer);
        Assert.Contains("input differs", divergence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorTextIsNeverCompared()
    {
        var expected = TestSupport.CreateRecording(
            events: [],
            terminalStatus: ExpertRecordingTerminal.ErrorStatus,
            terminalError: "connection reset");
        var actual = TestSupport.CreateRecording(
            events: [],
            terminalStatus: ExpertRecordingTerminal.ErrorStatus,
            terminalError: "gateway timeout");

        Assert.True(ExpertRecordingComparison.Compare(expected, actual).Matches);
        Assert.True(ExpertRecordingComparison.Compare(expected, actual, strictPayloads: true).Matches);
    }

    [Fact]
    public void CompareRejectsNullArguments()
    {
        var recording = TestSupport.CreateRecording();

        Assert.Throws<ArgumentNullException>(() => ExpertRecordingComparison.Compare(null!, recording));
        Assert.Throws<ArgumentNullException>(() => ExpertRecordingComparison.Compare(recording, null!));
    }

    [Fact]
    public void SequenceMismatchStillComparesTerminalInDefaultMode()
    {
        var expected = TestSupport.CreateRecording();
        var actual = TestSupport.CreateRecording(
            events: [("other", "{}")],
            terminalStatus: ExpertRecordingTerminal.Aborted);

        var result = ExpertRecordingComparison.Compare(expected, actual);

        Assert.False(result.Matches);
        Assert.Equal(2, result.Divergences.Count);
        Assert.Equal(ExpertRecordingComparison.EventTypeSequenceLayer, result.Divergences[0].Layer);
        Assert.Equal(ExpertRecordingComparison.TerminalLayer, result.Divergences[1].Layer);
    }
}
