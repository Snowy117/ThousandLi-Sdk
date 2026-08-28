using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// Internal incremental JSON parser producing <see cref="ExpertJsonStreamEvent"/> sequences from
/// arbitrary chunk boundaries. Accepts object and array roots only; scalar roots are rejected.
/// Not part of the public surface: experts consume events, not parser state.
/// </summary>
internal sealed class ExpertJsonStreamParser
{
    private const int MaxDepth = 128;

    private enum NodeState
    {
        ExpectRoot,
        ExpectValue,
        ExpectPropertyName,
        ExpectColon,
        ExpectObjectEntry,
        ExpectArrayEntry,
        Complete
    }

    private enum LiteralKind
    {
        None,
        String,
        Number,
        Keyword
    }

    private sealed class Container(string path, bool isObject)
    {
        public string Path { get; } = path;

        public bool IsObject { get; } = isObject;

        public bool HasElements { get; set; }

        public int ArrayIndex { get; set; }

        public string PendingChildPath { get; set; } = string.Empty;
    }

    private readonly List<ExpertJsonStreamEvent> _events = [];
    private readonly Stack<Container> _stack = new();
    private readonly StringBuilder _literal = new();
    private NodeState _state = NodeState.ExpectRoot;
    private LiteralKind _literalKind = LiteralKind.None;
    private bool _stringIsPropertyName;
    private string _valuePath = string.Empty;
    private bool _escapePhase;
    private int _unicodeDigitsRemaining;
    private uint _unicodeValue;
    private uint _pendingHighSurrogate;
    private long _line = 1;
    private long _column;
    private long _offset;

    public IReadOnlyList<ExpertJsonStreamEvent> Feed(ReadOnlySpan<char> chunk)
    {
        _events.Clear();
        foreach (var character in chunk)
            Consume(character);
        FlushStringChunk();
        return [.. _events];
    }

    public IReadOnlyList<ExpertJsonStreamEvent> Complete()
    {
        if (_state == NodeState.Complete)
            return [];
        if (_offset == 0)
            Fail("The stream does not contain a JSON value.");
        Fail("The stream ended with an incomplete JSON value.");
        return [];
    }

    private void Consume(char character)
    {
        var line = _line;
        var column = _column + 1;
        AdvancePosition(character);
        switch (_literalKind)
        {
            case LiteralKind.String:
                ConsumeStringCharacter(character, line, column);
                return;
            case LiteralKind.Number:
                ConsumeNumberCharacter(character, line, column);
                return;
            case LiteralKind.Keyword:
                ConsumeKeywordCharacter(character, line, column);
                return;
            case LiteralKind.None:
            default:
                switch (_state)
                {
                    case NodeState.ExpectRoot:
                    case NodeState.ExpectValue:
                        ConsumeValueStart(character, line, column);
                        break;
                    case NodeState.ExpectPropertyName:
                        ConsumePropertyNameStart(character, line, column);
                        break;
                    case NodeState.ExpectColon:
                        ConsumeColon(character, line, column);
                        break;
                    case NodeState.ExpectObjectEntry:
                        ConsumeObjectEntry(character, line, column);
                        break;
                    case NodeState.ExpectArrayEntry:
                        ConsumeArrayEntry(character, line, column);
                        break;
                    case NodeState.Complete:
                        if (IsJsonWhitespace(character))
                            return;
                        Fail("Trailing content after the end of the JSON value.", line, column);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected parser state '{_state}'.");
                }

                break;
        }
    }

    private void ConsumeValueStart(char character, long line, long column)
    {
        if (IsJsonWhitespace(character))
            return;
        if (character == ']' && _stack.Count > 0)
        {
            var parent = _stack.Peek();
            if (parent is { IsObject: false, HasElements: false })
            {
                CloseContainer();
                return;
            }
        }

        if (_state == NodeState.ExpectRoot && character is not ('{' or '['))
            Fail("The root of a streamed JSON value must be an object or an array.", line, column);

        switch (character)
        {
            case '{':
            case '[':
                PushContainer(character == '{', line, column);
                break;
            case '"':
                StartString(isPropertyName: false);
                break;
            case '-' or >= '0' and <= '9':
                StartNumber(character);
                break;
            case 't' or 'f' or 'n':
                StartKeyword(character);
                break;
            default:
                Fail($"Unexpected character '{character}' while expecting a value.", line, column);
                break;
        }
    }

    private void ConsumePropertyNameStart(char character, long line, long column)
    {
        if (IsJsonWhitespace(character))
            return;
        switch (character)
        {
            case '}' when !_stack.Peek().HasElements:
                // '}' may close only a still-empty object; after a separator a property name is
                // required, so this is a trailing comma.
                CloseContainer();
                return;
            case '}':
                Fail("Trailing commas are not allowed inside an object.", line, column);
                break;
            case '"':
                StartString(isPropertyName: true);
                return;
        }

        Fail($"Unexpected character '{character}' while expecting a property name.", line, column);
    }

    private void ConsumeColon(char character, long line, long column)
    {
        if (IsJsonWhitespace(character))
            return;
        if (character != ':')
            Fail($"Expected ':' after a property name but found '{character}'.", line, column);
        _valuePath = _stack.Peek().PendingChildPath;
        _state = NodeState.ExpectValue;
    }

    private void ConsumeObjectEntry(char character, long line, long column)
    {
        if (IsJsonWhitespace(character))
            return;
        switch (character)
        {
            case ',':
                _state = NodeState.ExpectPropertyName;
                break;
            case '}':
                CloseContainer();
                break;
            default:
                Fail($"Unexpected character '{character}' inside an object.", line, column);
                break;
        }
    }

    private void ConsumeArrayEntry(char character, long line, long column)
    {
        if (IsJsonWhitespace(character))
            return;
        switch (character)
        {
            case ',':
                var frame = _stack.Peek();
                frame.ArrayIndex++;
                _valuePath = $"{frame.Path}/{frame.ArrayIndex}";
                _state = NodeState.ExpectValue;
                break;

            case ']':
                CloseContainer();
                break;
            default:
                Fail($"Unexpected character '{character}' inside an array.", line, column);
                break;
        }
    }

    private void PushContainer(bool isObject, long line, long column)
    {
        if (_stack.Count >= MaxDepth)
            Fail($"The JSON value exceeds the maximum nesting depth of {MaxDepth}.", line, column);
        var path = _valuePath;
        Emit(isObject ? new ExpertJsonObjectStartedEvent(path) : new ExpertJsonArrayStartedEvent(path));
        _stack.Push(new Container(path, isObject));
        if (isObject)
        {
            _state = NodeState.ExpectPropertyName;
        }
        else
        {
            _valuePath = $"{path}/0";
            _state = NodeState.ExpectValue;
        }
    }

    private void CloseContainer()
    {
        var frame = _stack.Pop();
        Emit(frame.IsObject
            ? new ExpertJsonObjectCompletedEvent(frame.Path)
            : new ExpertJsonArrayCompletedEvent(frame.Path));
        FinishValue();
    }

    private void FinishValue()
    {
        if (_stack.Count == 0)
        {
            _state = NodeState.Complete;
            _valuePath = string.Empty;
            return;
        }

        var parent = _stack.Peek();
        parent.HasElements = true;
        _state = parent.IsObject ? NodeState.ExpectObjectEntry : NodeState.ExpectArrayEntry;
        _valuePath = string.Empty;
    }

    private void StartString(bool isPropertyName)
    {
        _literalKind = LiteralKind.String;
        _stringIsPropertyName = isPropertyName;
        _escapePhase = false;
        _unicodeDigitsRemaining = 0;
        _unicodeValue = 0;
        _pendingHighSurrogate = 0;
        if (!isPropertyName)
            Emit(new ExpertJsonStringStartedEvent(_valuePath));
    }

    private void ConsumeStringCharacter(char character, long line, long column)
    {
        if (_unicodeDigitsRemaining > 0)
        {
            AppendUnicodeHexDigit(character, line, column);
            return;
        }

        if (_escapePhase)
        {
            ConsumeEscape(character, line, column);
            return;
        }

        switch (character)
        {
            case '\\':
                _escapePhase = true;
                break;
            case '"':
                CloseString();
                break;
            case < ' ':
                Fail("Control characters are not allowed inside JSON strings.", line, column);
                break;
            default:
                if (_pendingHighSurrogate != 0)
                    Fail("An escaped high surrogate must be followed by its low surrogate escape.", line, column);
                _literal.Append(character);
                break;
        }
    }

    private void ConsumeEscape(char character, long line, long column)
    {
        _escapePhase = false;
        switch (character)
        {
            case '"':
                _literal.Append('"');
                break;
            case '\\':
                _literal.Append('\\');
                break;
            case '/':
                _literal.Append('/');
                break;
            case 'b':
                _literal.Append('\b');
                break;
            case 'f':
                _literal.Append('\f');
                break;
            case 'n':
                _literal.Append('\n');
                break;
            case 'r':
                _literal.Append('\r');
                break;
            case 't':
                _literal.Append('\t');
                break;
            case 'u':
                _unicodeDigitsRemaining = 4;
                _unicodeValue = 0;
                break;
            default:
                Fail($"Invalid escape sequence '\\{character}' inside a JSON string.", line, column);
                break;
        }
    }

    private void AppendUnicodeHexDigit(char character, long line, long column)
    {
        var digitValue = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
        if (digitValue < 0)
            Fail($"Invalid hexadecimal digit '{character}' in a Unicode escape sequence.", line, column);
        _unicodeValue = (_unicodeValue << 4) | (uint)digitValue;
        _unicodeDigitsRemaining--;
        if (_unicodeDigitsRemaining > 0)
            return;

        var unit = _unicodeValue;
        _unicodeValue = 0;
        if (_pendingHighSurrogate != 0)
        {
            if (!char.IsLowSurrogate((char)unit))
                Fail("An escaped high surrogate was not followed by a low surrogate.", line, column);
            _literal.Append(char.ConvertFromUtf32(
                (int)(0x10000 + (((_pendingHighSurrogate - 0xD800) << 10) | (unit - 0xDC00)))));
            _pendingHighSurrogate = 0;
            return;
        }

        // System.Text.Json cannot materialize a value containing an escaped lone surrogate, so a
        // lone low surrogate is rejected at parse time instead of failing later at value access.
        if (char.IsLowSurrogate((char)unit))
            Fail("An escaped low surrogate must extend a preceding high surrogate escape.", line, column);

        if (char.IsHighSurrogate((char)unit))
        {
            _pendingHighSurrogate = unit;
            return;
        }

        _literal.Append((char)unit);
    }

    private void CloseString()
    {
        if (_pendingHighSurrogate != 0)
            Fail("The string ends with an unpaired high surrogate escape.");
        if (_stringIsPropertyName)
        {
            var name = _literal.ToString();
            var owner = _stack.Peek();
            Emit(new ExpertJsonPropertyNameEvent(owner.Path, name));
            owner.PendingChildPath = $"{owner.Path}/{EscapePointerToken(name)}";
            _state = NodeState.ExpectColon;
        }
        else
        {
            var path = _valuePath;
            FlushDecodedString(path);
            Emit(new ExpertJsonStringCompletedEvent(path));
            FinishValue();
        }

        _literal.Clear();
        _literalKind = LiteralKind.None;
    }

    private void FlushStringChunk()
    {
        if (_literalKind != LiteralKind.String || _stringIsPropertyName || _literal.Length == 0)
            return;
        FlushDecodedString(_valuePath);
    }

    private void FlushDecodedString(string path)
    {
        if (_literal.Length == 0)
            return;
        Emit(new ExpertJsonStringChunkEvent(path, _literal.ToString()));
        _literal.Clear();
    }

    private void StartNumber(char character)
    {
        _literalKind = LiteralKind.Number;
        _literal.Clear();
        _literal.Append(character);
    }

    private void ConsumeNumberCharacter(char character, long line, long column)
    {
        if (character is '-' or '+' or '.' or 'e' or 'E' or >= '0' and <= '9')
        {
            _literal.Append(character);
            return;
        }

        CompleteNumber(line, column);
        _literalKind = LiteralKind.None;
        if (IsJsonWhitespace(character))
        {
            FinishValue();
            return;
        }

        switch (_state)
        {
            case NodeState.ExpectRoot:
            case NodeState.ExpectValue:
            case NodeState.ExpectPropertyName:
            case NodeState.ExpectColon:
            case NodeState.Complete:
            default:
                Fail($"Unexpected character '{character}' after a number.", line, column);
                break;
            case NodeState.ExpectObjectEntry:
                ConsumeObjectEntry(character, line, column);
                break;
            case NodeState.ExpectArrayEntry:
                ConsumeArrayEntry(character, line, column);
                break;
        }
    }

    private void CompleteNumber(long line, long column)
    {
        var raw = _literal.ToString();
        try
        {
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.ValueKind != JsonValueKind.Number)
                throw new JsonException("The token is not a JSON number.");
        }
        catch (JsonException exception)
        {
            Fail($"'{raw}' is not a valid JSON number.", line, column, exception);
        }

        Emit(new ExpertJsonNumberValueEvent(_valuePath, raw));
        _literal.Clear();
        FinishValue();
    }

    private void StartKeyword(char character)
    {
        _literalKind = LiteralKind.Keyword;
        _literal.Clear();
        _literal.Append(character);
    }

    private void ConsumeKeywordCharacter(char character, long line, long column)
    {
        var expected = _literal[0] switch
        {
            't' => "true",
            'f' => "false",
            _ => "null"
        };
        if (_literal.Length >= expected.Length || expected[_literal.Length] != character)
            Fail($"'{_literal}{character}' does not form a valid JSON keyword.", line, column);

        _literal.Append(character);
        if (_literal.Length < expected.Length)
            return;

        var path = _valuePath;
        switch (expected)
        {
            case "true":
                Emit(new ExpertJsonBooleanValueEvent(path, true));
                break;
            case "false":
                Emit(new ExpertJsonBooleanValueEvent(path, false));
                break;
            default:
                Emit(new ExpertJsonNullValueEvent(path));
                break;
        }

        _literal.Clear();
        _literalKind = LiteralKind.None;
        FinishValue();
    }

    private void Emit(ExpertJsonStreamEvent streamEvent) => _events.Add(streamEvent);

    /// <summary>JSON allows only these four characters as insignificant whitespace; other Unicode
    /// whitespace (form feed, vertical tab, NBSP, …) is malformed and must match System.Text.Json
    /// rejection semantics.</summary>
    private static bool IsJsonWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';

    private void AdvancePosition(char character)
    {
        _offset++;
        if (character == '\n')
        {
            _line++;
            _column = 0;
        }
        else
        {
            _column++;
        }
    }

    [DoesNotReturn]
    private void Fail(string message, long? line = null, long? column = null, Exception? innerException = null) =>
        throw new ExpertJsonStreamException(
            $"Malformed streamed JSON: {message}",
            line ?? _line,
            Math.Max(column ?? _column, 1),
            innerException);

    private static string EscapePointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
