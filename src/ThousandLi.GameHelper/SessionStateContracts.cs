using JetBrains.Annotations;

namespace ThousandLi.GameHelper;

/// <summary>标记 Game Package 的唯一强类型 SessionState 根类型。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[MeansImplicitUse]
public sealed class SessionStateRootAttribute : Attribute;

/// <summary>声明仅持久化、不暴露给 AI 的 SessionState 成员。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SessionStateMemberAttribute : Attribute;

/// <summary>声明持久化且 AI 可见、可由 VariableUpdate 更新的 SessionState 成员。</summary>
[AttributeUsage(AttributeTargets.Property)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class AiStateMemberAttribute(int order, string description) : Attribute
{
    /// <summary>成员在状态渲染与 schema 渲染中的确定性顺序。</summary>
    public int Order { get; } = order;

    /// <summary>提供给 AI 理解此变量语义的描述。</summary>
    public string Description { get; } = description;

    /// <summary>提供给 AI 的更新规则；为空时不渲染规则块。</summary>
    public string? UpdateRule { get; init; }

    /// <summary>数字成员的最小值边界；使用字符串是为了支持 attribute 可选参数。</summary>
    public string? Min { get; init; }

    /// <summary>数字成员的最大值边界；使用字符串是为了支持 attribute 可选参数。</summary>
    public string? Max { get; init; }
}

/// <summary>SessionState package/root/member 合同错误。</summary>
public sealed class SessionStateContractException : InvalidOperationException
{
    /// <summary>创建 SessionState 合同异常。</summary>
    public SessionStateContractException(string message)
        : base(message)
    {
    }

    /// <summary>创建无消息的 SessionState 合同异常（满足标准异常构造器约定）。</summary>
    // ReSharper disable once UnusedMember.Global
    public SessionStateContractException()
    {
    }

    /// <summary>创建带内部异常的 SessionState 合同异常。</summary>
    // ReSharper disable once UnusedMember.Global
    public SessionStateContractException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>VariableUpdate 模型 patch proposal 无法通过验证。</summary>
public sealed class VariableUpdateValidationException : InvalidOperationException
{
    /// <summary>创建 VariableUpdate 验证异常。</summary>
    public VariableUpdateValidationException(string message)
        : base(message)
    {
    }

    /// <summary>创建带内部异常的 VariableUpdate 验证异常。</summary>
    // ReSharper disable once UnusedMember.Global
    public VariableUpdateValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>创建无消息的 VariableUpdate 验证异常（满足标准异常构造器约定）。</summary>
    // ReSharper disable once UnusedMember.Global
    public VariableUpdateValidationException()
    {
    }
}
