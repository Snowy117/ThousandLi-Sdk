using System.Globalization;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>AI 目标架构辅助节点的基类型和工厂。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract record AiJsonSchema
{
    /// <summary>从统一 AST JSON 表示还原 AI 架构。</summary>
    /// <param name="json">JSON AST 架构。</param>
    /// <returns>还原后的 AI 架构。</returns>
    public static AiJsonSchema FromJson(JsonElement json)
    {
        return Parse(json, "$root");

        static AiJsonSchema Parse(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"AI schema node '{path}' must be an object AST node.", nameof(element));
            }

            if (!element.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"AI schema node '{path}' must contain string property 'kind'.",
                    nameof(element));
            }

            var kind = kindElement.GetString();
            return kind switch
            {
                "string" => String(),
                "number" => Number(),
                "boolean" => Boolean(),
                "array" => ParseArray(element, path),
                "object" => ParseObject(element, path),
                "enum" => ParseEnum(element, path),
                "nullable" => Parse(element.GetProperty("value"), $"{path}.value").Nullable(),
                "optional" => Parse(element.GetProperty("value"), $"{path}.value").Optional(),
                _ => throw new ArgumentException($"AI schema node '{path}' has unsupported kind '{kind}'.",
                    nameof(element))
            };
        }

        static AiJsonSchema ParseArray(JsonElement element, string path)
        {
            if (!element.TryGetProperty("item", out var itemElement))
            {
                throw new ArgumentException($"AI array schema '{path}' must contain property 'item'.",
                    nameof(element));
            }

            return Array(Parse(itemElement, $"{path}.item"));
        }

        static AiJsonSchema ParseEnum(JsonElement element, string path)
        {
            if (!element.TryGetProperty("values", out var valuesElement) ||
                valuesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException($"AI enum schema '{path}' must contain array property 'values'.",
                    nameof(element));
            }

            return Enum([.. valuesElement.EnumerateArray().Select(item => item.GetString()!)]);
        }

        static AiJsonSchema ParseObject(JsonElement element, string path)
        {
            if (!element.TryGetProperty("properties", out var propertiesElement) ||
                propertiesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException($"AI object schema '{path}' must contain array property 'properties'.",
                    nameof(element));
            }

            var properties = propertiesElement.EnumerateArray().Select((property, index) =>
            {
                var propertyPath = string.Create(CultureInfo.InvariantCulture, $"{path}.properties[{index}]");
                if (property.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException($"AI schema property '{propertyPath}' must be an object.",
                        nameof(property));
                }

                if (!property.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException(
                        $"AI schema property '{propertyPath}' must contain string property 'name'.",
                        nameof(property));
                }

                var name = nameElement.GetString()!;
                if (!property.TryGetProperty("schema", out var schemaElement))
                {
                    throw new ArgumentException(
                        $"AI schema property '{propertyPath}' must contain property 'schema'.",
                        nameof(property));
                }

                var schema = Parse(schemaElement, $"{propertyPath}.schema");
                var optional = property.TryGetProperty("optional", out var optionalElement) &&
                               optionalElement.ValueKind == JsonValueKind.True;
                string? description = null;
                if (property.TryGetProperty("description", out var descriptionElement) &&
                    descriptionElement.ValueKind != JsonValueKind.Null)
                {
                    description = descriptionElement.GetString();
                }

                if (!property.TryGetProperty("order", out var orderElement) ||
                    orderElement.ValueKind != JsonValueKind.Number ||
                    !orderElement.TryGetInt32(out var order))
                {
                    throw new ArgumentException(
                        $"AI schema property '{propertyPath}' must contain integer property 'order'.",
                        nameof(property));
                }

                return new AiSchemaProperty(name, schema, optional, description, order);
            }).ToArray();

            return Object(properties);
        }
    }

    /// <summary>创建对象架构。</summary>
    /// <param name="properties">对象属性。</param>
    /// <returns>对象架构。</returns>
    public static AiObjectSchema Object(params AiSchemaProperty[] properties)
    {
        return new AiObjectSchema(properties);
    }

    /// <summary>创建数组架构。</summary>
    /// <param name="itemSchema">每个数组项的架构。</param>
    /// <returns>数组架构。</returns>
    public static AiArraySchema Array(AiJsonSchema itemSchema)
    {
        return new AiArraySchema(itemSchema);
    }

    /// <summary>创建字符串原始架构。</summary>
    /// <returns>字符串架构。</returns>
    public static AiPrimitiveSchema String()
    {
        return new AiPrimitiveSchema("string");
    }

    /// <summary>创建数字原始架构。</summary>
    /// <returns>数字架构。</returns>
    public static AiPrimitiveSchema Number()
    {
        return new AiPrimitiveSchema("number");
    }

    /// <summary>创建布尔原始架构。</summary>
    /// <returns>布尔架构。</returns>
    public static AiPrimitiveSchema Boolean()
    {
        return new AiPrimitiveSchema("boolean");
    }

    /// <summary>创建枚举架构。</summary>
    /// <param name="values">允许的字符串值。</param>
    /// <returns>枚举架构。</returns>
    public static AiEnumSchema Enum(params string[] values)
    {
        return new AiEnumSchema(values);
    }

    /// <summary>创建必需对象属性架构。</summary>
    /// <param name="name">属性名。</param>
    /// <param name="schema">属性架构。</param>
    /// <param name="order">属性顺序。</param>
    /// <param name="description">属性用途说明。</param>
    /// <returns>必需架构属性。</returns>
    public static AiSchemaProperty Required(
        string name,
        AiJsonSchema schema,
        int order = 0,
        string? description = null)
    {
        return new AiSchemaProperty(name, schema, optional: false, description, order);
    }

    /// <summary>创建可选对象属性架构。</summary>
    /// <param name="name">属性名。</param>
    /// <param name="schema">属性架构。</param>
    /// <param name="order">属性顺序。</param>
    /// <param name="description">属性用途说明。</param>
    /// <returns>可选架构属性。</returns>
    public static AiSchemaProperty Optional(
        string name,
        AiJsonSchema schema,
        int order = 0,
        string? description = null)
    {
        return new AiSchemaProperty(name, schema, optional: true, description, order);
    }

    /// <summary>将此架构包装为可为 null。</summary>
    /// <returns>可为 null 的架构包装器。</returns>
    public AiNullableSchema Nullable()
    {
        return new AiNullableSchema(this);
    }

    /// <summary>将此架构包装为可选。</summary>
    /// <returns>可选架构包装器。</returns>
    public AiOptionalSchema Optional()
    {
        return new AiOptionalSchema(this);
    }

    /// <summary>将此架构序列化为 JSON 元素。</summary>
    /// <returns>架构的 JSON AST 表示。</returns>
    public JsonElement ToJson()
    {
        return JsonSerializer.SerializeToElement(ToJsonValue());
    }

    /// <summary>将此架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的架构值。</returns>
    internal abstract object? ToJsonValue();

    /// <summary>创建带 kind 字段的 AST 对象。</summary>
    /// <param name="kind">架构节点种类。</param>
    /// <param name="fields">额外字段。</param>
    /// <returns>表示 AST 节点的字典。</returns>
    internal static Dictionary<string, object?> CreateNode(string kind, params KeyValuePair<string, object?>[] fields)
    {
        var node = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind
        };

        foreach (var field in fields) node[field.Key] = field.Value;

        return node;
    }
}

/// <summary>AI 对象架构的属性定义。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiSchemaProperty
{
    /// <summary>创建 AI 对象架构属性。</summary>
    /// <param name="name">属性名。</param>
    /// <param name="schema">属性架构。</param>
    /// <param name="optional">该属性是否可选。</param>
    /// <param name="description">属性用途说明。</param>
    /// <param name="order">属性顺序。</param>
    public AiSchemaProperty(
        string name,
        AiJsonSchema schema,
        bool optional = false,
        string? description = null,
        int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.EndsWith('?'))
            throw new ArgumentException("AI schema property names cannot end with '?'.", nameof(name));

        var innerSchema = schema ?? throw new ArgumentNullException(nameof(schema));
        if (innerSchema is AiOptionalSchema optionalSchema)
        {
            innerSchema = optionalSchema.InnerSchema;
            optional = true;
        }

        Name = name;
        Schema = innerSchema;
        Optional = optional;
        Description = description;
        Order = order;
    }

    /// <summary>不包含可选标记后缀的属性名。</summary>
    public string Name { get; }

    /// <summary>属性架构。</summary>
    public AiJsonSchema Schema { get; }

    /// <summary>该属性是否可选。</summary>
    public bool Optional { get; }

    /// <summary>属性用途说明。</summary>
    public string? Description { get; }

    /// <summary>跨属性合并和渲染顺序。</summary>
    public int Order { get; }
}

/// <summary>表示 JSON 对象的 AI 架构。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiObjectSchema : AiJsonSchema
{
    /// <summary>根据属性定义创建对象架构。</summary>
    /// <param name="properties">对象属性。</param>
    public AiObjectSchema(params AiSchemaProperty[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<AiSchemaProperty>(properties.Length);
        foreach (var property in properties)
        {
            if (property is null)
                throw new ArgumentNullException(nameof(properties));
            if (!seenNames.Add(property.Name))
            {
                throw new ArgumentException($"AI schema object already contains property '{property.Name}'.",
                    nameof(properties));
            }

            normalized.Add(property);
        }

        Properties = normalized;
    }

    /// <summary>对象属性定义。</summary>
    public IReadOnlyList<AiSchemaProperty> Properties { get; }

    /// <summary>将此对象架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的对象架构值。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode("object", new KeyValuePair<string, object?>("properties",
            Properties.Select(property => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = property.Name,
                ["schema"] = property.Schema.ToJsonValue(),
                ["optional"] = property.Optional,
                ["description"] = property.Description,
                ["order"] = property.Order
            }).ToArray()));
    }
}

/// <summary>表示 JSON 数组的 AI 架构。</summary>
/// <param name="ItemSchema">每个数组项的架构。</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiArraySchema(AiJsonSchema ItemSchema) : AiJsonSchema
{
    /// <summary>每个数组项的架构。</summary>
    public AiJsonSchema ItemSchema { get; } = ItemSchema ?? throw new ArgumentNullException(nameof(ItemSchema));

    /// <summary>将此数组架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的数组架构值。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode("array", new KeyValuePair<string, object?>("item", ItemSchema.ToJsonValue()));
    }
}

/// <summary>表示 JSON 原始类型的 AI 架构。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiPrimitiveSchema : AiJsonSchema
{
    /// <summary>创建原始架构。</summary>
    /// <param name="type">原始架构类型名。</param>
    internal AiPrimitiveSchema(string type)
    {
        Type = type;
    }

    /// <summary>原始架构类型名。</summary>
    public string Type { get; }

    /// <summary>将此原始架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>原始架构 AST。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode(Type);
    }
}

/// <summary>表示字符串枚举的 AI 架构。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiEnumSchema : AiJsonSchema
{
    /// <summary>根据允许值创建枚举架构。</summary>
    /// <param name="values">允许的字符串值。</param>
    public AiEnumSchema(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("AI enum schema must contain at least one value.", nameof(values));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(values));
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("AI enum schema value is null or whitespace.", nameof(values));
            if (!seen.Add(value))
                throw new ArgumentException($"AI enum schema contains duplicate value '{value}'.", nameof(values));
        }

        Values = [.. values];
    }

    /// <summary>允许的字符串值。</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>将此枚举架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的枚举架构值。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode("enum", new KeyValuePair<string, object?>("values", Values));
    }
}

/// <summary>允许值为 null 的 AI 架构包装器。</summary>
/// <param name="InnerSchema">被包装的架构。</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiNullableSchema(AiJsonSchema InnerSchema) : AiJsonSchema
{
    /// <summary>被包装的架构。</summary>
    public AiJsonSchema InnerSchema { get; } = InnerSchema ?? throw new ArgumentNullException(nameof(InnerSchema));

    /// <summary>将此可为 null 架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的可为 null 架构值。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode("nullable", new KeyValuePair<string, object?>("value", InnerSchema.ToJsonValue()));
    }
}

/// <summary>将值标记为可选的 AI 架构包装器。</summary>
/// <param name="InnerSchema">被包装的架构。</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AiOptionalSchema(AiJsonSchema InnerSchema) : AiJsonSchema
{
    /// <summary>被包装的架构。</summary>
    public AiJsonSchema InnerSchema { get; } = InnerSchema ?? throw new ArgumentNullException(nameof(InnerSchema));

    /// <summary>将此可选架构转换为适合序列化器的 AST 值。</summary>
    /// <returns>适合序列化器的可选架构值。</returns>
    internal override object ToJsonValue()
    {
        return CreateNode("optional", new KeyValuePair<string, object?>("value", InnerSchema.ToJsonValue()));
    }
}
