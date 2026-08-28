using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThousandLi.GameHelper;

internal static class SessionStateAiProjection
{
    public static JsonElement Project(SessionStateContract contract, JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (state.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SessionState root must be a JSON object.", nameof(state));

        var root = ProjectNode(contract, contract.GetSchemaNode(contract.RootType, aiFacingOnly: true), state)
                   ?? throw new InvalidOperationException("AI-facing SessionState root cannot be null.");
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ProjectNode(SessionStateContract contract, SessionStateSchemaNode schema, JsonElement value)
    {
        return schema switch
        {
            SessionStateTypeNode objectNode => ProjectObject(contract, objectNode, value),
            SessionStateListNode listNode => ProjectList(contract, listNode, value),
            SessionStateDictionaryNode dictionaryNode => ProjectDictionary(contract, dictionaryNode, value),
            _ => JsonNode.Parse(value.GetRawText()),
        };
    }

    private static JsonObject ProjectObject(SessionStateContract contract, SessionStateTypeNode schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SessionState object must be a JSON object.", nameof(value));

        var projected = new JsonObject();
        foreach (var member in schema.Members)
        {
            if (!value.TryGetProperty(member.JsonName, out var child)) continue;
            projected[member.JsonName] = ProjectNode(contract,
                contract.GetSchemaNode(member.Property.PropertyType, aiFacingOnly: true), child);
        }

        return projected;
    }

    private static JsonArray ProjectList(SessionStateContract contract, SessionStateListNode schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("SessionState list must be a JSON array.", nameof(value));

        var projected = new JsonArray();
        foreach (var item in value.EnumerateArray()) projected.Add(ProjectNode(contract, schema.Item, item));
        return projected;
    }

    private static JsonObject ProjectDictionary(SessionStateContract contract, SessionStateDictionaryNode schema,
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SessionState dictionary must be a JSON object.", nameof(value));

        var projected = new JsonObject();
        foreach (var property in value.EnumerateObject())
            projected[property.Name] = ProjectNode(contract, schema.Value, property.Value);
        return projected;
    }
}
