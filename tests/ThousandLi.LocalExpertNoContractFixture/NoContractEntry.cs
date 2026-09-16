using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.LocalExpertNoContractFixture.NoContractAbstractExpert),
    typeof(ThousandLi.LocalExpertNoContractFixture.NoContractConcreteExpert))]

namespace ThousandLi.LocalExpertNoContractFixture;

/// <summary>继承锚类型但未携带 <c>[ExpertContract]</c> 的坏形状锚（装载校验应拒绝）。</summary>
public abstract class NoContractAbstractExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class NoContractConcreteExpert : NoContractAbstractExpert;
