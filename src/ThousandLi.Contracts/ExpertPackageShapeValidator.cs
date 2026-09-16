using System.Reflection;

namespace ThousandLi.Contracts;

/// <summary>
/// Expert Package 入口程序集的装载形状校验结果：入口点声明的抽象锚类型、具体专家类型与
/// 契约 id。SDK DevHost 与平台 Host 的装载器都调用 <see cref="ExpertPackageShapeValidator.Validate"/>
/// 获得同一份规则判定，各自保留自己的 AssemblyLoadContext 与身份记录。
/// </summary>
public sealed record ExpertPackageShape
{
    internal ExpertPackageShape(string contractId, Type abstractExpertType, Type concreteExpertType)
    {
        ContractId = contractId;
        AbstractExpertType = abstractExpertType;
        ConcreteExpertType = concreteExpertType;
    }

    /// <summary>锚类型 <c>[ExpertContract]</c> 声明的契约 id。</summary>
    public string ContractId { get; }

    /// <summary>入口点声明的抽象锚类型。</summary>
    public Type AbstractExpertType { get; }

    /// <summary>入口点声明的具体专家类型。</summary>
    public Type ConcreteExpertType { get; }
}

/// <summary>
/// Expert Package 入口程序集的形状校验规则（单一实现）：恰好一个
/// <c>[ExpertPackageEntryPoint]</c>、抽象锚类型继承 <see cref="ExpertBase"/> 且为抽象类并携带
/// <c>[ExpertContract]</c>、具体类型继承锚类型且为同程序集内声明的具体类并具备公共无参构造。
/// </summary>
public static class ExpertPackageShapeValidator
{
    /// <summary>校验一个已加载的 Expert Package 入口程序集并返回其装载形状。</summary>
    /// <param name="assembly">已加载的入口程序集。</param>
    /// <returns>装载形状（契约 id + 锚类型 + 具体类型 + 构造委托）。</returns>
    public static ExpertPackageShape Validate(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var attributes = assembly.GetCustomAttributes<ExpertPackageEntryPointAttribute>().ToArray();
        if (attributes.Length != 1)
        {
            throw new InvalidOperationException(
                $"An Expert Package Assembly must declare exactly one ExpertPackageEntryPoint attribute, " +
                $"but '{assembly.GetName().Name}' declares {attributes.Length}.");
        }

        var abstractType = attributes[0].AbstractExpertType;
        var concreteType = attributes[0].ConcreteExpertType;
        if (!abstractType.IsAbstract)
        {
            throw new InvalidOperationException(
                $"Expert entry point abstract type '{abstractType.FullName}' must be an abstract class.");
        }

        if (!typeof(ExpertBase).IsAssignableFrom(abstractType))
        {
            throw new InvalidOperationException(
                $"Expert entry point abstract type '{abstractType.FullName}' must inherit from '{nameof(ExpertBase)}'.");
        }

        var contractAttribute =
            abstractType.GetCustomAttribute<ExpertContractAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"Expert entry point abstract type '{abstractType.FullName}' does not carry '[ExpertContract]'; " +
                "the entry point cannot be bound to a contract id.");

        if (!abstractType.IsAssignableFrom(concreteType) || concreteType.IsAbstract ||
            concreteType.ContainsGenericParameters)
        {
            throw new InvalidOperationException(
                $"Expert entry point concrete type '{concreteType.FullName}' must be a concrete class inheriting " +
                $"'{abstractType.FullName}'.");
        }

        if (concreteType.Assembly != assembly)
        {
            throw new InvalidOperationException(
                "Expert entry point concrete type must be declared in the Package entry assembly.");
        }

        if (concreteType.GetConstructor(Type.EmptyTypes) is not { IsPublic: true })
        {
            throw new InvalidOperationException(
                $"Expert entry point concrete type '{concreteType.FullName}' requires a public parameterless constructor.");
        }

        return new ExpertPackageShape(contractAttribute.Id, abstractType, concreteType);
    }

    /// <summary>为已通过校验的具体专家类型构造实例工厂（缓存公共无参构造委托）。</summary>
    public static Func<ExpertBase> CreateFactory(ExpertPackageShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var constructor = shape.ConcreteExpertType.GetConstructor(Type.EmptyTypes)!;
        return () => (ExpertBase)constructor.Invoke(null);
    }
}
