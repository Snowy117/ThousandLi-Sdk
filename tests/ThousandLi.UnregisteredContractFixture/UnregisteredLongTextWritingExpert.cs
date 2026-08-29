using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredLongTextWritingExpert),
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredLongTextWritingImplementation))]

namespace ThousandLi.UnregisteredContractFixture;

[ExpertContract("tests.unregistered/story", 1, 0, "966023b5ce250c29dbaacd38653d8529b48815060152f8fff4e454cca441eed1")]
public abstract class UnregisteredLongTextWritingExpert
    : AbstractLongTextWritingExpert, IExpertContract, IInvocableExpert
{
    public new static ExpertContractDefinition Definition { get; } = new(semanticEventTypes: ["tick"]);

    public abstract Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class UnregisteredLongTextWritingImplementation : UnregisteredLongTextWritingExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
