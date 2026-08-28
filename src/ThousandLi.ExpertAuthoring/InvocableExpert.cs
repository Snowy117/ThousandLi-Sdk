using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Structured invocation bridge implemented by contract abstract expert bases. The local expert
/// executor binds an invocation's structured input and semantic event sink through this surface;
/// callback-dense authoring APIs stay inside the expert process and never cross the executor seam.
/// </summary>
public interface IInvocableExpert
{
    /// <summary>
    /// Executes one invocation. <paramref name="input"/> follows the contract input schema, semantic
    /// events stream to <paramref name="events"/> as they are produced, and the returned value
    /// follows the contract output schema.
    /// </summary>
    Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);
}
