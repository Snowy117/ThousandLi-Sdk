using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.FirstLongTextWritingExpert))]
[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.SecondLongTextWritingExpert))]

namespace ThousandLi.LocalExpertMultiEntryFixture;

public sealed class FirstLongTextWritingExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class SecondLongTextWritingExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
