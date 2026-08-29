namespace ThousandLi.Contracts;

public sealed partial class JsonStreamParser
{
    private void StartPropertyName()
    {
        BeginString(StringKind.PropertyName, string.Empty, events: null);
    }

    private void BeginString(StringKind kind, string path, List<JsonStreamEvent>? events)
    {
        _stringKind = kind;
        _stringPath = path;
        _stringBuffer.Clear();
        _stringChunkBuffer.Clear();
        _afterBackslash = false;
        _unicodeDigitsRemaining = 0;
        _unicodeValue = 0;
        _pendingHighSurrogate = null;

        if (kind == StringKind.Value) events?.Add(JsonStreamEvent.StringStarted(path));
    }

    private void ProcessStringChar(char c, List<JsonStreamEvent> events)
    {
        if (_unicodeDigitsRemaining > 0)
        {
            ProcessUnicodeHex(c);
            return;
        }

        if (_afterBackslash)
        {
            ProcessEscapedChar(c);
            return;
        }

        switch (c)
        {
            case '\\':
                _afterBackslash = true;
                return;
            case '"':
                EndString(events);
                return;
        }

        if (c < ' ') ThrowJson("Control characters must be escaped inside JSON strings");

        AppendDecodedChar(c);
    }

    private void ProcessEscapedChar(char c)
    {
        _afterBackslash = false;
        switch (c)
        {
            case '"':
                AppendDecodedChar('"');
                return;
            case '\\':
                AppendDecodedChar('\\');
                return;
            case '/':
                AppendDecodedChar('/');
                return;
            case 'b':
                AppendDecodedChar('\b');
                return;
            case 'f':
                AppendDecodedChar('\f');
                return;
            case 'n':
                AppendDecodedChar('\n');
                return;
            case 'r':
                AppendDecodedChar('\r');
                return;
            case 't':
                AppendDecodedChar('\t');
                return;
            case 'u':
                _unicodeDigitsRemaining = 4;
                _unicodeValue = 0;
                return;
            default:
                ThrowUnexpected(c, "valid JSON escape sequence");
                return;
        }
    }

    private void ProcessUnicodeHex(char c)
    {
        var value = HexValue(c);
        if (value < 0) ThrowUnexpected(c, "hexadecimal digit");

        _unicodeValue = (_unicodeValue << 4) | value;
        _unicodeDigitsRemaining--;

        if (_unicodeDigitsRemaining != 0) return;
        AppendDecodedChar((char)_unicodeValue);
        _unicodeValue = 0;
    }

    private void AppendDecodedChar(char c)
    {
        if (_pendingHighSurrogate is { } highSurrogate)
        {
            if (!char.IsLowSurrogate(c)) ThrowJson("High surrogate in JSON string was not followed by a low surrogate");

            AppendStringChar(highSurrogate);
            AppendStringChar(c);
            _pendingHighSurrogate = null;
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        if (char.IsLowSurrogate(c)) ThrowJson("Low surrogate in JSON string did not follow a high surrogate");

        AppendStringChar(c);
    }

    private void AppendStringChar(char c)
    {
        _stringBuffer.Append(c);

        if (_stringKind == StringKind.Value) _stringChunkBuffer.Append(c);
    }

    private void EndString(List<JsonStreamEvent> events)
    {
        if (_pendingHighSurrogate is not null)
            ThrowJson("High surrogate in JSON string was not followed by a low surrogate");

        if (_stringKind == StringKind.PropertyName)
        {
            var propertyName = _stringBuffer.ToString();
            var frame = _frames[^1];
            frame.PendingPropertyName = propertyName;
            frame.State = ContainerState.ObjectExpectColon;
            events.Add(JsonStreamEvent.PropertyName(AppendPath(frame.Path, propertyName), propertyName));
            ResetString();
            return;
        }

        FlushStringChunk(events);
        events.Add(JsonStreamEvent.StringCompleted(_stringPath));
        ResetString();
        CompleteValue();
    }

    private void FlushStringChunk(List<JsonStreamEvent> events)
    {
        if (_stringChunkBuffer.Length == 0) return;

        events.Add(JsonStreamEvent.StringChunk(_stringPath, _stringChunkBuffer.ToString()));
        _stringChunkBuffer.Clear();
    }

    private void ResetString()
    {
        _stringKind = StringKind.None;
        _stringPath = string.Empty;
        _stringBuffer.Clear();
        _stringChunkBuffer.Clear();
        _afterBackslash = false;
        _unicodeDigitsRemaining = 0;
        _unicodeValue = 0;
        _pendingHighSurrogate = null;
    }
}
