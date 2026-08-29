using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.ExpertContracts.Narration;

/// <summary>
/// Single inheritance forces contract identity ([ExpertContract] plus the static descriptor and
/// definition) and the authoring surface (inherited from RuntimeLongTextWritingExpertBase) to meet
/// in this one base class: concrete narrator experts derive from this type and implement InvokeAsync.
/// </summary>
[ExpertContract("thousandli.expert/narrator", 1, 0, "c514466424e626a6f24dfb5b53894c493351fb2466d30f1ba6e00e5153264b10")]
public abstract class AbstractNarratorExpert
    : RuntimeLongTextWritingExpertBase, IExpertContract, IInvocableExpert
{
    public static ExpertContractDescriptor Descriptor { get; } =
        new("thousandli.expert/narrator", new ContractVersion(1, 0), "c514466424e626a6f24dfb5b53894c493351fb2466d30f1ba6e00e5153264b10");

    public static ExpertContractDefinition Definition { get; } = new(
        inputSchema: ParseSchema("""
            {
              "type": "object",
              "properties": {
                "turn": { "type": "integer", "description": "1-based game turn this narration belongs to." },
                "player": { "type": "string", "description": "Display name of the acting player." },
                "action": { "type": "object", "description": "Opaque player action payload for this turn." }
              },
              "required": ["turn", "player", "action"]
            }
            """),
        semanticEventTypes: ["chunk"],
        outputSchema: ParseSchema("""
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "Completed narration text for the turn." }
              },
              "required": ["text"]
            }
            """));

    /// <inheritdoc />
    public abstract Task<JsonElement> InvokeAsync(
        JsonElement input,
        IExpertSemanticEventSink events,
        CancellationToken cancellationToken = default);

    private static JsonElement? ParseSchema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
