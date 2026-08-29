using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractNarratorExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.FirstNarratorExpert))]
[assembly: ExpertPackageEntryPoint(
    typeof(AbstractNarratorExpert),
    typeof(ThousandLi.LocalExpertMultiEntryFixture.SecondNarratorExpert))]

namespace ThousandLi.LocalExpertMultiEntryFixture;

public sealed class FirstNarratorExpert : AbstractNarratorExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class SecondNarratorExpert : AbstractNarratorExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
