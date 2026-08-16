using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper;

/// <summary>可在 GameHelper attach 后记录精细 JSON Pointer 差异的 SessionState list。</summary>
[JsonConverter(typeof(TrackedListJsonConverterFactory))]
public sealed class TrackedList<T> : IList<T>, IReadOnlyList<T>, ITrackedCollection
{
    private readonly List<T> _items = [];
    private string _path = string.Empty;
    private ISessionStateCollectionTracker? _tracker;

    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _items[index];
        set
        {
            var path = Join(_path, index.ToString(CultureInfo.InvariantCulture));
            var wrapped = Wrap(value, path);
            _items[index] = wrapped;
            _tracker?.Replace(new JsonPointer(path), wrapped);
        }
    }

    /// <inheritdoc />
    public void Add(T item)
    {
        var index = _items.Count;
        var wrapped = Wrap(item, Join(_path, index.ToString(CultureInfo.InvariantCulture)));
        _items.Add(wrapped);
        _tracker?.Add(new JsonPointer(Join(_path, "-")), wrapped);
        ReattachFrom(index);
    }

    /// <inheritdoc />
    public void Clear()
    {
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            _items.RemoveAt(index);
            _tracker?.Remove(new JsonPointer(Join(_path, index.ToString(CultureInfo.InvariantCulture))));
        }
    }

    /// <inheritdoc />
    public bool Contains(T item)
    {
        return _items.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex)
    {
        _items.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    /// <inheritdoc />
    public int IndexOf(T item)
    {
        return _items.IndexOf(item);
    }

    /// <inheritdoc />
    public void Insert(int index, T item)
    {
        var wrapped = Wrap(item, Join(_path, index.ToString(CultureInfo.InvariantCulture)));
        _items.Insert(index, wrapped);
        _tracker?.Add(new JsonPointer(Join(_path, index.ToString(CultureInfo.InvariantCulture))), wrapped);
        ReattachFrom(index);
    }

    /// <inheritdoc />
    public bool Remove(T item)
    {
        var index = _items.IndexOf(item);
        if (index < 0) return false;

        RemoveAt(index);
        return true;
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        _tracker?.Remove(new JsonPointer(Join(_path, index.ToString(CultureInfo.InvariantCulture))));
        ReattachFrom(index);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    void ITrackedCollection.Attach(ISessionStateCollectionTracker tracker, string path)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        ReattachFrom(0);
    }

    internal void AddDeserialized(T item)
    {
        _items.Add(item);
    }

    private T Wrap(T value, string path)
    {
        return _tracker is null ? value : (T)_tracker.WrapValue(typeof(T), value, path);
    }

    private void ReattachFrom(int startIndex)
    {
        if (_tracker is null) return;

        for (var index = startIndex; index < _items.Count; index++)
        {
            var path = Join(_path, index.ToString(CultureInfo.InvariantCulture));
            _items[index] = (T)_tracker.WrapValue(typeof(T), _items[index], path);
        }
    }

    private static string Join(string parent, string segment)
    {
        return string.IsNullOrEmpty(parent) ? $"/{segment}" : $"{parent}/{segment}";
    }
}

internal sealed class TrackedListJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(TrackedList<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(TrackedListJsonConverter<>).MakeGenericType(itemType))!;
    }

    private sealed class TrackedListJsonConverter<TItem> : JsonConverter<TrackedList<TItem>>
    {
        public override TrackedList<TItem> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("TrackedList JSON must be an array.");

            var list = new TrackedList<TItem>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return list;

                var item = JsonSerializer.Deserialize<TItem>(ref reader, options)!;
                list.AddDeserialized(item);
            }

            throw new JsonException("TrackedList JSON array is incomplete.");
        }

        public override void Write(Utf8JsonWriter writer, TrackedList<TItem> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}
