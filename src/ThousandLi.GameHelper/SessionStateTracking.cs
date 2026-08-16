using System.Collections;
using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper;

internal interface ITrackedCollection
{
    void Attach(ISessionStateCollectionTracker tracker, string path);
}

internal interface ISessionStateCollectionTracker
{
    object WrapValue(Type declaredType, object? value, string path);

    void Add(JsonPointer path, object? value);

    void Replace(JsonPointer path, object? value);

    void Remove(JsonPointer path);
}

/// <summary>
///     把 SessionState 精细差异写回 <see cref="GameState" /> 的集合跟踪器。
///     通过 <see cref="SessionStateProxyFactory" /> 的代理把对象/集合写入映射为 GameState 变更。
/// </summary>
internal sealed class GameStateSessionStateTracker(
    GameState state,
    SessionStateContract contract,
    string pathPrefix = "")
    : ISessionStateCollectionTracker, ISessionStateChangeSink
{
    void ISessionStateChangeSink.Replace(string path, object? value)
    {
        Replace(new JsonPointer(path), value);
    }

    public object WrapValue(Type declaredType, object? value, string path)
    {
        return SessionStateProxyFactory.Default.WrapValue(declaredType, value, path, contract, this);
    }

    public void Add(JsonPointer path, object? value)
    {
        state.Add(MapPath(path), Serialize(value));
    }

    public void Replace(JsonPointer path, object? value)
    {
        state.Replace(MapPath(path), Serialize(value));
    }

    public void Remove(JsonPointer path)
    {
        state.Remove(MapPath(path));
    }

    private JsonPointer MapPath(JsonPointer relativePath)
    {
        var relative = relativePath.Value;
        return new JsonPointer(string.IsNullOrEmpty(relative) ? pathPrefix : pathPrefix + relative);
    }

    private JsonElement Serialize(object? value)
    {
        return value is null
            ? JsonSerializer.SerializeToElement<object?>(value: null, SessionStateJson.Options)
            : SessionStateJson.Serialize(value, value.GetType(), contract);
    }
}

internal interface ISessionStateChangeSink
{
    void Replace(string path, object? value);
}

internal static class SessionStateJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static JsonElement Serialize(object value, Type type, SessionStateContract contract)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(contract);
        return SerializeCore(value, type, contract).Clone();
    }

    private static JsonElement SerializeCore(object? value, Type type, SessionStateContract contract)
    {
        if (value is null) return JsonSerializer.SerializeToElement<object?>(value: null, Options);

        type = Nullable.GetUnderlyingType(type) ?? type;
        return type switch
        {
            _ when contract.IsStateObjectType(type) => SerializeObject(value, type, contract),
            _ when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(TrackedList<>) => SerializeList(
                EnumerateTrackedList(value), type.GetGenericArguments()[0], contract),
            _ when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(TrackedDictionary<>) =>
                SerializeDictionary(EnumerateTrackedDictionary(value), type.GetGenericArguments()[0], contract),
            _ => JsonSerializer.SerializeToElement(value, type, Options)
        };
    }

    private static JsonElement SerializeObject(object value, Type type, SessionStateContract contract)
    {
        var jsonObject = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in contract.GetMembers(type))
        {
            jsonObject[member.JsonName] = SerializeCore(member.Property.GetValue(value), member.Property.PropertyType,
                contract);
        }

        return JsonSerializer.SerializeToElement(jsonObject, Options);
    }

    private static JsonElement SerializeList(IEnumerable<object?> values, Type itemType, SessionStateContract contract)
    {
        var jsonValues = values.Select(value => SerializeCore(value, itemType, contract)).ToArray();
        return JsonSerializer.SerializeToElement(jsonValues, Options);
    }

    private static JsonElement SerializeDictionary(IEnumerable<KeyValuePair<string, object?>> values, Type valueType,
        SessionStateContract contract)
    {
        var jsonValues = values.ToDictionary(
            pair => pair.Key,
            pair => SerializeCore(pair.Value, valueType, contract),
            StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(jsonValues, Options);
    }

    private static IEnumerable<object?> EnumerateTrackedList(object value)
    {
        return ((IEnumerable)value).Cast<object?>();
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateTrackedDictionary(object value)
    {
        return ((IEnumerable)value).Cast<object>().Select(entry =>
        {
            var entryType = entry.GetType();
            return new KeyValuePair<string, object?>(
                (string)entryType.GetProperty("Key")!.GetValue(entry)!,
                entryType.GetProperty("Value")!.GetValue(entry));
        });
    }

    public static object? Deserialize(JsonElement snapshot, Type type)
    {
        return snapshot.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("SessionState snapshot cannot be undefined.", nameof(snapshot))
            : JsonSerializer.Deserialize(snapshot.GetRawText(), type, Options);
    }
}
