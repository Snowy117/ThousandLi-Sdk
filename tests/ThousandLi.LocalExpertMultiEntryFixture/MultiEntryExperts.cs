using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.FirstLongTextWritingExpert))]
[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.SecondLongTextWritingExpert))]

namespace ThousandLi.LocalExpertMultiEntryFixture;

public sealed class FirstLongTextWritingExpert : AbstractLongTextWritingExpert, IInvocableExpert
{
    public Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class SecondLongTextWritingExpert : AbstractLongTextWritingExpert, IInvocableExpert
{
    public Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
