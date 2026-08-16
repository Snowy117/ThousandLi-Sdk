using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>模型 JSON 流事件基类，携带事件对应的 JSON Pointer 路径。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract record JsonStreamEvent(string Path)
{
    /// <summary>创建对象开始事件。</summary>
    public static JsonStreamEvent ObjectStarted(string path)
    {
        return new JsonStreamObjectStartedEvent(path);
    }

    /// <summary>创建对象完成事件。</summary>
    public static JsonStreamEvent ObjectCompleted(string path)
    {
        return new JsonStreamObjectCompletedEvent(path);
    }

    /// <summary>创建数组开始事件。</summary>
    public static JsonStreamEvent ArrayStarted(string path)
    {
        return new JsonStreamArrayStartedEvent(path);
    }

    /// <summary>创建数组完成事件。</summary>
    public static JsonStreamEvent ArrayCompleted(string path)
    {
        return new JsonStreamArrayCompletedEvent(path);
    }

    /// <summary>创建属性名事件。</summary>
    public static JsonStreamEvent PropertyName(string path, string name)
    {
        return new JsonStreamPropertyNameEvent(path, name);
    }

    /// <summary>创建字符串开始事件。</summary>
    public static JsonStreamEvent StringStarted(string path)
    {
        return new JsonStreamStringStartedEvent(path);
    }

    /// <summary>创建字符串增量事件。</summary>
    public static JsonStreamEvent StringChunk(string path, string value)
    {
        return new JsonStreamStringChunkEvent(path, value);
    }

    /// <summary>创建字符串完成事件。</summary>
    public static JsonStreamEvent StringCompleted(string path)
    {
        return new JsonStreamStringCompletedEvent(path);
    }

    /// <summary>创建数字值事件。</summary>
    public static JsonStreamEvent NumberValue(string path, string rawValue)
    {
        return new JsonStreamNumberValueEvent(path, rawValue);
    }

    /// <summary>创建布尔值事件。</summary>
    public static JsonStreamEvent BooleanValue(string path, bool value)
    {
        return new JsonStreamBooleanValueEvent(path, value);
    }

    /// <summary>创建 null 值事件。</summary>
    public static JsonStreamEvent NullValue(string path)
    {
        return new JsonStreamNullValueEvent(path);
    }
}

/// <summary>对象开始事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamObjectStartedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>对象完成事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamObjectCompletedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>数组开始事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamArrayStartedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>数组完成事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamArrayCompletedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>属性名事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamPropertyNameEvent(string Path, string Name) : JsonStreamEvent(Path);

/// <summary>字符串开始事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamStringStartedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>字符串增量事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamStringChunkEvent(string Path, string Value) : JsonStreamEvent(Path);

/// <summary>字符串完成事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamStringCompletedEvent(string Path) : JsonStreamEvent(Path);

/// <summary>数字值事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamNumberValueEvent(string Path, string RawValue) : JsonStreamEvent(Path);

/// <summary>布尔值事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamBooleanValueEvent(string Path, bool Value) : JsonStreamEvent(Path);

/// <summary>null 值事件。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record JsonStreamNullValueEvent(string Path) : JsonStreamEvent(Path);
