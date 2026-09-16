using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredLongTextWritingExpert),
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredLongTextWritingImplementation))]

namespace ThousandLi.UnregisteredContractFixture;

/// <summary>携带 SDK 未知契约 id 的合法形状锚（本地组合绑定校验应拒绝其未知契约）。</summary>
[ExpertContract("tests.unregistered/story")]
public abstract class UnregisteredLongTextWritingExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class UnregisteredLongTextWritingImplementation : UnregisteredLongTextWritingExpert;
