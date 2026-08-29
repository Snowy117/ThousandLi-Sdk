using System.Text;
using System.Text.Json;

namespace ThousandLi.Contracts;

public sealed partial class JsonStreamParser
{
    private void BeginNumber(char c, string path)
    {
        _numberBuffer.Clear();
        _numberBuffer.Append(c);
        _numberPath = path;
    }

    private void CompleteNumber(List<JsonStreamEvent> events)
    {
        var value = _numberBuffer.ToString();
        if (!IsValidJsonNumber(value)) ThrowJson($"Invalid JSON number '{value}'");

        events.Add(JsonStreamEvent.NumberValue(_numberPath, value));
        _numberBuffer.Clear();
        _numberPath = string.Empty;
        CompleteValue();
    }

    private static bool IsValidJsonNumber(string value)
    {
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, default);
            return reader.Read()
                   && reader.TokenType == JsonTokenType.Number
                   && reader.BytesConsumed == utf8.Length
                   && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
