// ReSharper disable ClassWithVirtualMembersNeverInherited.Local
// ReSharper disable UnusedMember.Global

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper.Tests;

public sealed class VariableUpdatePatchApplierTests
{
    private const string DefaultStateJson =
        """{"name":"Alice","nullableText":null,"counter":1,"bounded":5,"ratio":1.5,"nested":{"score":2,"note":"old"},"items":[1,2],"map":{"a":1,"b":2}}""";

    private static readonly SessionStateContract SContract = SessionStateContract.Create(typeof(TestVariables));

    [Fact]
    public void InsertRejectsSchemaOutsideFixedObjectProperty()
    {
        var state = State();

        var result = Apply(state,
            """
            [{"op":"insert","path":"/unknown","value":1}]
            """);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("schema-outside fixed object", StringComparison.Ordinal));
        AssertJson(DefaultStateJson, state.Snapshot);
    }

    [Fact]
    public void RemoveRejectsFixedObjectProperty()
    {
        var state = State();

        var result = Apply(state,
            """
            [{"op":"remove","path":"/name"}]
            """);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("cannot remove fixed object property", StringComparison.Ordinal));
        AssertJson(DefaultStateJson, state.Snapshot);
    }

    [Fact]
    public void ReplaceValidatesCompleteObjectShapeAndAppliesNormalizedObject()
    {
        var state = State();

        var invalid = Apply(state,
            """
            [{"op":"replace","path":"/nested","value":{"score":3,"extra":true}}]
            """);
        Assert.Equal(1, invalid.FailedOperationCount);
        Assert.Contains(invalid.Warnings, warning => warning.Contains("schema-outside property 'extra'", StringComparison.Ordinal));

        var result = Apply(state,
            """
            [{"op":"replace","path":"/nested","value":{"score":7,"note":"new"}}]
            """);

        AssertJson(
            """{"name":"Alice","nullableText":null,"counter":1,"bounded":5,"ratio":1.5,"nested":{"score":7,"note":"new"},"items":[1,2],"map":{"a":1,"b":2}}""",
            state.Snapshot);
        AssertPatchJson("""[{"op":"replace","path":"/nested","value":{"score":7,"note":"new"}}]""", result.AppliedPatch);
    }

    [Fact]
    public void ReplaceValidatesCompleteListAndDictionaryValues()
    {
        var state = State();

        var invalidList = Apply(state,
            """
            [{"op":"replace","path":"/items","value":[1,"bad"]}]
            """);
        Assert.Equal(1, invalidList.FailedOperationCount);

        var result = Apply(state,
            """
            [{"op":"replace","path":"/map","value":{"z":9,"c":3}}]
            """);

        AssertJson("""{"c":3,"z":9}""", state.Snapshot.GetProperty("map"));
        AssertPatchJson("""[{"op":"replace","path":"/map","value":{"c":3,"z":9}}]""", result.AppliedPatch);
    }

    [Fact]
    public void TrackedDictionaryInsertReplaceRemoveEnforceKeyRulesAndJsonPointerEscapes()
    {
        var state = State();

        var duplicateInsert = Apply(state,
            """
            [{"op":"insert","path":"/map/a","value":10}]
            """);
        var missingReplace = Apply(state,
            """
            [{"op":"replace","path":"/map/missing","value":10}]
            """);
        var privateInsert = Apply(state,
            """
            [{"op":"insert","path":"/map/_private","value":10}]
            """);
        Assert.Equal(1, duplicateInsert.FailedOperationCount);
        Assert.Equal(1, missingReplace.FailedOperationCount);
        Assert.Equal(1, privateInsert.FailedOperationCount);

        Apply(state,
            """
            [{"op":"insert","path":"/map/a~1b","value":3},{"op":"replace","path":"/map/a~1b","value":4},{"op":"remove","path":"/map/a~1b"}]
            """);

        AssertJson("""{"a":1,"b":2}""", state.Snapshot.GetProperty("map"));
    }

    [Fact]
    public void NullableReplaceAllowsNullOnlyForNullableMember()
    {
        var state = State();

        Apply(state,
            """
            [{"op":"replace","path":"/nullableText","value":null}]
            """);

        Assert.Equal(JsonValueKind.Null, state.Snapshot.GetProperty("nullableText").ValueKind);

        var result = Apply(state,
            """
            [{"op":"replace","path":"/counter","value":null}]
            """);
        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("does not allow null", StringComparison.Ordinal));
    }

    [Fact]
    public void DeltaAllowsFractionalTargetsSkipsInvalidIntegerFractionAndRejectsOverflow()
    {
        var state = State(
            """{"name":"Alice","nullableText":null,"counter":2147483647,"bounded":5,"ratio":1.5,"nested":{"score":2,"note":"old"},"items":[1,2],"map":{"a":1,"b":2}}""");

        var fractional = Apply(state,
            """
            [{"op":"delta","path":"/ratio","value":0.25}]
            """);
        Assert.Equal(1.75, state.Snapshot.GetProperty("ratio").GetDouble());
        AssertPatchJson("""[{"op":"delta","path":"/ratio","value":0.25}]""", fractional.AppliedPatch);

        var invalidIntegerFraction = Apply(state,
            """
            [{"op":"delta","path":"/counter","value":1.5}]
            """);
        Assert.Equal(1, invalidIntegerFraction.FailedOperationCount);
        Assert.Empty(invalidIntegerFraction.AppliedPatch);

        var overflow = Apply(state,
            """
            [{"op":"delta","path":"/counter","value":1}]
            """);
        Assert.Equal(1, overflow.FailedOperationCount);
        Assert.Empty(overflow.AppliedPatch);
    }

    [Fact]
    public void DeltaAllowsDecimalTargetsAndRecordsActualDecimalDelta()
    {
        var state = new GameState(Json("""{"balance":1.25}"""));
        var contract = SessionStateContract.Create(typeof(DecimalVariables));

        var result = VariableUpdatePatchApplier.Apply(state: state, contract: contract, operations: Operations(
                """
                [{"op":"delta","path":"/balance","value":0.35}]
                """),
                logger: NullLogger.Instance);

        Assert.Equal(1.60m, state.Snapshot.GetProperty("balance").GetDecimal());
        Assert.Equal(0, result.FailedOperationCount);
        AssertPatchJson("""[{"op":"delta","path":"/balance","value":0.35}]""", result.AppliedPatch);
    }

    [Fact]
    public void ApplySkipsInvalidCommandAndAppliesLaterValidCommands()
    {
        var state = State();

        var result = Apply(state,
            """
            [
              {"op":"replace","path":"/missing","value":1},
              {"op":"replace","path":"/name","value":"Bob"},
              {"op":"delta","path":"/ratio","value":0.5}
            ]
            """);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("/missing", StringComparison.Ordinal));
        Assert.Equal("Bob", state.Snapshot.GetProperty("name").GetString());
        Assert.Equal(2.0, state.Snapshot.GetProperty("ratio").GetDouble());
        AssertPatchJson(
            """[{"op":"replace","path":"/name","value":"Bob"},{"op":"delta","path":"/ratio","value":0.5}]""",
            result.AppliedPatch);
    }

    [Fact]
    public void Apply_WhenValidatorRejectsCommand_SkipsItAndAppliesLaterCommand()
    {
        var state = State();
        var result = VariableUpdatePatchApplier.Apply(
            state,
            SContract,
            [
                new VariableUpdateReplace("/name", JsonValue.Create("Rejected")),
                new VariableUpdateReplace("/nested/note", JsonValue.Create("Applied")),
            ],
            NullLogger.Instance,
            operation => !string.Equals(operation.Path, "/name", StringComparison.Ordinal));

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("rejected by the Game validator", StringComparison.Ordinal));
        Assert.Equal("Alice", state.Snapshot.GetProperty("name").GetString());
        Assert.Equal("Applied", state.Snapshot.GetProperty("nested").GetProperty("note").GetString());
        var applied = Assert.IsType<VariableUpdateReplace>(Assert.Single(result.AppliedPatch));
        Assert.Equal("/nested/note", applied.Path);
    }

    [Fact]
    public void Apply_WhenValidatorMutatesItsOperationCopy_AppliesOriginalValue()
    {
        var state = State();
        var sourceValue = new JsonObject { ["score"] = 7, ["note"] = "new" };

        var result = VariableUpdatePatchApplier.Apply(
            state,
            SContract,
            [new VariableUpdateReplace("/nested", sourceValue)],
            NullLogger.Instance,
            operation =>
            {
                var replace = Assert.IsType<VariableUpdateReplace>(operation);
                Assert.IsType<JsonObject>(replace.Value)["score"] = 99;
                return true;
            });

        Assert.Equal(7, state.Snapshot.GetProperty("nested").GetProperty("score").GetInt32());
        var applied = Assert.IsType<VariableUpdateReplace>(Assert.Single(result.AppliedPatch));
        Assert.Equal(7, Assert.IsType<JsonObject>(applied.Value)["score"]!.GetValue<int>());
    }

    [Fact]
    public void ApplyResult_DefensivelyClonesAppliedPatchValues()
    {
        var state = State();
        var result = Apply(state,
            """
            [{"op":"replace","path":"/nested","value":{"score":7,"note":"new"}}]
            """);

        var exposed = Assert.IsType<VariableUpdateReplace>(Assert.Single(result.AppliedPatch));
        Assert.IsType<JsonObject>(exposed.Value)["score"] = 99;

        var stored = Assert.IsType<VariableUpdateReplace>(Assert.Single(result.AppliedPatch));
        Assert.Equal(7, Assert.IsType<JsonObject>(stored.Value)["score"]!.GetValue<int>());
    }

    [Fact]
    public void ApplyLogsSkippedCommandWithOperationAndPath()
    {
        var state = State();
        var logger = new RecordingLogger();

        var result = VariableUpdatePatchApplier.Apply(state, SContract, Operations(
            """
            [
              {"op":"replace","path":"/missing","value":1},
              {"op":"replace","path":"/name","value":"Bob"}
            ]
            """), logger);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Equal("Bob", state.Snapshot.GetProperty("name").GetString());
        var record = Assert.Single(logger.Records,
            entry => entry.EventId.Id == VariableUpdateLogEventIds.VariableUpdateCommandSkippedId);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("replace", record.Message, StringComparison.Ordinal);
        Assert.Contains("/missing", record.Message, StringComparison.Ordinal);
        Assert.NotNull(record.Exception);
    }

    [Fact]
    public void MinMaxClampWarningsAreRecordedForReplaceAndDeltaAndAppliedPatchUsesClampedValues()
    {
        var state = State();

        var result = Apply(state,
            """
            [{"op":"replace","path":"/bounded","value":99},{"op":"delta","path":"/bounded","value":-100}]
            """);

        Assert.Equal(0, state.Snapshot.GetProperty("bounded").GetInt32());
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, warning => Assert.Contains("VariableUpdate clamped", warning, StringComparison.Ordinal));
        AssertPatchJson("""[{"op":"replace","path":"/bounded","value":10},{"op":"delta","path":"/bounded","value":-10}]""",
            result.AppliedPatch);
    }

    [Fact]
    public void SnapshotShapeMismatchFailsAsVariableUpdateValidationException()
    {
        var state = State(
            """{"name":"Alice","nullableText":null,"counter":1,"bounded":5,"ratio":1.5,"nested":{"score":2,"note":"old"},"items":{},"map":{"a":1,"b":2}}""");

        var result = Apply(state,
            """
            [{"op":"insert","path":"/items/0","value":3}]
            """);

        Assert.Equal(1, result.FailedOperationCount);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("list path does not point to a JSON array", StringComparison.Ordinal));
    }

    private static GameState State(string json = DefaultStateJson)
    {
        return new GameState(Json(json));
    }

    private static VariableUpdateApplyResult Apply(GameState state, string patchJson)
    {
        return VariableUpdatePatchApplier.Apply(state, SContract, Operations(patchJson), NullLogger.Instance);
    }

    private static IReadOnlyList<VariableUpdateOperation> Operations(string json)
    {
        return VariableUpdatePatchProposal.FromJson(Json(json)).Operations;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertJson(string expectedJson, JsonElement actual)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual),
            $"Expected {expectedJson}, got {actual.GetRawText()}.");
    }

    private static void AssertPatchJson(string expectedJson, IReadOnlyList<VariableUpdateOperation> actual)
    {
        AssertJson(expectedJson, JsonSerializer.SerializeToElement(actual));
    }

    [SessionStateRoot]
    private class TestVariables
    {
        [AiStateMember(0, "名称")] public virtual string Name { get; set; } = "Alice";

        [AiStateMember(1, "可空文本")] public virtual string? NullableText { get; set; }

        [AiStateMember(2, "计数")] public virtual int Counter { get; set; } = 1;

        [AiStateMember(3, "有界值", Min = "0", Max = "10")]
        public virtual int Bounded { get; set; } = 5;

        [AiStateMember(4, "比例")] public virtual double Ratio { get; set; } = 1.5;

        [AiStateMember(5, "嵌套对象")] public virtual NestedVariables Nested { get; set; } = new();

        [AiStateMember(6, "列表")] public virtual TrackedList<int> Items { get; set; } = [1, 2];

        [AiStateMember(7, "字典")]
        public virtual TrackedDictionary<int> Map { get; set; } = new() { ["a"] = 1, ["b"] = 2 };
    }

    private class NestedVariables
    {
        [AiStateMember(0, "分数")] public virtual int Score { get; set; } = 2;

        [AiStateMember(1, "备注")] public virtual string Note { get; set; } = "old";
    }

    [SessionStateRoot]
    private class DecimalVariables
    {
        [AiStateMember(0, "余额")] public virtual decimal Balance { get; set; }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(new LogRecord(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed record LogRecord(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}
