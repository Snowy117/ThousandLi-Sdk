using System.Reflection;
using Castle.DynamicProxy;

namespace ThousandLi.Contracts;

internal interface ISessionStateProxyFactory
{
    // ReSharper disable once UnusedMemberInSuper.Global -- kept on the seam used by tests and future generated proxy factories.
    object CreateRoot(SessionStateContract contract, object root, GameStateSessionStateTracker tracker);

    object WrapValue(Type declaredType, object? value, string path, SessionStateContract contract,
        ISessionStateChangeSink sink);
}

internal sealed class SessionStateProxyFactory : ISessionStateProxyFactory
{
    private static readonly ProxyGenerator ProxyGeneratorInstance = new();

    public static SessionStateProxyFactory Default { get; } = new();
    // TODO: 后续可考虑用 source generator 生成更高效率的 SessionState tracker/proxy 代码。

    public object CreateRoot(SessionStateContract contract, object root, GameStateSessionStateTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tracker);
        return WrapValue(contract.RootType, root, string.Empty, contract, tracker);
    }

    public object WrapValue(Type declaredType, object? value, string path, SessionStateContract contract,
        ISessionStateChangeSink sink)
    {
        ArgumentNullException.ThrowIfNull(declaredType);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(sink);
        return value switch
        {
            null => null!,
            ITrackedCollection collection => AttachCollection(collection, path, sink),
            _ when !contract.IsStateObjectType(declaredType) || ProxyUtil.IsProxy(value) => value,
            _ => CreateProxy(declaredType, value, path, contract, sink)
        };
    }

    private static object AttachCollection(ITrackedCollection collection, string path, ISessionStateChangeSink sink)
    {
        collection.Attach((ISessionStateCollectionTracker)sink, path);
        return collection;
    }

    private object CreateProxy(Type declaredType, object value, string path, SessionStateContract contract,
        ISessionStateChangeSink sink)
    {
        var interceptor = new SessionStateSetterInterceptor(path, contract, sink, this)
        {
            SuppressTracking = true
        };
        var proxy = ProxyGeneratorInstance.CreateClassProxy(declaredType, interceptor);
        CopyContractProperties(declaredType, value, proxy, contract, path, sink);
        interceptor.SuppressTracking = false;
        return proxy;
    }

    private void CopyContractProperties(Type type, object source, object target, SessionStateContract contract,
        string parentPath, ISessionStateChangeSink sink)
    {
        foreach (var member in contract.GetMembers(type))
        {
            var value = member.Property.GetValue(source);
            var childPath = Join(parentPath, member.JsonName);
            var wrapped = WrapValue(member.Property.PropertyType, value, childPath, contract, sink);
            member.Property.SetValue(target, wrapped);
        }
    }

    private static string Join(string parent, string segment)
    {
        return string.IsNullOrEmpty(parent) ? $"/{segment}" : $"{parent}/{segment}";
    }
}

internal sealed class SessionStateSetterInterceptor(
    string objectPath,
    SessionStateContract contract,
    ISessionStateChangeSink sink,
    ISessionStateProxyFactory proxyFactory) : IInterceptor
{
    public bool SuppressTracking { get; set; }

    public void Intercept(IInvocation invocation)
    {
        if (!TryGetTrackedMember(invocation, out var property, out var member))
        {
            invocation.Proceed();
            return;
        }

        var path = Join(objectPath, member.JsonName);
        var value = proxyFactory.WrapValue(property.PropertyType, invocation.Arguments[0], path, contract, sink);
        invocation.Arguments[0] = value;
        invocation.Proceed();
        if (SuppressTracking) return;

        sink.Replace(path, value);
    }

    private bool TryGetTrackedMember(IInvocation invocation, out PropertyInfo property,
        out SessionStateMemberNode member)
    {
        member = null!;
        return TryGetSetterProperty(invocation.Method, out property) &&
               contract.TryGetMember(invocation.TargetType, property.Name, out member);
    }

    private static bool TryGetSetterProperty(MethodInfo method, out PropertyInfo property)
    {
        property = null!;
        if (!method.IsSpecialName || !method.Name.StartsWith("set_", StringComparison.Ordinal)) return false;

        var name = method.Name[4..];
        var propertyInfo = method.DeclaringType?.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (propertyInfo is null) return false;

        property = propertyInfo;
        return true;
    }

    private static string Join(string parent, string segment)
    {
        return string.IsNullOrEmpty(parent) ? $"/{segment}" : $"{parent}/{segment}";
    }
}
