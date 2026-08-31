using ThousandLi.Contracts;
using ThousandLi.Testing;

namespace ThousandLi.Sdk.Tests;

/// <summary>
///     组合引擎 <see cref="ExpertSessionComposition" /> 公开契约的直接单元测试：构造期绑定校验、
///     未知契约 id 的拒绝路径、包覆盖类型校验与 per-invocation 实例/上下文工厂语义。
///     facade 视角与重复 contract id 场景由 LocalExpertFacadeTests 与平台 ExpertRuntimeContextTests 覆盖。
/// </summary>
public sealed class ExpertSessionCompositionTests
{
    private const string ContractId = AbstractLongTextWritingExpert.ContractId;

    private static ExpertSessionBinding Binding(
        string contractId = ContractId,
        Type? abstractType = null,
        Func<ExpertBase>? factory = null,
        ExpertStructuredInvoker? invoker = null)
    {
        factory ??= static () => new RecordingLongTextWritingExpert();
        invoker ??= static (_, _, _, _, _, _) => throw new InvalidOperationException(
            "The structured invoker is not exercised by this test.");
        return new ExpertSessionBinding(
            contractId,
            abstractType ?? typeof(AbstractLongTextWritingExpert),
            factory,
            invoker);
    }

    private static ExpertSessionComposition CreateComposition(params ExpertSessionBinding[] bindings) => new(
        bindings,
        static _ => FakeExpertExecutionContext.Instance,
        invocationPrefix: "test");

    [Fact]
    public void ConstructorRejectsBlankContractId()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateComposition(Binding(" ")));

        Assert.Contains("blank contract id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsAbstractTypeOutsideTheExpertBaseHierarchy()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateComposition(Binding(abstractType: typeof(string))));

        Assert.Contains("does not inherit ExpertBase", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsTheSameAbstractTypeBoundToTwoContracts()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateComposition(
            Binding("tests/first"),
            Binding("tests/second")));

        Assert.Contains("bound to more than one contract", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'tests/first' and 'tests/second'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateExpertThrowsForUnknownContractIdAndListsRegisteredContracts()
    {
        var composition = CreateComposition(Binding());

        var exception = Assert.Throws<InvalidOperationException>(() => composition.CreateExpert("tests/missing"));

        Assert.Contains("No expert binding is registered", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{ContractId}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncThrowsForUnknownContractId()
    {
        var composition = CreateComposition(Binding());
        var request = new ExpertInvocationRequest("tests/missing", "default", TestSupport.Json("{}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composition.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken)
                .AsTask());

        Assert.Contains("No expert binding is registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateExpertOverrideRejectsExpertOutsideTheBoundAnchor()
    {
        var composition = CreateComposition(Binding());

        var exception = Assert.Throws<InvalidOperationException>(
            () => composition.CreateExpert(ContractId, () => new UnrelatedExpert()));

        Assert.Contains("must inherit", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(AbstractLongTextWritingExpert).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseReturnsFreshBoundInstancesPerCall()
    {
        var composition = CreateComposition(Binding());

        var first = composition.Use<AbstractLongTextWritingExpert>();
        var second = composition.Use<AbstractLongTextWritingExpert>();

        Assert.NotSame(first, second);
        Assert.Throws<InvalidOperationException>(() => first.Bind(FakeExpertExecutionContext.Instance));
        Assert.Throws<InvalidOperationException>(() => second.Bind(FakeExpertExecutionContext.Instance));
    }

    [Fact]
    public async Task ExecuteAsyncDrivesTheStructuredInvokerWithFactoryInstanceAndContext()
    {
        var output = TestSupport.Json("""{"text":"done"}""");
        ExpertBase? seenExpert = null;
        IExpertExecutionContext? seenContext = null;
        string? seenContractId = null;
        var composition = new ExpertSessionComposition(
            [Binding(invoker: (expert, request, _, context, _, _) =>
            {
                seenExpert = expert;
                seenContext = context;
                seenContractId = request.ContractId;
                return Task.FromResult(output);
            })],
            static _ => FakeExpertExecutionContext.Instance,
            invocationPrefix: "test");
        var request = new ExpertInvocationRequest(ContractId, "default", TestSupport.Json("""{"prompt":"hi"}"""));

        var result = await composition.ExecuteAsync(request, new RecordingSemanticSink(), TestSupport.CancellationToken);

        Assert.IsType<RecordingLongTextWritingExpert>(seenExpert);
        Assert.Equal(ContractId, seenContractId);
        Assert.Same(FakeExpertExecutionContext.Instance, seenContext);
        Assert.Equal(output.GetRawText(), result.Output.GetRawText());
        Assert.StartsWith("test-", result.InvocationId, StringComparison.Ordinal);
    }

    private sealed class UnrelatedExpert : ExpertBase
    {
        protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
            Task.FromResult(new ExpertCompletionResult());

        protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
            StreamAsyncCore(cancellationToken);
    }
}
