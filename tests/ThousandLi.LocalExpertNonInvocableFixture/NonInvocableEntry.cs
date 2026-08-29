using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.LocalExpertNonInvocableFixture.NonInvocableAbstractExpert),
    typeof(ThousandLi.LocalExpertNonInvocableFixture.NonInvocableConcreteExpert))]

namespace ThousandLi.LocalExpertNonInvocableFixture;

[ExpertContract("tests.bad/non-invocable", 1, 0, "0000000000000000000000000000000000000000000000000000000000000000")]
public abstract class NonInvocableAbstractExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class NonInvocableConcreteExpert : NonInvocableAbstractExpert;
