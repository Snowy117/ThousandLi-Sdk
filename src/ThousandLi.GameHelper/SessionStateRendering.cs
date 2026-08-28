using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ThousandLi.GameHelper;

internal static class SessionStateZodSchemaRenderer
{
    private static readonly JsonSerializerOptions s_noEscapeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(SessionStateContract contract)
    {
        var builder = new StringBuilder();
        RenderNode(builder, contract, contract.GetSchemaNode(contract.RootType, aiFacingOnly: true), 0, member: null);
        return builder.ToString();
    }

    public static string RenderStateYaml(SessionStateContract contract, JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (state.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SessionState root must be a JSON object.", nameof(state));

        var builder = new StringBuilder();
        RenderYamlValue(builder, contract, contract.GetSchemaNode(contract.RootType, aiFacingOnly: true), state, 0);
        return builder.ToString().TrimEnd();
    }

    private static void RenderNode(StringBuilder builder, SessionStateContract contract, SessionStateSchemaNode node,
        int indent, SessionStateMemberNode? member)
    {
        switch (node)
        {
            case SessionStateTypeNode objectNode:
                RenderObject(builder, contract, objectNode, indent);
                break;
            case SessionStatePrimitiveNode primitive:
                builder.Append(string.Equals(primitive.Kind, "string", StringComparison.Ordinal) ? "z.string()" : "z.boolean()");
                AppendDescription(builder, member);
                break;
            case SessionStateNumberNode number:
                builder.Append(number.Integer ? "z.number().int()" : "z.number()");
                if (member?.Min is { } min) builder.Append(CultureInfo.InvariantCulture, $".min({min})");
                if (member?.Max is { } max) builder.Append(CultureInfo.InvariantCulture, $".max({max})");
                AppendDescription(builder, member);
                break;
            case SessionStateEnumNode enumNode:
                builder.Append("z.enum([");
                builder.AppendJoin(", ",
                    Enum.GetNames(enumNode.EnumType).Select(name => JsonSerializer.Serialize(name, s_noEscapeOptions)));
                builder.Append("])");
                AppendDescription(builder, member);
                break;
            case SessionStateListNode list:
                builder.Append("z.array(");
                RenderNode(builder, contract, list.Item, indent, member: null);
                builder.Append(')');
                AppendDescription(builder, member);
                break;
            case SessionStateDictionaryNode dictionary:
                builder.Append("z.record(z.string(), ");
                RenderNode(builder, contract, dictionary.Value, indent, member: null);
                builder.Append(')');
                AppendDescription(builder, member);
                break;
        }
    }

    private static void RenderObject(StringBuilder builder, SessionStateContract contract,
        SessionStateTypeNode objectNode,
        int indent)
    {
        builder.AppendLine("z.object({");
        foreach (var member in objectNode.Members)
        {
            if (!string.IsNullOrWhiteSpace(member.UpdateRule)) RenderRule(builder, member.UpdateRule, indent + 2);
            builder.Append(' ', indent + 2).Append(member.JsonName).Append(": ");
            RenderNode(builder, contract, contract.GetSchemaNode(member.Property.PropertyType, aiFacingOnly: true), indent + 2,
                member);
            if (IsNullable(member.Property.PropertyType)) builder.Append(".nullable()");
            builder.AppendLine(",");
        }

        builder.Append(' ', indent).Append('}').Append(')');
    }

    private static void RenderYamlValue(StringBuilder builder, SessionStateContract contract,
        SessionStateSchemaNode node,
        JsonElement value, int indent)
    {
        switch (node)
        {
            case SessionStateTypeNode objectNode:
                RenderYamlObject(builder, contract, objectNode, value, indent);
                break;
            case SessionStateListNode listNode:
                RenderYamlList(builder, contract, listNode, value, indent);
                break;
            case SessionStateDictionaryNode dictionaryNode:
                RenderYamlDictionary(builder, contract, dictionaryNode, value, indent);
                break;
            default:
                builder.Append(RenderYamlScalar(value));
                break;
        }
    }

    private static void RenderYamlObject(StringBuilder builder, SessionStateContract contract,
        SessionStateTypeNode objectNode, JsonElement value, int indent)
    {
        foreach (var member in objectNode.Members)
        {
            if (!value.TryGetProperty(member.JsonName, out var child)) continue;

            builder.Append(' ', indent).Append(member.JsonName).Append(':');
            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                builder.AppendLine();
                RenderYamlValue(builder, contract, contract.GetSchemaNode(member.Property.PropertyType, aiFacingOnly: true), child,
                    indent + 2);
            }
            else
            {
                builder.Append(' ').AppendLine(RenderYamlScalar(child));
            }
        }
    }

    private static void RenderYamlList(StringBuilder builder, SessionStateContract contract,
        SessionStateListNode listNode, JsonElement value, int indent)
    {
        foreach (var item in value.EnumerateArray())
        {
            builder.Append(' ', indent).Append('-');
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                builder.AppendLine();
                RenderYamlValue(builder, contract, listNode.Item, item, indent + 2);
            }
            else
            {
                builder.Append(' ').AppendLine(RenderYamlScalar(item));
            }
        }
    }

    private static void RenderYamlDictionary(StringBuilder builder, SessionStateContract contract,
        SessionStateDictionaryNode dictionaryNode, JsonElement value, int indent)
    {
        foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            builder.Append(' ', indent).Append(property.Name).Append(':');
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                builder.AppendLine();
                RenderYamlValue(builder, contract, dictionaryNode.Value, property.Value, indent + 2);
            }
            else
            {
                builder.Append(' ').AppendLine(RenderYamlScalar(property.Value));
            }
        }
    }

    private static string RenderYamlScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => JsonSerializer.Serialize(value.GetString(), s_noEscapeOptions),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };
    }

    private static void AppendDescription(StringBuilder builder, SessionStateMemberNode? member)
    {
        if (!string.IsNullOrWhiteSpace(member?.Description))
            builder.Append(".describe(").Append(JsonSerializer.Serialize(member.Description, s_noEscapeOptions)).Append(')');
    }

    private static void RenderRule(StringBuilder builder, string rule, int indent)
    {
        var lines = rule.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 1)
        {
            builder.Append(' ', indent).Append("/** ").Append(lines[0]).AppendLine(" */");
            return;
        }

        builder.Append(' ', indent).AppendLine("/**");
        foreach (var line in lines) builder.Append(' ', indent).Append(" * ").AppendLine(line);
        builder.Append(' ', indent).AppendLine(" */");
    }

    private static bool IsNullable(Type type)
    {
        return Nullable.GetUnderlyingType(type) is not null;
    }
}
