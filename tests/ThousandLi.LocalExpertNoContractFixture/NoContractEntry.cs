using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.LocalExpertNoContractFixture.LocalAbstractExpert),
    typeof(ThousandLi.LocalExpertNoContractFixture.LocalConcreteExpert))]

namespace ThousandLi.LocalExpertNoContractFixture;

public interface ILocalFeature : IExpertFeature;

public abstract class LocalAbstractExpert
    : ExpertBase<ILocalFeature, LocalAbstractExpert>, IInvocableExpert
{
    public abstract Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class LocalConcreteExpert : LocalAbstractExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
