namespace ThousandLi.Contracts;

public sealed partial class JsonStreamParser
{
    private static bool IsValueStart(char c)
    {
        return c is '{' or '[' or '"' or 't' or 'f' or 'n' or '-' or >= '0' and <= '9';
    }

    private static bool IsNumberContinuation(char c)
    {
        return c is >= '0' and <= '9' or '-' or '+' or '.' or 'e' or 'E';
    }

    private static bool IsJsonWhitespace(char c)
    {
        return c is ' ' or '\t' or '\r' or '\n';
    }

    private static int HexValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    private static string AppendPath(string parent, string segment)
    {
        var escaped = EscapePathSegment(segment);
        return parent.Length == 0 ? $"/{escaped}" : $"{parent}/{escaped}";
    }

    private static string EscapePathSegment(string segment)
    {
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private void ThrowUnexpected(char actual, string expected)
    {
        ThrowJson($"Expected {expected}, but found '{actual}'");
    }

    private void ThrowJson(string message)
    {
        throw new JsonStreamException(message, _position);
    }

    private enum StringKind
    {
        None,
        PropertyName,
        Value,
    }

    private enum ContainerKind
    {
        Object,
        Array,
    }

    private enum ContainerState
    {
        ObjectExpectPropertyOrEnd,
        ObjectExpectProperty,
        ObjectExpectColon,
        ObjectExpectValue,
        ObjectExpectCommaOrEnd,
        ArrayExpectValueOrEnd,
        ArrayExpectValue,
        ArrayExpectCommaOrEnd,
    }

    private sealed class ContainerFrame(ContainerKind kind, ContainerState state, string path)
    {
        public ContainerKind Kind { get; } = kind;

        public ContainerState State { get; set; } = state;

        public string Path { get; } = path;

        public string? PendingPropertyName { get; set; }

        public int NextArrayIndex { get; set; }
    }
}
