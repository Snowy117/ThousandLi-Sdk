using System.Text;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.DevHost;

namespace ThousandLi.Sdk.Tests;

public sealed class ScriptedLongTextWritingExpertTests
{
    private static ScriptedLongTextWritingScenario DefaultScenario { get; } = new(
        "default",
        TestSupport.Json("""
            {
              "narrative": "A New Path Opens.",
              "actionOptions": ["Continue", "Flee"],
              "timeTagStart": "2026",
              "timeTagEnd": "2027",
              "afterThinking": "thought",
              "afterFormat": "formatted"
            }
            """),
        metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["extraKey"] = "extra" },
        reasoning: "reasoned");

    [Fact]
    public async Task StreamAsyncDrivesCallbacksAndReturnsMetadataAndReasoning()
    {
        var facade = new ScriptedLongTextWritingExpertFacade([DefaultScenario]);
        var narrative = new StringBuilder();
        var completed = new List<string>();
        var options = new List<string>();
        var timeTagStarts = new StringBuilder();
        var timeTagEnds = new StringBuilder();
        var buckets = new InMemoryHistoryBucketSet();
        buckets.Create("main", "主叙事历史");
        buckets["main"].AddMessages(null, null, ChatMessage.User("prior"));

        var builder = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithCurrentState("{}")
            .WithHistoryBuckets(buckets["main"])
            .WithFeatures(
                new TimeTagsFeature(
                    (tag, _) => { timeTagStarts.Append(tag.Delta); return ValueTask.CompletedTask; },
                    (tag, _) => { timeTagEnds.Append(tag.Delta); return ValueTask.CompletedTask; }),
                new ActionOptionsFeature(6, (option, _) =>
                {
                    options.Add(option.Text);
                    return ValueTask.CompletedTask;
                }))
            .WithPrimaryOutput(new TextPrimaryOutput(
                (delta, _) => { narrative.Append(delta.Delta); return ValueTask.CompletedTask; },
                onCompleted: (completedEvent, _) =>
                {
                    completed.Add(completedEvent.Text);
                    return ValueTask.CompletedTask;
                }));

        var result = await builder.StreamAsync(TestSupport.CancellationToken);

        Assert.Equal("A New Path Opens.", narrative.ToString());
        Assert.Equal(["A New Path Opens."], completed);
        Assert.Equal(["Continue", "Flee"], options);
        Assert.Equal("2026", timeTagStarts.ToString());
        Assert.Equal("2027", timeTagEnds.ToString());
        Assert.Equal("reasoned", result.Reasoning);
        Assert.Equal("extra", result.Metadata!["extraKey"]);
        Assert.Equal("thought", result.Metadata["afterThinking"]);
        Assert.Equal("formatted", result.Metadata["afterFormat"]);
        // 专家读到了历史桶：1 个已提交回合。
        Assert.Equal("1", result.Metadata["historyTurns"]);
        Assert.Equal("1", result.Metadata["history.主叙事历史"]);
    }

    [Fact]
    public async Task CompleteAsyncProducesSameDeterministicOutput()
    {
        var facade = new ScriptedLongTextWritingExpertFacade([DefaultScenario]);
        var firstNarrative = new StringBuilder();
        var first = await facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPrimaryOutput(new TextPrimaryOutput(
                (delta, _) => { firstNarrative.Append(delta.Delta); return ValueTask.CompletedTask; }))
            .CompleteAsync(TestSupport.CancellationToken);
        var secondNarrative = new StringBuilder();
        var second = await facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPrimaryOutput(new TextPrimaryOutput(
                (delta, _) => { secondNarrative.Append(delta.Delta); return ValueTask.CompletedTask; }))
            .CompleteAsync(TestSupport.CancellationToken);

        Assert.Equal("A New Path Opens.", firstNarrative.ToString());
        Assert.Equal(firstNarrative.ToString(), secondNarrative.ToString());
        Assert.Equal(first.Metadata!.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            second.Metadata!.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task StreamAsyncWithoutCategoryInputsThrows()
    {
        var facade = new ScriptedLongTextWritingExpertFacade([DefaultScenario]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.Use<AbstractLongTextWritingExpert>().StreamAsync(TestSupport.CancellationToken));

        Assert.Contains("world settings", exception.Message);
    }

    [Fact]
    public async Task JsonPrimaryOutputReceivesPrimaryJsonStreamEvents()
    {
        var facade = new ScriptedLongTextWritingExpertFacade([DefaultScenario]);
        var paths = new List<string>();
        var builder = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPrimaryOutput(new JsonPrimaryOutput(
                "narrative",
                AiJsonSchema.String(),
                (jsonEvent, _) =>
                {
                    paths.Add(jsonEvent.Event.Path);
                    return ValueTask.CompletedTask;
                }));

        await builder.StreamAsync(TestSupport.CancellationToken);

        Assert.Contains("/narrative", paths);
        Assert.Contains(paths, path => path.StartsWith("/narrative", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("timeTagStart", "2026")]
    [InlineData("timeTagEnd", "2027")]
    public async Task TimeTagsFeatureChunksItsField(string field, string expected)
    {
        var scenario = new ScriptedLongTextWritingScenario(
            field,
            JsonDocument.Parse($$"""{"{{field}}":"{{expected}}"}""").RootElement.Clone());
        var facade = new ScriptedLongTextWritingExpertFacade([scenario]);
        var received = new StringBuilder();
        var builder = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPrimaryOutput(new TextPrimaryOutput((_, _) => ValueTask.CompletedTask))
            .WithFeatures(
                field == "timeTagStart"
                    ? new TimeTagsFeature((tag, _) =>
                    {
                        received.Append(tag.Delta);
                        return ValueTask.CompletedTask;
                    }, onEndTag: null)
                    : new TimeTagsFeature(onStartTag: null, (tag, _) =>
                    {
                        received.Append(tag.Delta);
                        return ValueTask.CompletedTask;
                    }));

        await builder.StreamAsync(TestSupport.CancellationToken);

        Assert.Equal(expected, received.ToString());
    }

    [Fact]
    public async Task LoadReadsScriptedOutputEntriesFromScenarioFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-scenarios-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                [
                  {
                    "scenarioId": "advance",
                    "contract": "x",
                    "events": [ { "eventType": "chunk", "payload": { "text": "raw" } } ],
                    "result": { "text": "raw" },
                    "output": { "narrative": "Scripted!" },
                    "metadata": { "fromFile": "yes" },
                    "reasoning": "file-reasoned"
                  }
                ]
                """, TestSupport.CancellationToken);

            var facade = ScriptedLongTextWritingExpertFacade.Load(path);
            var expert = facade.Use<AbstractLongTextWritingExpert>();
            var narrative = new StringBuilder();
            var builder = expert.WithWorldSettings("world").WithPlayerInput("input")
                .WithPrimaryOutput(new TextPrimaryOutput(
                    (delta, _) => { narrative.Append(delta.Delta); return ValueTask.CompletedTask; }));

            var result = await builder.CompleteAsync(TestSupport.CancellationToken);

            Assert.Equal("Scripted!", narrative.ToString());
            Assert.Equal("file-reasoned", result.Reasoning);
            Assert.Equal("yes", result.Metadata!["fromFile"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyFacadeStreamsEmptyOutputWithoutThrowing()
    {
        var facade = ScriptedLongTextWritingExpertFacade.Empty();

        var builder = facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("input")
            .WithPrimaryOutput(new TextPrimaryOutput((_, _) => ValueTask.CompletedTask));

        var result = await builder.StreamAsync(TestSupport.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void EmptyScenarioListIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScriptedLongTextWritingExpertFacade([]));
    }
}
