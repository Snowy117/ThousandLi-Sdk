using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThousandLi.Contracts;

/// <summary>可在 GameHelper attach 后记录精细 JSON Pointer 差异并按 key ordinal 序列化的 SessionState dictionary。</summary>
[JsonConverter(typeof(TrackedDictionaryJsonConverterFactory))]
public sealed class TrackedDictionary<T> : IDictionary<string, T>, IReadOnlyDictionary<string, T>, ITrackedCollection
{
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);
    private string _path = string.Empty;
    private ISessionStateCollectionTracker? _tracker;

    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    ICollection<string> IDictionary<string, T>.Keys => _items.Keys;

    ICollection<T> IDictionary<string, T>.Values => _items.Values;

    public T this[string key]
    {
        get => _items[key];
        set
        {
            ValidateKey(key);
            var path = Join(_path, Escape(key));
            var wrapped = Wrap(value, path);
            var exists = _items.ContainsKey(key);
            _items[key] = wrapped;
            if (exists) _tracker?.Replace(new JsonPointer(path), wrapped);
            else _tracker?.Add(new JsonPointer(path), wrapped);
        }
    }

    /// <inheritdoc />
    public void Add(string key, T value)
    {
        ValidateKey(key);
        var path = Join(_path, Escape(key));
        var wrapped = Wrap(value, path);
        _items.Add(key, wrapped);
        _tracker?.Add(new JsonPointer(path), wrapped);
    }

    /// <inheritdoc />
    public void Add(KeyValuePair<string, T> item)
    {
        Add(item.Key, item.Value);
    }

    /// <inheritdoc />
    public void Clear()
    {
        foreach (var key in _items.Keys.Order(StringComparer.Ordinal).ToArray()) Remove(key);
    }

    /// <inheritdoc />
    public bool Contains(KeyValuePair<string, T> item)
    {
        return ((ICollection<KeyValuePair<string, T>>)_items).Contains(item);
    }

    public bool ContainsKey(string key)
    {
        ValidateKey(key);
        return _items.ContainsKey(key);
    }

    /// <inheritdoc />
    public void CopyTo(KeyValuePair<string, T>[] array, int arrayIndex)
    {
        foreach (var item in this) array[arrayIndex++] = item;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
    {
        return _items.OrderBy(pair => pair.Key, StringComparer.Ordinal).GetEnumerator();
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        ValidateKey(key);
        if (!_items.Remove(key)) return false;

        _tracker?.Remove(new JsonPointer(Join(_path, Escape(key))));
        return true;
    }

    /// <inheritdoc />
    public bool Remove(KeyValuePair<string, T> item)
    {
        return Contains(item) && Remove(item.Key);
    }

    public bool TryGetValue(string key, out T value)
    {
        ValidateKey(key);
        return _items.TryGetValue(key, out value!);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public IEnumerable<string> Keys => _items.Keys.Order(StringComparer.Ordinal);

    /// <inheritdoc />
    public IEnumerable<T> Values => Keys.Select(key => _items[key]);

    void ITrackedCollection.Attach(ISessionStateCollectionTracker tracker, string path)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        foreach (var key in _items.Keys.ToArray()) _items[key] = Wrap(_items[key], Join(_path, Escape(key)));
    }

    internal void AddDeserialized(string key, T value)
    {
        ValidateKey(key);
        _items.Add(key, value);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith('_'))
            throw new ArgumentException("TrackedDictionary keys cannot start with '_'.", nameof(key));
    }

    private T Wrap(T value, string path)
    {
        return _tracker is null ? value : (T)_tracker.WrapValue(typeof(T), value, path);
    }

    private static string Join(string parent, string segment)
    {
        return string.IsNullOrEmpty(parent) ? $"/{segment}" : $"{parent}/{segment}";
    }

    private static string Escape(string segment)
    {
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
}

internal sealed class TrackedDictionaryJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(TrackedDictionary<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(TrackedDictionaryJsonConverter<>).MakeGenericType(itemType))!;
    }

    private sealed class TrackedDictionaryJsonConverter<TValue> : JsonConverter<TrackedDictionary<TValue>>
    {
        public override TrackedDictionary<TValue> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("TrackedDictionary JSON must be an object.");

            var dictionary = new TrackedDictionary<TValue>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return dictionary;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("TrackedDictionary JSON property name expected.");

                var key = reader.GetString()!;
                reader.Read();
                var value = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                dictionary.AddDeserialized(key, value);
            }

            throw new JsonException("TrackedDictionary JSON object is incomplete.");
        }

        public override void Write(Utf8JsonWriter writer, TrackedDictionary<TValue> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value)
            {
                writer.WritePropertyName(pair.Key);
                JsonSerializer.Serialize(writer, pair.Value, options);
            }

            writer.WriteEndObject();
        }
    }
}
