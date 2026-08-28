using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>变量更新命令的基类。</summary>
[JsonConverter(typeof(VariableUpdateOperationJsonConverter))]
public abstract record VariableUpdateOperation(string Path);

/// <summary>替换既有变量值。</summary>
public sealed record VariableUpdateReplace(string Path, JsonNode? Value) : VariableUpdateOperation(Path);

/// <summary>向集合或字典插入变量值。</summary>
public sealed record VariableUpdateInsert(string Path, JsonNode? Value) : VariableUpdateOperation(Path);

/// <summary>移除集合或字典中的变量值。</summary>
public sealed record VariableUpdateRemove(string Path) : VariableUpdateOperation(Path);

/// <summary>对数值变量应用增量。</summary>
public sealed record VariableUpdateDelta(string Path, decimal Value) : VariableUpdateOperation(Path);

/// <summary>变量更新 patch proposal（GameHelper PatchApplier / 专家变量更新第二遍调用共享）。</summary>
public sealed record VariableUpdatePatchProposal
{
    /// <summary>从操作列表创建 proposal；列表本身被防御性复制。</summary>
    public VariableUpdatePatchProposal(IReadOnlyList<VariableUpdateOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Operations = operations;
    }

    /// <summary>提案操作的防御性副本，调用方修改不会影响 proposal。</summary>
    public IReadOnlyList<VariableUpdateOperation> Operations
    {
        get => [.. field.Select(CloneOperation)];
        private init => field = Array.AsReadOnly([.. value.Select(CloneOperation)]);
    }

    /// <summary>从模型 JSON 数组反序列化 proposal。仅供 JSON 边界使用。</summary>
    public static VariableUpdatePatchProposal FromJson(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("VariableUpdate patch proposal must be a JSON array.", nameof(patch));

        var operations = JsonSerializer.Deserialize<List<VariableUpdateOperation>>(patch.GetRawText())
                         ?? throw new JsonException("VariableUpdate patch proposal could not be deserialized.");
        return new VariableUpdatePatchProposal(operations);
    }

    private static VariableUpdateOperation CloneOperation(VariableUpdateOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation switch
        {
            VariableUpdateReplace replace => replace with { Value = replace.Value?.DeepClone() },
            VariableUpdateInsert insert => insert with { Value = insert.Value?.DeepClone() },
            VariableUpdateRemove remove => remove,
            VariableUpdateDelta delta => delta,
            _ => throw new ArgumentException("VariableUpdate operation has an invalid value.", nameof(operation)),
        };
    }
}

/// <summary>按 <c>op</c> 字段读写变量更新命令。</summary>
public sealed class VariableUpdateOperationJsonConverter : JsonConverter<VariableUpdateOperation>
{
    /// <inheritdoc />
    public override VariableUpdateOperation Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject
                   ?? throw new JsonException("VariableUpdate patch command must be a JSON object.");
        var operation = RequiredString(node, "op");
        var path = RequiredString(node, "path");
        return operation switch
        {
            "replace" => new VariableUpdateReplace(path, RequiredValue(node)),
            "insert" => new VariableUpdateInsert(path, RequiredValue(node)),
            "remove" => new VariableUpdateRemove(path),
            "delta" => new VariableUpdateDelta(path, RequiredDecimal(node)),
            _ => throw new JsonException($"VariableUpdate operation '{operation}' is not supported."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VariableUpdateOperation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        switch (value)
        {
            case VariableUpdateReplace replace:
                WriteValueOperation(writer, "replace", replace.Path, replace.Value, options);
                break;
            case VariableUpdateInsert insert:
                WriteValueOperation(writer, "insert", insert.Path, insert.Value, options);
                break;
            case VariableUpdateRemove remove:
                writer.WriteString("op", "remove");
                writer.WriteString("path", remove.Path);
                break;
            case VariableUpdateDelta delta:
                writer.WriteString("op", "delta");
                writer.WriteString("path", delta.Path);
                writer.WriteNumber("value", delta.Value);
                break;
            default:
                throw new JsonException($"Unsupported VariableUpdate operation type '{value.GetType().FullName}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteValueOperation(Utf8JsonWriter writer, string operation, string path, JsonNode? value,
        JsonSerializerOptions options)
    {
        writer.WriteString("op", operation);
        writer.WriteString("path", path);
        writer.WritePropertyName("value");
        if (value is null)
            writer.WriteNullValue();
        else
            value.WriteTo(writer, options);
    }

    private static string RequiredString(JsonObject command, string propertyName)
    {
        if (command[propertyName] is not JsonValue value || !value.TryGetValue<string>(out var result) ||
            string.IsNullOrEmpty(result))
        {
            throw new JsonException($"VariableUpdate patch command requires string '{propertyName}'.");
        }

        return result;
    }

    private static JsonNode RequiredValue(JsonObject command)
    {
        if (!command.TryGetPropertyValue("value", out var value))
            throw new JsonException("VariableUpdate patch command requires 'value'.");
        return value?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }

    private static decimal RequiredDecimal(JsonObject command)
    {
        if (command["value"] is not JsonValue value || !value.TryGetValue<decimal>(out var result))
            throw new JsonException("VariableUpdate delta operation requires a decimal value.");
        return result;
    }
}

/// <summary>变量更新 Feature：由专家在主输出完成后生成 patch proposal，再交由 Game callback 应用。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class VariableUpdateFeature(
    Func<VariableUpdatePatchProposal, CancellationToken, ValueTask> onPatchProposed) : ILongTextWritingFeature
{
    /// <summary>完整 patch proposal callback。</summary>
    public Func<VariableUpdatePatchProposal, CancellationToken, ValueTask> OnPatchProposed { get; } =
        onPatchProposed ?? throw new ArgumentNullException(nameof(onPatchProposed));
}
