using System.Text;

namespace ThousandLi.Contracts;

/// <summary>
/// 增量 JSON 流解析器：把任意 chunk 边界的模型输出解析为 <see cref="JsonStreamEvent"/> 序列。
/// 通过 <see cref="Feed(string)"/> 分块喂入字符，通过 <see cref="Complete"/> 结束流并校验完整性。
/// </summary>
public sealed partial class JsonStreamParser
{
    private readonly List<ContainerFrame> _frames = [];
    private readonly StringBuilder _numberBuffer = new();
    private readonly StringBuilder _stringBuffer = new();
    private readonly StringBuilder _stringChunkBuffer = new();
    private bool _afterBackslash;

    private string? _literalExpected;
    private int _literalIndex;
    private string _literalPath = string.Empty;

    private string _numberPath = string.Empty;
    private char? _pendingHighSurrogate;
    private long _position;
    private bool _rootValueCompleted;
    private bool _rootValueStarted;

    private StringKind _stringKind;
    private string _stringPath = string.Empty;
    private int _unicodeDigitsRemaining;
    private int _unicodeValue;

    /// <summary>喂入一块字符并返回这段输入产生的流事件。字符串值的增量按喂入边界冲刷。</summary>
    public IReadOnlyList<JsonStreamEvent> Feed(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        return Feed(chunk.AsSpan());
    }

    private List<JsonStreamEvent> Feed(ReadOnlySpan<char> chunk)
    {
        var events = new List<JsonStreamEvent>();
        var index = 0;

        while (index < chunk.Length)
        {
            var consumed = ProcessNext(chunk[index], events);
            if (!consumed) continue;

            index++;
            _position++;
        }

        if (_stringKind == StringKind.Value) FlushStringChunk(events);

        return events;
    }

    /// <summary>结束流：校验没有未完结的字符串、字面量、数字或容器，且至少提供了一个完整 JSON 值。</summary>
    public IReadOnlyList<JsonStreamEvent> Complete()
    {
        var events = new List<JsonStreamEvent>();

        if (_stringKind != StringKind.None) ThrowJson("Unterminated JSON string");

        if (_literalExpected is not null) ThrowJson($"Incomplete JSON literal '{_literalExpected}'");

        if (_numberBuffer.Length > 0) CompleteNumber(events);

        if (_frames.Count > 0) ThrowJson("Incomplete JSON container");

        if (!_rootValueCompleted) ThrowJson("No complete JSON value was provided");

        return events;
    }

    private bool ProcessNext(char c, List<JsonStreamEvent> events)
    {
        if (_stringKind != StringKind.None)
        {
            ProcessStringChar(c, events);
            return true;
        }

        if (_literalExpected is not null)
        {
            ProcessLiteralChar(c, events);
            return true;
        }

        if (_numberBuffer.Length > 0)
        {
            if (IsNumberContinuation(c))
            {
                _numberBuffer.Append(c);
                return true;
            }

            CompleteNumber(events);
            return false;
        }

        ProcessJsonChar(c, events);
        return true;
    }
}
