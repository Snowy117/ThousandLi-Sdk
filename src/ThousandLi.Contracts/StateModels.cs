using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

public readonly record struct JsonPointer(string Value)
{
    public override string ToString() => Value;
}

public enum StateChangeOperation
{
    Add,
    Replace,
    Remove
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record StateChange
{
    public StateChange(StateChangeOperation operation, JsonPointer path, JsonElement? oldValue, JsonElement? newValue)
    {
        Operation = operation;
        Path = path;
        OldValue = oldValue?.Clone();
        NewValue = newValue?.Clone();
    }

    public StateChangeOperation Operation { get; }
    public JsonPointer Path { get; }
    public JsonElement? OldValue { get; }
    public JsonElement? NewValue { get; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class GameState
{
    private readonly List<StateChange> _changes = [];
    private JsonObject _root;

    public GameState(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Game state snapshot root must be a JSON object.", nameof(snapshot));
        _root = ParseObject(snapshot);
    }

    public JsonElement Snapshot => ToElement(_root);
    public IReadOnlyList<StateChange> Changes => [.. _changes];

    public bool Contains(JsonPointer path) => TryGetNode(ParsePointer(path), out _);

    public JsonElement Get(JsonPointer path) => TryGetNode(ParsePointer(path), out var node)
        ? ToElement(node)
        : throw new KeyNotFoundException($"State path '{path}' does not exist.");

    public void Add(JsonPointer path, JsonElement value)
    {
        JsonContractGuard.ThrowIfUndefined(value, nameof(value));
        var segments = ParsePointer(path);
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot add the root state because it already exists.");
        var (parent, segment) = ResolveParent(segments, path);
        AddToParent(parent, segment, path, value);
        _changes.Add(new StateChange(StateChangeOperation.Add, path, oldValue: null, value));
    }

    public void Replace(JsonPointer path, JsonElement value)
    {
        JsonContractGuard.ThrowIfUndefined(value, nameof(value));
        var segments = ParsePointer(path);
        if (segments.Length == 0)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Game state root must remain a JSON object.");
            var oldRoot = Snapshot;
            _root = ParseObject(value);
            _changes.Add(new StateChange(StateChangeOperation.Replace, path, oldRoot, value));
            return;
        }

        var (parent, segment) = ResolveParent(segments, path);
        var oldValue = ReplaceInParent(parent, segment, path, value);
        _changes.Add(new StateChange(StateChangeOperation.Replace, path, oldValue, value));
    }

    public void Remove(JsonPointer path)
    {
        var segments = ParsePointer(path);
        if (segments.Length == 0) throw new InvalidOperationException("Cannot remove the root state.");
        var (parent, segment) = ResolveParent(segments, path);
        var oldValue = RemoveFromParent(parent, segment, path);
        _changes.Add(new StateChange(StateChangeOperation.Remove, path, oldValue, newValue: null));
    }

    private static void AddToParent(JsonNode? parent, string segment, JsonPointer path, JsonElement value)
    {
        switch (parent)
        {
            case JsonObject parentObject:
                if (parentObject.ContainsKey(segment))
                    throw new InvalidOperationException($"Cannot add state path '{path}' because it already exists.");
                parentObject[segment] = ParseNode(value);
                break;
            case JsonArray parentArray when TryGetArrayAddIndex(segment, parentArray.Count, out var index):
                parentArray.Insert(index, ParseNode(value));
                break;
            default:
                throw new KeyNotFoundException($"State path '{path}' does not identify a valid add position.");
        }
    }

    private static JsonElement ReplaceInParent(JsonNode? parent, string segment, JsonPointer path, JsonElement value)
    {
        switch (parent)
        {
            case JsonObject parentObject when parentObject.TryGetPropertyValue(segment, out var objectValue):
                {
                    var oldValue = ToElement(objectValue);
                    parentObject[segment] = ParseNode(value);
                    return oldValue;
                }
            case JsonArray parentArray when TryGetArrayIndex(segment, parentArray.Count, out var index):
                {
                    var oldValue = ToElement(parentArray[index]);
                    parentArray[index] = ParseNode(value);
                    return oldValue;
                }
            default:
                throw new KeyNotFoundException($"State path '{path}' does not exist.");
        }
    }

    private static JsonElement RemoveFromParent(JsonNode? parent, string segment, JsonPointer path)
    {
        switch (parent)
        {
            case JsonObject parentObject when parentObject.TryGetPropertyValue(segment, out var objectValue):
                {
                    var oldValue = ToElement(objectValue);
                    parentObject.Remove(segment);
                    return oldValue;
                }
            case JsonArray parentArray when TryGetArrayIndex(segment, parentArray.Count, out var index):
                {
                    var oldValue = ToElement(parentArray[index]);
                    parentArray.RemoveAt(index);
                    return oldValue;
                }
            default:
                throw new KeyNotFoundException($"State path '{path}' does not exist.");
        }
    }

    private (JsonNode? Parent, string Segment) ResolveParent(string[] segments, JsonPointer path)
    {
        JsonNode? parent = _root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!TryGetChild(parent, segments[index], out parent))
                throw new KeyNotFoundException($"Parent path for state path '{path}' does not exist.");
        }
        return (parent, segments[^1]);
    }

    private bool TryGetNode(IEnumerable<string> segments, out JsonNode? node)
    {
        node = _root;
        foreach (var segment in segments)
        {
            if (!TryGetChild(node, segment, out node)) return false;
        }
        return true;
    }

    private static bool TryGetChild(JsonNode? parent, string segment, out JsonNode? child)
    {
        switch (parent)
        {
            case JsonObject parentObject:
                return parentObject.TryGetPropertyValue(segment, out child);
            case JsonArray parentArray when TryGetArrayIndex(segment, parentArray.Count, out var index):
                child = parentArray[index];
                return true;
            default:
                child = null;
                return false;
        }
    }

    private static string[] ParsePointer(JsonPointer path)
    {
        if (path.Value is null) throw new ArgumentException("JSON Pointer value cannot be null.", nameof(path));
        return path.Value.Length switch
        {
            0 => [],
            _ when path.Value[0] == '/' => [.. path.Value[1..].Split('/').Select(segment => Unescape(segment, path))],
            _ => throw new ArgumentException("JSON Pointer must be empty or start with '/'.", nameof(path))
        };
    }

    private static string Unescape(string segment, JsonPointer path)
    {
        if (!segment.Contains('~', StringComparison.Ordinal)) return segment;
        var result = new StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '~')
            {
                result.Append(segment[index]);
                continue;
            }
            index++;
            if (index >= segment.Length) throw InvalidPointer(path);
            result.Append(segment[index] switch { '0' => '~', '1' => '/', _ => throw InvalidPointer(path) });
        }
        return result.ToString();
    }

    private static ArgumentException InvalidPointer(JsonPointer path) =>
        new($"JSON Pointer '{path}' contains an invalid escape.", nameof(path));

    private static bool TryGetArrayIndex(string segment, int count, out int index) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < count;

    private static bool TryGetArrayAddIndex(string segment, int count, out int index)
    {
        if (!string.Equals(segment, "-", StringComparison.Ordinal))
            return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index <= count;
        index = count;
        return true;
    }

    private static JsonObject ParseObject(JsonElement value) => JsonNode.Parse(value.GetRawText()) as JsonObject
        ?? throw new ArgumentException("JSON value must be an object.", nameof(value));

    private static JsonNode? ParseNode(JsonElement value) => JsonNode.Parse(value.GetRawText());
    private static JsonElement ToElement(JsonNode? node) => JsonSerializer.SerializeToElement(node);
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ReadOnlyGameState(JsonElement snapshot)
{
    private readonly GameState _state = new(snapshot);

    public JsonElement Snapshot => _state.Snapshot;
    public bool Contains(JsonPointer path) => _state.Contains(path);
    public JsonElement Get(JsonPointer path) => _state.Get(path);
}
