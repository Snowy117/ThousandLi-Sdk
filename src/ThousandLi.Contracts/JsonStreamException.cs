using System.Globalization;

namespace ThousandLi.Contracts;

/// <summary>
/// 增量 JSON 流解析失败：畸形结构、流结束时的不完整值、非法转义或非法数字。
/// <see cref="Position"/> 指向已连接的模型输出中的字符偏移。
/// </summary>
public sealed class JsonStreamException : Exception
{
    /// <summary>以消息与出错字符偏移创建解析失败。</summary>
    public JsonStreamException(string message, long position)
        : base(string.Create(CultureInfo.InvariantCulture, $"{message} (position {position})."))
    {
        Position = position;
    }

    // RCS1194 要求 Exception 实现标准构造器，此处为满足该规则。
    // ReSharper disable once UnusedMember.Global
    public JsonStreamException()
    {
    }

    // RCS1194 要求 Exception 实现标准构造器，此处为满足该规则。
    // ReSharper disable once UnusedMember.Global
    public JsonStreamException(string? message) : base(message)
    {
    }

    // RCS1194 要求 Exception 实现标准构造器，此处为满足该规则。
    // ReSharper disable once UnusedMember.Global
    public JsonStreamException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>出错位置在已连接输入中的字符偏移。</summary>
    public long Position { get; }
}
