using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// FakeExpertFacade 契约测试 + 官方包测试迁移的标准 stub 模式样例：
/// fake facade 注册「new + Bind + return」工厂闭包，stub 专家派生
/// 类别锚（AbstractLongTextWritingExpert）并经 ExpertExecution 重放 RecordedBasicAi 的录制流。
/// </summary>
public sealed class FakeExpertFacadeTests
{
    [Fact]
    public void UseReturnsFreshExpertPerCall()
    {
        var created = 0;
        var facade = new FakeExpertFacade();
        facade.Register<AbstractLongTextWritingExpert>(() =>
        {
            created++;
            return new CountingStubExpert();
        });

        var first = facade.Use<AbstractLongTextWritingExpert>();
        var second = facade.Use<AbstractLongTextWritingExpert>();

        Assert.NotSame(first, second);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task ConvenienceRegistrationBindsTheDefaultExecutionContext()
    {
        var facade = new FakeExpertFacade();
        facade.Register<AbstractLongTextWritingExpert, CountingStubExpert>();

        var first = facade.Use<AbstractLongTextWritingExpert>();
        var second = facade.Use<AbstractLongTextWritingExpert>();

        Assert.NotSame(first, second);
        // Reaching the stub's StreamAsyncCore (instead of the unbound-context guard) proves the
        // convenience registration bound the default fake execution context automatically.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => first.WithWorldSettings("world").WithPlayerInput("go").StreamAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => second.WithWorldSettings("world").WithPlayerInput("go").CompleteAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UseThrowsForUnregisteredAbstractType()
    {
        var facade = new FakeExpertFacade();

        var exception = Assert.Throws<InvalidOperationException>(
            facade.Use<AbstractLongTextWritingExpert>);

        Assert.Contains(
            $"No Expert implementation is registered for abstract expert '{typeof(AbstractLongTextWritingExpert).FullName}'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UseThrowsWhenFactoryReturnsNull()
    {
        var facade = new FakeExpertFacade();
        facade.Register<AbstractLongTextWritingExpert>(() => null!);

        var exception = Assert.Throws<InvalidOperationException>(
            facade.Use<AbstractLongTextWritingExpert>);

        Assert.Contains("returned null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StubExpertPatternStreamsCannedEventsThroughExpertExecution()
    {
        var basicAi = new RecordedBasicAi(
            ["test-model"],
            [],
            [new RecordedRuntimeBasicAiInteraction(
                "test-model",
                streamEvents:
                [
                    new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectStarted("")),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "雪之下")),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/narrative", "看着远方")),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/narrative")),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.StringChunk("/afterFormat", "格式化")),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.StringCompleted("/afterFormat")),
                    new BasicAiReasoningStreamEvent("推理"),
                    new BasicAiJsonStreamEvent(JsonStreamEvent.ObjectCompleted("")),
                ])]);
        var executionContext = new LocalExpertExecutionContext(
            basicAi,
            new BoundPlayerProfile(new PlayerId("test-player"), "TestPlayer", "Test persona"),
            NullLogger.Instance);
        var facade = new FakeExpertFacade();
        facade.Register<AbstractLongTextWritingExpert>(() =>
        {
            var expert = new StubLongTextWritingExpert();
            expert.Bind(executionContext);
            return expert;
        });

        var narrativeDeltas = new List<string>();
        var reasoningDeltas = new List<string>();
        var result = await facade.Use<AbstractLongTextWritingExpert>()
            .WithWorldSettings("world")
            .WithPlayerInput("go")
            .WithPrimaryOutput(new TextPrimaryOutput((evt, _) =>
            {
                narrativeDeltas.Add(evt.Delta);
                return ValueTask.CompletedTask;
            }))
            .WithReasoningHandler((evt, _) =>
            {
                reasoningDeltas.Add(evt.Delta);
                return ValueTask.CompletedTask;
            })
            .StreamAsync(TestSupport.CancellationToken);

        Assert.Equal(["雪之下", "看着远方"], narrativeDeltas);
        Assert.Equal(["推理"], reasoningDeltas);
        Assert.Equal("推理", result.Reasoning);
        Assert.NotNull(result.Metadata);
        Assert.Equal("格式化", result.Metadata!["afterFormat"]);
        var invocation = Assert.Single(basicAi.RuntimeInvocations);
        Assert.Equal("test-model", invocation.ModelId);
    }

    /// <summary>
    /// 样例 stub 专家：直接派生类别锚（获得 RuntimeContext/MetadataFieldNames/绑定面），
    /// StreamAsyncCore override 里构建 request + sink 后交给 ExpertExecution 单调用执行。
    /// </summary>
    private sealed class StubLongTextWritingExpert : AbstractLongTextWritingExpert
    {
        private static readonly IReadOnlySet<string> SDeclaredMetadataFields =
            new HashSet<string>(StringComparer.Ordinal) { "afterFormat" };

        protected override IReadOnlySet<string> MetadataFieldNames => SDeclaredMetadataFields;

        protected override async Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
        {
            ValidateCategoryInputs();
            var textOutput = (TextPrimaryOutput)(ConfiguredPrimaryOutput
                ?? throw new InvalidOperationException("The stub expert requires a primary output."));
            var request = new BasicAiRequest(
                "test-model",
                [BasicAiMessage.User("test")],
                AiJsonSchema.Object());
            return await ExpertExecution.StreamOnceAsync(
                    this, request, new PrimaryOutputSink(textOutput), cancellationToken)
                .ConfigureAwait(false);
        }

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken)
            => throw new NotSupportedException("The stub expert sample only exercises streaming.");
    }

    /// <summary>样例最小 sink：把主输出属性的字符串增量转发给 TextPrimaryOutput callback。</summary>
    private sealed class PrimaryOutputSink(TextPrimaryOutput primaryOutput) : IJsonExpertStreamEventSink
    {
        public async ValueTask OnEventAsync(JsonStreamEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt is not JsonStreamStringChunkEvent chunk)
                return;
            if (!string.Equals(chunk.Path, "/" + primaryOutput.PropertyName, StringComparison.Ordinal))
                return;

            await primaryOutput.OnDelta(new TextDeltaEvent(chunk.Value), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CountingStubExpert : AbstractLongTextWritingExpert
    {
        protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
            => throw new NotSupportedException("Counting stub never executes.");

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken)
            => throw new NotSupportedException("Counting stub never executes.");
    }
}
