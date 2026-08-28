using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper;

/// <summary>把结构化变量更新命令逐条应用到 <see cref="GameState" /> 托管根的唯一验证/应用权威。</summary>
public static class VariableUpdatePatchApplier
{
    /// <summary>
    /// 逐条应用变量更新命令：单条可恢复失败只回滚该命令并记录警告，后续命令继续应用；
    /// 全部命令处理完毕后把结果根写回 <paramref name="state" />。
    /// </summary>
    public static VariableUpdateApplyResult Apply(GameState state, SessionStateContract contract,
        IReadOnlyList<VariableUpdateOperation> operations, ILogger logger,
        Func<VariableUpdateOperation, bool>? validateOperation = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(logger);

        var root = ToNode(state.Snapshot) as JsonObject
                   ?? throw new VariableUpdateValidationException("VariableUpdate state root must be a JSON object.");
        var warnings = new List<string>();
        var appliedOperations = new List<VariableUpdateOperation>();
        var failedOperationCount = 0;
        foreach (var operation in operations)
        {
            var previousRoot = root.DeepClone() as JsonObject
                               ?? throw new InvalidOperationException("VariableUpdate state root could not be cloned.");
            var operationName = OperationName(operation);
            var operationPath = operation.Path;
            try
            {
                var safeOperation = VariableUpdateOperationCopy.Clone(operation);
                if (validateOperation?.Invoke(VariableUpdateOperationCopy.Clone(safeOperation)) is false)
                {
                    throw new VariableUpdateValidationException(
                        $"VariableUpdate operation at '{safeOperation.Path}' was rejected by the Game validator.");
                }

                appliedOperations.Add(ApplyOperation(root, contract, safeOperation, logger, warnings));
            }
            catch (Exception exception) when (IsRecoverableCommandFailure(exception))
            {
                failedOperationCount++;
                root = previousRoot;
                warnings.Add($"VariableUpdate skipped command '{operationName}' at '{operationPath}': {exception.Message}");
                logger.VariableUpdateCommandSkipped(exception, operationName, operationPath);
            }
        }

        state.Replace(new JsonPointer(string.Empty), ToElement(root));
        return new VariableUpdateApplyResult([.. appliedOperations], [.. warnings], failedOperationCount);
    }

    private static bool IsRecoverableCommandFailure(Exception exception)
    {
        return exception is VariableUpdateValidationException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or JsonException or OverflowException or FormatException;
    }

    private static string OperationName(VariableUpdateOperation operation)
    {
        return operation switch
        {
            VariableUpdateReplace => "replace",
            VariableUpdateInsert => "insert",
            VariableUpdateRemove => "remove",
            VariableUpdateDelta => "delta",
            _ => "<invalid>",
        };
    }

    private static VariableUpdateOperation ApplyOperation(JsonObject root, SessionStateContract contract,
        VariableUpdateOperation operation, ILogger logger, List<string> warnings)
    {
        if (string.IsNullOrEmpty(operation.Path))
            throw new VariableUpdateValidationException("VariableUpdate path must not be null or empty.");
        if (operation.Path.Contains("/_", StringComparison.Ordinal) || operation.Path.EndsWith("/_", StringComparison.Ordinal))
            throw new VariableUpdateValidationException("VariableUpdate cannot modify readonly '_' paths.");

        return operation switch
        {
            VariableUpdateReplace replace => ApplyReplace(root, contract, replace, logger, warnings),
            VariableUpdateInsert insert => ApplyInsert(root, contract, insert, logger, warnings),
            VariableUpdateRemove remove => ApplyRemove(root, contract, remove),
            VariableUpdateDelta delta => ApplyDelta(root, contract, delta, logger, warnings),
            _ => throw new VariableUpdateValidationException(
                $"VariableUpdate operation '{operation.GetType().FullName}' is not supported."),
        };
    }

    private static VariableUpdateReplace ApplyReplace(JsonObject root, SessionStateContract contract,
        VariableUpdateReplace operation, ILogger logger, List<string> warnings)
    {
        var target = ResolveTarget(contract, root, operation.Path, PathAccessMode.Existing);
        var normalized = NormalizeValue(contract, target.Node, operation.Value, operation.Path, target.Nullable,
            target.Member, logger, warnings);
        ReplaceAt(root, operation.Path, normalized);
        return new VariableUpdateReplace(operation.Path, normalized?.DeepClone());
    }

    private static VariableUpdateInsert ApplyInsert(JsonObject root, SessionStateContract contract,
        VariableUpdateInsert operation, ILogger logger, List<string> warnings)
    {
        var target = ResolveTarget(contract, root, operation.Path, PathAccessMode.Insert);
        var normalized = NormalizeValue(contract, target.Node, operation.Value, operation.Path, target.Nullable,
            target.Member, logger, warnings);
        AddAt(root, operation.Path, normalized);
        return new VariableUpdateInsert(operation.Path, normalized?.DeepClone());
    }

    private static VariableUpdateRemove ApplyRemove(JsonObject root, SessionStateContract contract,
        VariableUpdateRemove operation)
    {
        _ = ResolveTarget(contract, root, operation.Path, PathAccessMode.Remove);
        RemoveAt(root, operation.Path);
        return operation;
    }

    private static VariableUpdateDelta ApplyDelta(JsonObject root, SessionStateContract contract,
        VariableUpdateDelta operation, ILogger logger, List<string> warnings)
    {
        var target = ResolveTarget(contract, root, operation.Path, PathAccessMode.Existing);
        if (target.Node is not SessionStateNumberNode { ClrType: var clrType } number)
        {
            throw new VariableUpdateValidationException(
                "VariableUpdate delta target must be a numeric SessionState member.");
        }

        var current = GetAt(root, operation.Path);
        return number.Integer
            ? ApplyIntegerDelta(root, operation, current, target, clrType, logger, warnings)
            : ApplyFractionalDelta(root, operation, current, target, clrType, logger, warnings);
    }

    private static VariableUpdateDelta ApplyIntegerDelta(JsonObject root, VariableUpdateDelta operation,
        JsonNode? current, PathTarget target, Type clrType, ILogger logger, List<string> warnings)
    {
        if (decimal.Truncate(operation.Value) != operation.Value || operation.Value is < long.MinValue or > long.MaxValue)
        {
            throw new VariableUpdateValidationException(
                "VariableUpdate delta operation requires an integer value for integer targets.");
        }

        var currentValue = RequiredInt64(current, "VariableUpdate delta target must be an integer number.");
        var next = checked(currentValue + decimal.ToInt64(operation.Value));
        EnsureIntegerRange(clrType, next, operation.Path);
        var clamped = ClampIfNeeded(target.Member, operation.Path, SerializeInteger(clrType, next), logger, warnings);
        ReplaceAt(root, operation.Path, clamped);
        var applied = RequiredInt64(clamped, "VariableUpdate clamped integer value is invalid.") - currentValue;
        return new VariableUpdateDelta(operation.Path, applied);
    }

    private static VariableUpdateDelta ApplyFractionalDelta(JsonObject root, VariableUpdateDelta operation,
        JsonNode? current, PathTarget target, Type clrType, ILogger logger, List<string> warnings)
    {
        if (clrType == typeof(decimal))
        {
            var currentValue = RequiredDecimal(current,
                "VariableUpdate delta operation requires decimal-compatible numeric values.");
            var clamped = ClampIfNeeded(target.Member, operation.Path,
                JsonValue.Create(currentValue + operation.Value), logger, warnings);
            ReplaceAt(root, operation.Path, clamped);
            return new VariableUpdateDelta(operation.Path,
                RequiredDecimal(clamped, "VariableUpdate clamped decimal value is invalid.") - currentValue);
        }

        if (clrType != typeof(double))
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate delta target has unsupported numeric type '{clrType.FullName}'.");
        }

        var doubleCurrentValue = RequiredDouble(current,
            "VariableUpdate delta operation requires finite double-compatible numeric values.");
        var delta = decimal.ToDouble(operation.Value);
        if (!double.IsFinite(doubleCurrentValue) || !double.IsFinite(delta) || !double.IsFinite(doubleCurrentValue + delta))
        {
            throw new VariableUpdateValidationException(
                "VariableUpdate delta operation requires finite double-compatible numeric values.");
        }

        var doubleClamped = ClampIfNeeded(target.Member, operation.Path,
            JsonValue.Create(doubleCurrentValue + delta), logger, warnings);
        ReplaceAt(root, operation.Path, doubleClamped);
        return new VariableUpdateDelta(operation.Path,
            (decimal)(RequiredDouble(doubleClamped, "VariableUpdate clamped double value is invalid.") - doubleCurrentValue));
    }

    private static JsonNode? NormalizeValue(SessionStateContract contract, SessionStateSchemaNode node, JsonNode? value,
        string path, bool allowNull, SessionStateMemberNode? member, ILogger logger, List<string> warnings)
    {
        if (value is null)
        {
            return allowNull
                ? null
                : throw new VariableUpdateValidationException($"VariableUpdate path '{path}' does not allow null.");
        }

        return node switch
        {
            SessionStateTypeNode objectNode => NormalizeObjectValue(contract, objectNode, value, path, logger, warnings),
            SessionStateListNode listNode => NormalizeListValue(contract, listNode, value, path, logger, warnings),
            SessionStateDictionaryNode dictionaryNode => NormalizeDictionaryValue(contract, dictionaryNode, value, path, logger,
                warnings),
            SessionStatePrimitiveNode { Kind: "string" } => RequireString(value, path),
            SessionStatePrimitiveNode { Kind: "boolean" } => RequireBoolean(value, path),
            SessionStateNumberNode number => NormalizeNumberValue(number, member, value, path, logger, warnings),
            SessionStateEnumNode enumNode => NormalizeEnumValue(enumNode, value, path),
            _ => throw new VariableUpdateValidationException($"VariableUpdate path '{path}' has unsupported schema node."),
        };
    }

    private static JsonObject NormalizeObjectValue(SessionStateContract contract, SessionStateTypeNode objectNode,
        JsonNode value, string path, ILogger logger, List<string> warnings)
    {
        var input = value as JsonObject ?? ThrowType<JsonObject>(path, "object");
        var allowed = objectNode.Members.Select(member => member.JsonName).ToHashSet(StringComparer.Ordinal);
        var schemaOutsideProperty = input.Select(property => property.Key)
            .FirstOrDefault(propertyName => !allowed.Contains(propertyName));
        if (schemaOutsideProperty is not null)
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate object at '{path}' contains schema-outside property '{schemaOutsideProperty}'.");
        }

        var normalized = new JsonObject();
        foreach (var member in objectNode.Members)
        {
            if (!input.TryGetPropertyValue(member.JsonName, out var child))
            {
                throw new VariableUpdateValidationException(
                    $"VariableUpdate object at '{path}' is missing required property '{member.JsonName}'.");
            }

            normalized[member.JsonName] = NormalizeValue(contract,
                contract.GetSchemaNode(member.Property.PropertyType, aiFacingOnly: true), child,
                Join(path, Escape(member.JsonName)), IsNullable(member.Property.PropertyType), member, logger, warnings);
        }

        return normalized;
    }

    private static JsonArray NormalizeListValue(SessionStateContract contract, SessionStateListNode listNode, JsonNode value,
        string path, ILogger logger, List<string> warnings)
    {
        var input = value as JsonArray ?? ThrowType<JsonArray>(path, "array");
        var normalized = new JsonArray();
        foreach (var (item, index) in input.Select((item, index) => (item, index)))
        {
            normalized.Add(NormalizeValue(contract, listNode.Item, item,
                Join(path, index.ToString(CultureInfo.InvariantCulture)), allowNull: false, member: null, logger, warnings));
        }

        return normalized;
    }

    private static JsonObject NormalizeDictionaryValue(SessionStateContract contract, SessionStateDictionaryNode dictionaryNode,
        JsonNode value, string path, ILogger logger, List<string> warnings)
    {
        var input = value as JsonObject ?? ThrowType<JsonObject>(path, "object");
        var normalized = new JsonObject();
        foreach (var property in input.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            ValidateDictionaryKey(property.Key);
            normalized[property.Key] = NormalizeValue(contract, dictionaryNode.Value, property.Value,
                Join(path, Escape(property.Key)), allowNull: false, member: null, logger, warnings);
        }

        return normalized;
    }

    private static JsonNode NormalizeNumberValue(SessionStateNumberNode number, SessionStateMemberNode? member,
        JsonNode value, string path, ILogger logger, List<string> warnings)
    {
        ValidateNumberValue(number, value, path);
        return ClampIfNeeded(member, path, value, logger, warnings);
    }

    private static JsonNode NormalizeEnumValue(SessionStateEnumNode enumNode, JsonNode value, string path)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var name) ||
            !Enum.IsDefined(enumNode.EnumType, name))
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate path '{path}' has invalid enum value '{value}'.");
        }

        return value.DeepClone();
    }

    private static JsonNode RequireString(JsonNode value, string path)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _)) return value.DeepClone();
        return ThrowType<JsonNode>(path, "string");
    }

    private static JsonNode RequireBoolean(JsonNode value, string path)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out _)) return value.DeepClone();
        return ThrowType<JsonNode>(path, "boolean");
    }

    private static JsonNode ClampIfNeeded(SessionStateMemberNode? member, string path, JsonNode value,
        ILogger logger, List<string> warnings)
    {
        if (member is null || (member.Min is null && member.Max is null) || !TryGetDecimal(value, out var original))
            return value.DeepClone();

        var clamped = original;
        if (member.Min is { } min) clamped = decimal.Max(clamped, min);
        if (member.Max is { } max) clamped = decimal.Min(clamped, max);
        if (clamped == original) return value.DeepClone();

        var warning = string.Create(CultureInfo.InvariantCulture,
            $"VariableUpdate clamped '{path}' from {original} to {clamped}.");
        warnings.Add(warning);
        logger.VariableUpdateValueClamped(path, original, clamped);
        return SerializeClampedNumber(member.Property.PropertyType, clamped, path);
    }

    private static PathTarget ResolveTarget(SessionStateContract contract, JsonObject root, string path,
        PathAccessMode accessMode)
    {
        var segments = ParsePointer(path);
        if (segments.Count == 0)
        {
            return accessMode == PathAccessMode.Existing
                ? new PathTarget(contract.GetSchemaNode(contract.RootType, aiFacingOnly: true), Member: null, Nullable: false)
                : throw new VariableUpdateValidationException("VariableUpdate cannot insert or remove the root object.");
        }

        return ResolveTargetCore(contract, contract.GetSchemaNode(contract.RootType, aiFacingOnly: true), root, segments, 0,
            accessMode);
    }

    private static PathTarget ResolveTargetCore(SessionStateContract contract, SessionStateSchemaNode node,
        JsonNode? current, IReadOnlyList<string> segments, int index, PathAccessMode accessMode)
    {
        var segment = segments[index];
        var isLast = index == segments.Count - 1;
        return node switch
        {
            SessionStateTypeNode objectNode => ResolveObjectTarget(contract, objectNode, current, segments, index,
                segment, isLast, accessMode),
            SessionStateListNode listNode => ResolveListTarget(contract, listNode, current, segments, index, segment,
                isLast, accessMode),
            SessionStateDictionaryNode dictionaryNode => ResolveDictionaryTarget(contract, dictionaryNode, current,
                segments, index, segment, isLast, accessMode),
            _ => throw new VariableUpdateValidationException("VariableUpdate path tries to traverse through a scalar value."),
        };
    }

    private static PathTarget ResolveObjectTarget(SessionStateContract contract, SessionStateTypeNode objectNode,
        JsonNode? current, IReadOnlyList<string> segments, int index, string segment, bool isLast,
        PathAccessMode accessMode)
    {
        var member = objectNode.Members.FirstOrDefault(candidate =>
            string.Equals(candidate.JsonName, segment, StringComparison.Ordinal));
        if (member is null)
        {
            if (accessMode == PathAccessMode.Insert && isLast)
            {
                throw new VariableUpdateValidationException(
                    $"VariableUpdate cannot insert schema-outside fixed object property '{segment}'.");
            }

            throw new VariableUpdateValidationException(
                $"VariableUpdate path segment '{segment}' is not declared by fixed object schema.");
        }

        switch (accessMode)
        {
            case PathAccessMode.Remove when isLast:
                throw new VariableUpdateValidationException(
                    $"VariableUpdate cannot remove fixed object property '{segment}'; replace nullable properties with null instead.");
            case PathAccessMode.Insert when isLast:
                throw new VariableUpdateValidationException(
                    $"VariableUpdate cannot insert fixed object property '{segment}'. Use replace for existing properties.");
            case PathAccessMode.Existing:
            case PathAccessMode.Insert:
            case PathAccessMode.Remove:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(accessMode), accessMode, message: null);
        }

        if (current is not JsonObject objectCurrent || !objectCurrent.TryGetPropertyValue(segment, out var child))
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate path '/{string.Join('/', segments.Take(index + 1))}' does not exist.");
        }

        var childNode = contract.GetSchemaNode(member.Property.PropertyType, aiFacingOnly: true);
        return isLast
            ? new PathTarget(childNode, member, IsNullable(member.Property.PropertyType))
            : ResolveTargetCore(contract, childNode, child, segments, index + 1, accessMode);
    }

    private static PathTarget ResolveListTarget(SessionStateContract contract, SessionStateListNode listNode,
        JsonNode? current, IReadOnlyList<string> segments, int index, string segment, bool isLast,
        PathAccessMode accessMode)
    {
        if (current is not JsonArray arrayCurrent)
            throw new VariableUpdateValidationException("VariableUpdate list path does not point to a JSON array.");
        if (!isLast)
        {
            return ResolveTargetCore(contract, listNode.Item,
                arrayCurrent[ParseListExistingIndex(segment, arrayCurrent.Count)], segments, index + 1, accessMode);
        }

        if (accessMode == PathAccessMode.Insert)
            ValidateListInsertSegment(segment, arrayCurrent.Count);
        else
            ValidateListExistingSegment(segment, arrayCurrent.Count);
        return new PathTarget(listNode.Item, Member: null, Nullable: false);
    }

    private static PathTarget ResolveDictionaryTarget(SessionStateContract contract, SessionStateDictionaryNode dictionaryNode,
        JsonNode? current, IReadOnlyList<string> segments, int index, string segment, bool isLast,
        PathAccessMode accessMode)
    {
        if (current is not JsonObject dictionaryCurrent)
            throw new VariableUpdateValidationException("VariableUpdate dictionary path does not point to a JSON object.");
        ValidateDictionaryKey(segment);
        var exists = dictionaryCurrent.TryGetPropertyValue(segment, out var dictionaryChild);
        if (isLast)
        {
            if (accessMode == PathAccessMode.Insert)
            {
                if (exists)
                {
                    throw new VariableUpdateValidationException(
                        $"VariableUpdate cannot insert existing TrackedDictionary key '{segment}'.");
                }

                return new PathTarget(dictionaryNode.Value, Member: null, Nullable: false);
            }

            if (!exists)
            {
                throw new VariableUpdateValidationException(
                    $"VariableUpdate TrackedDictionary key '{segment}' does not exist.");
            }

            return new PathTarget(dictionaryNode.Value, Member: null, Nullable: false);
        }

        if (!exists)
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate TrackedDictionary key '{segment}' does not exist.");
        }

        return ResolveTargetCore(contract, dictionaryNode.Value, dictionaryChild, segments, index + 1, accessMode);
    }

    private static void ValidateNumberValue(SessionStateNumberNode number, JsonNode value, string path)
    {
        if (number.Integer)
        {
            var integer = RequiredInt64(value, $"VariableUpdate path '{path}' requires an integer number.");
            EnsureIntegerRange(number.ClrType, integer, path);
            return;
        }

        if (number.ClrType == typeof(double) && !TryGetDouble(value, out _)) ThrowType(path, "number");
        if (number.ClrType == typeof(decimal) && !TryGetDecimal(value, out _)) ThrowType(path, "decimal number");
    }

    private static void ValidateListInsertSegment(string segment, int count)
    {
        if (string.Equals(segment, "-", StringComparison.Ordinal)) return;
        if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index > count)
        {
            throw new VariableUpdateValidationException(string.Create(CultureInfo.InvariantCulture,
                $"VariableUpdate list insert index '{segment}' is outside the valid range 0..{count}."));
        }
    }

    private static void ValidateListExistingSegment(string segment, int count)
    {
        _ = ParseListExistingIndex(segment, count);
    }

    private static int ParseListExistingIndex(string segment, int count)
    {
        if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= count)
        {
            throw new VariableUpdateValidationException($"VariableUpdate list index '{segment}' does not exist.");
        }

        return index;
    }

    private static void ValidateDictionaryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new VariableUpdateValidationException("VariableUpdate TrackedDictionary key cannot be empty.");
        if (key.StartsWith('_'))
        {
            throw new VariableUpdateValidationException(
                $"VariableUpdate cannot modify readonly TrackedDictionary key '{key}'.");
        }
    }

    private static void EnsureIntegerRange(Type clrType, long value, string path)
    {
        if (clrType == typeof(int) && value is < int.MinValue or > int.MaxValue)
        {
            throw new VariableUpdateValidationException($"VariableUpdate integer value at '{path}' exceeds Int32 range.");
        }
    }

    private static JsonValue SerializeInteger(Type clrType, long value)
    {
        return clrType == typeof(int) ? JsonValue.Create((int)value) : JsonValue.Create(value);
    }

    private static JsonValue SerializeClampedNumber(Type clrType, decimal value, string path)
    {
        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (clrType == typeof(int)) return JsonValue.Create(decimal.ToInt32(value));
        if (clrType == typeof(long)) return JsonValue.Create(decimal.ToInt64(value));
        if (clrType == typeof(double)) return JsonValue.Create((double)value);
        if (clrType == typeof(decimal)) return JsonValue.Create(value);
        throw new VariableUpdateValidationException(
            $"VariableUpdate path '{path}' has unsupported numeric type '{clrType.FullName}'.");
    }

    private static void ReplaceAt(JsonObject root, string path, JsonNode? value)
    {
        var (parent, segment) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject objectParent when objectParent.ContainsKey(segment):
                objectParent[segment] = value?.DeepClone();
                return;
            case JsonArray arrayParent:
                arrayParent[ParseListExistingIndex(segment, arrayParent.Count)] = value?.DeepClone();
                return;
            default:
                throw new KeyNotFoundException($"State path '{path}' does not exist.");
        }
    }

    private static void AddAt(JsonObject root, string path, JsonNode? value)
    {
        var (parent, segment) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject objectParent:
                if (objectParent.ContainsKey(segment))
                    throw new InvalidOperationException($"State path '{path}' already exists.");
                objectParent[segment] = value?.DeepClone();
                return;
            case JsonArray arrayParent:
                var index = string.Equals(segment, "-", StringComparison.Ordinal)
                    ? arrayParent.Count
                    : int.Parse(segment, CultureInfo.InvariantCulture);
                arrayParent.Insert(index, value?.DeepClone());
                return;
            default:
                throw new InvalidOperationException($"State path '{path}' has no collection parent.");
        }
    }

    private static void RemoveAt(JsonObject root, string path)
    {
        var (parent, segment) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject objectParent when objectParent.Remove(segment):
                return;
            case JsonArray arrayParent:
                arrayParent.RemoveAt(ParseListExistingIndex(segment, arrayParent.Count));
                return;
            default:
                throw new KeyNotFoundException($"State path '{path}' does not exist.");
        }
    }

    private static JsonNode? GetAt(JsonObject root, string path)
    {
        return ParsePointer(path).Aggregate<string, JsonNode?>(root, (current, segment) =>
            current switch
            {
                JsonObject objectCurrent when objectCurrent.TryGetPropertyValue(segment, out var child) => child,
                JsonArray arrayCurrent => arrayCurrent[ParseListExistingIndex(segment, arrayCurrent.Count)],
                _ => throw new KeyNotFoundException($"State path '{path}' does not exist."),
            });
    }

    private static (JsonNode Parent, string Segment) ResolveParent(JsonObject root, string path)
    {
        var segments = ParsePointer(path);
        if (segments.Count == 0) throw new InvalidOperationException("VariableUpdate cannot modify the root object directly.");
        var parent = segments.Take(segments.Count - 1).Aggregate<string, JsonNode>(root, (current, segment) =>
            current switch
            {
                JsonObject objectCurrent when objectCurrent.TryGetPropertyValue(segment, out var child) && child is not null => child,
                JsonArray arrayCurrent => arrayCurrent[ParseListExistingIndex(segment, arrayCurrent.Count)]
                    ?? throw new KeyNotFoundException($"Parent path for state path '{path}' does not exist."),
                _ => throw new KeyNotFoundException($"Parent path for state path '{path}' does not exist."),
            });

        return (parent, segments[^1]);
    }

    private static List<string> ParsePointer(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            throw new VariableUpdateValidationException("VariableUpdate path must be a JSON Pointer.");
        return [.. path[1..].Split('/').Select(Unescape)];
    }

    private static string Unescape(string segment)
    {
        var result = new StringBuilder(segment.Length);
        var index = 0;
        while (index < segment.Length)
        {
            if (segment[index] != '~')
            {
                result.Append(segment[index]);
                index++;
                continue;
            }

            if (index + 1 >= segment.Length)
                throw new VariableUpdateValidationException("VariableUpdate path contains invalid JSON Pointer escape.");
            var escapedCharacter = segment[index + 1];
            result.Append(escapedCharacter switch
            {
                '0' => '~',
                '1' => '/',
                _ => throw new VariableUpdateValidationException(
                    $"VariableUpdate path contains invalid JSON Pointer escape '~{escapedCharacter}'."),
            });
            index += 2;
        }

        return result.ToString();
    }

    private static string Escape(string segment)
    {
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static string Join(string parent, string segment)
    {
        return string.IsNullOrEmpty(parent) ? $"/{segment}" : $"{parent}/{segment}";
    }

    private static bool IsNullable(Type type)
    {
        return Nullable.GetUnderlyingType(type) is not null || !type.IsValueType;
    }

    private static long RequiredInt64(JsonNode? value, string message)
    {
        if (TryGetDecimal(value, out var decimalValue) && decimal.Truncate(decimalValue) == decimalValue &&
            decimalValue is >= long.MinValue and <= long.MaxValue)
        {
            return decimal.ToInt64(decimalValue);
        }

        throw new VariableUpdateValidationException(message);
    }

    private static decimal RequiredDecimal(JsonNode? value, string message)
    {
        return TryGetDecimal(value, out var result) ? result : throw new VariableUpdateValidationException(message);
    }

    private static double RequiredDouble(JsonNode? value, string message)
    {
        return TryGetDouble(value, out var result) ? result : throw new VariableUpdateValidationException(message);
    }

    private static bool TryGetDecimal(JsonNode? value, out decimal result)
    {
        if (value is not JsonValue)
        {
            result = 0;
            return false;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        if (document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetDecimal(out result))
            return true;

        result = 0;
        return false;
    }

    private static bool TryGetDouble(JsonNode? value, out double result)
    {
        if (value is not JsonValue)
        {
            result = 0;
            return false;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        if (document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetDouble(out result))
            return true;

        result = 0;
        return false;
    }

    private static T ThrowType<T>(string path, string expected)
    {
        throw new VariableUpdateValidationException($"VariableUpdate path '{path}' requires {expected} value.");
    }

    private static void ThrowType(string path, string expected)
    {
        throw new VariableUpdateValidationException($"VariableUpdate path '{path}' requires {expected} value.");
    }

    private static JsonNode? ToNode(JsonElement value)
    {
        return JsonNode.Parse(value.GetRawText());
    }

    private static JsonElement ToElement(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private sealed record PathTarget(SessionStateSchemaNode Node, SessionStateMemberNode? Member, bool Nullable);

    private enum PathAccessMode
    {
        Existing,
        Insert,
        Remove,
    }
}

/// <summary>变量更新应用结果：仅含实际应用的命令、跳过/钳制警告与失败计数。</summary>
public sealed record VariableUpdateApplyResult
{
    /// <summary>创建应用结果；appliedPatch 与 warnings 被防御性复制。</summary>
    public VariableUpdateApplyResult(
        IReadOnlyList<VariableUpdateOperation> appliedPatch,
        IReadOnlyList<string> warnings,
        int failedOperationCount = 0)
    {
        ArgumentNullException.ThrowIfNull(appliedPatch);
        ArgumentNullException.ThrowIfNull(warnings);
        AppliedPatch = appliedPatch;
        Warnings = warnings;
        FailedOperationCount = failedOperationCount;
    }

    /// <summary>实际应用（含钳制后）的命令；getter 返回防御性副本。</summary>
    public IReadOnlyList<VariableUpdateOperation> AppliedPatch
    {
        get => [.. field.Select(VariableUpdateOperationCopy.Clone)];
        private init => field = Array.AsReadOnly([.. value.Select(VariableUpdateOperationCopy.Clone)]);
    }

    /// <summary>跳过命令与钳制调整的警告列表。</summary>
    public IReadOnlyList<string> Warnings
    {
        get => [.. field];
        private init => field = Array.AsReadOnly([.. value]);
    }

    /// <summary>被跳过的命令数。</summary>
    public int FailedOperationCount { get; }
}

internal static class VariableUpdateOperationCopy
{
    public static VariableUpdateOperation Clone(VariableUpdateOperation operation)
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

/// <summary>GameHelper 内部的变量更新日志事件 Id（语义与平台 Observability 2011/2012 延续）。</summary>
internal static class VariableUpdateLogEventIds
{
    internal const int VariableUpdateCommandSkippedId = 2011;
    internal const int VariableUpdateValueClampedId = 2012;
}

internal static partial class VariableUpdatePatchApplierLogs
{
    [LoggerMessage(EventId = VariableUpdateLogEventIds.VariableUpdateCommandSkippedId, Level = LogLevel.Warning,
        Message = "VariableUpdate command skipped: Operation={Operation}, Path={Path}")]
    public static partial void VariableUpdateCommandSkipped(
        this ILogger logger, Exception exception, string operation, string path);

    [LoggerMessage(EventId = VariableUpdateLogEventIds.VariableUpdateValueClampedId, Level = LogLevel.Warning,
        Message = "VariableUpdate clamped '{Path}' from {OriginalValue} to {ClampedValue}.")]
    public static partial void VariableUpdateValueClamped(
        this ILogger logger, string path, decimal originalValue, decimal clampedValue);
}
