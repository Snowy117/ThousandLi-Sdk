using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.ExpertContracts;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredNarratorExpert),
    typeof(ThousandLi.UnregisteredContractFixture.UnregisteredNarratorImplementation))]

namespace ThousandLi.UnregisteredContractFixture;

public interface IUnregisteredFeature : IExpertFeature;

[ExpertContract("tests.unregistered/narrator", 1, 0, "0fb414108c05b181387ae936fa3eebe3f07fb8a8ea5b7c102f7308a61e39a362")]
public abstract class UnregisteredNarratorExpert
    : ExpertBase<IUnregisteredFeature, UnregisteredNarratorExpert>, IExpertContract, IInvocableExpert
{
    public static ExpertContractDefinition Definition { get; } = new(semanticEventTypes: ["tick"]);

    public abstract Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);

    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class UnregisteredNarratorImplementation : UnregisteredNarratorExpert
{
    public override Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
