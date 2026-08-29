namespace ThousandLi.Contracts;

public sealed partial class JsonStreamParser
{
    private void BeginLiteral(string expected, string path)
    {
        _literalExpected = expected;
        _literalIndex = 1;
        _literalPath = path;
    }

    private void ProcessLiteralChar(char c, List<JsonStreamEvent> events)
    {
        var expected = _literalExpected ?? throw new InvalidOperationException("No literal is being parsed.");
        if (c != expected[_literalIndex]) ThrowUnexpected(c, $"'{expected[_literalIndex]}'");

        _literalIndex++;
        if (_literalIndex < expected.Length) return;

        var path = _literalPath;
        _literalExpected = null;
        _literalIndex = 0;
        _literalPath = string.Empty;

        switch (expected)
        {
            case "true":
                events.Add(JsonStreamEvent.BooleanValue(path, value: true));
                break;
            case "false":
                events.Add(JsonStreamEvent.BooleanValue(path, value: false));
                break;
            case "null":
                events.Add(JsonStreamEvent.NullValue(path));
                break;
            default:
                throw new InvalidOperationException($"Unknown literal '{expected}'.");
        }

        CompleteValue();
    }
}
