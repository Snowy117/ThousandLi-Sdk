using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThousandLi.Contracts;

/// <summary>
/// 从 <see cref="JsonStreamEvent"/> 序列重建完整 JSON 值的扩展。供录制了流式主输出后需要把
/// 同一 JSON 喂给后续调用的编排器使用。
/// </summary>
public static class JsonStreamExtensions
{
    extension(IEnumerable<JsonStreamEvent> events)
    {
        /// <summary>重建 JSON 树并要求根为对象；根缺失或不是对象时抛出 <see cref="InvalidOperationException"/>。</summary>
        public JsonElement BuildJsonElement()
        {
            var root = JsonStreamReassembly.BuildRootNode(events);

            return root switch
            {
                null => throw new InvalidOperationException("JSON stream did not contain a root value."),
                not JsonObject => throw new InvalidOperationException("JSON stream root must be an object."),
                _ => JsonSerializer.SerializeToElement(root),
            };
        }

        /// <summary>
        /// 重建 JSON 树并允许任意形状的根（对象/数组/标量）；流为空时抛出
        /// <see cref="InvalidOperationException"/>。wire 完成帧的 json 主输出聚合使用。
        /// </summary>
        public JsonElement BuildJsonValue()
        {
            var materialized = events.ToList();
            if (materialized.Count == 0)
                throw new InvalidOperationException("JSON stream did not contain a root value.");

            var root = JsonStreamReassembly.BuildRootNode(materialized);
            return JsonSerializer.SerializeToElement(root);
        }
    }
}

/// <summary>
/// <see cref="JsonStreamEvent"/> 序列到 <see cref="JsonNode"/> 的共享栈式重组器：
/// <see cref="JsonStreamExtensions.BuildJsonElement"/>（对象根）与
/// <see cref="JsonStreamExtensions.BuildJsonValue"/>（任意根）共用同一实现，防二次实现漂移。
/// </summary>
internal static class JsonStreamReassembly
{
    public static JsonNode? BuildRootNode(IEnumerable<JsonStreamEvent> events)
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

        // root/stack/currentPropertyName/currentStringBuilder 是相互依赖的遍历状态，
        // 拆分会引入大量参数传递，保留为局部函数。
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
