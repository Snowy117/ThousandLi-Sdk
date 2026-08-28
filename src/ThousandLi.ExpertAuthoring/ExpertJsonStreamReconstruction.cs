using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Rebuilds a complete JSON value from a recorded <see cref="JsonStreamEvent"/> sequence. Used by
/// orchestrators that record a streaming main pass and must feed the same JSON into a follow-up call.
/// </summary>
internal static class ExpertJsonStreamReconstruction
{
    internal static JsonElement BuildJsonElement(IEnumerable<JsonStreamEvent> events)
    {
        var node = BuildJsonNode(events)
                   ?? throw new InvalidOperationException("JSON stream did not contain a root value.");
        return node is not JsonObject
            ? throw new InvalidOperationException("JSON stream root must be an object.")
            : JsonSerializer.SerializeToElement(node);
    }

    // 一次性遍历事件序列重建 JSON 树：root/stack/currentPropertyName/currentStringBuilder 是相互依赖的
    // 遍历状态，拆分会引入大量参数传递，保留为单方法。
    private static JsonNode? BuildJsonNode(IEnumerable<JsonStreamEvent> events)
    {
        JsonNode? root = null;
        var stack = new Stack<JsonNode>();
        string? currentPropertyName = null;
        StringBuilder? currentStringBuilder = null;

        foreach (var evt in events)
        {
            switch (evt)
            {
                case JsonStreamObjectStartedEvent:
                    var obj = new JsonObject();
                    AddNode(obj);
                    stack.Push(obj);
                    break;
                case JsonStreamObjectCompletedEvent:
                    stack.Pop();
                    break;
                case JsonStreamArrayStartedEvent:
                    var arr = new JsonArray();
                    AddNode(arr);
                    stack.Push(arr);
                    break;
                case JsonStreamArrayCompletedEvent:
                    stack.Pop();
                    break;
                case JsonStreamPropertyNameEvent prop:
                    currentPropertyName = prop.Name;
                    break;
                case JsonStreamStringStartedEvent:
                    currentStringBuilder = new StringBuilder();
                    break;
                case JsonStreamStringChunkEvent chunk:
                    currentStringBuilder?.Append(chunk.Value);
                    break;
                case JsonStreamStringCompletedEvent:
                    AddNode(JsonValue.Create(currentStringBuilder?.ToString() ?? string.Empty));
                    currentStringBuilder = null;
                    break;
                case JsonStreamNumberValueEvent num:
                    AddNode(JsonNode.Parse(num.RawValue));
                    break;
                case JsonStreamBooleanValueEvent b:
                    AddNode(JsonValue.Create(b.Value));
                    break;
                case JsonStreamNullValueEvent:
                    AddNode(null);
                    break;
            }
        }

        return root;

        void AddNode(JsonNode? node)
        {
            if (root is null && stack.Count == 0)
            {
                root = node;
                return;
            }

            var parent = stack.Peek();
            switch (parent)
            {
                case JsonObject when currentPropertyName == null:
                    throw new InvalidOperationException(
                        "JsonObject requires a PropertyName before adding a value.");
                case JsonObject obj:
                    obj[currentPropertyName] = node;
                    currentPropertyName = null;
                    break;
                case JsonArray arr:
                    arr.Add(node);
                    break;
            }
        }
    }
}
