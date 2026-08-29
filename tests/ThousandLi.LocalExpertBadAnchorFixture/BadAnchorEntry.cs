using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.LocalExpertBadAnchorFixture.ForeignAbstractExpert),
    typeof(ThousandLi.LocalExpertBadAnchorFixture.ForeignConcreteExpert))]

namespace ThousandLi.LocalExpertBadAnchorFixture;

[ExpertContract("tests.bad/anchor", 1, 0, "0000000000000000000000000000000000000000000000000000000000000000")]
public abstract class ForeignAbstractExpert : IInvocableExpert
{
    public abstract Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);
}

public sealed class ForeignConcreteExpert : ForeignAbstractExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
