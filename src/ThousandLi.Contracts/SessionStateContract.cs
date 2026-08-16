using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>SessionState 单个成员的描述符。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record SessionStateMemberDescriptor(
    string Name,
    string JsonName,
    Type ValueType,
    bool AiFacing,
    int? Order,
    string? Description,
    string? UpdateRule,
    int? Min,
    int? Max);

/// <summary>SessionState 对象类型的描述符（根或嵌套对象）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record SessionStateTypeDescriptor(
    Type Type,
    IReadOnlyList<SessionStateMemberDescriptor> Members);

/// <summary>
///     强类型 SessionState 的反射合同：从根类型属性图生成成员节点，
///     供序列化、代理写回与（第二期）AI 投影消费。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SessionStateContract
{
    private readonly Dictionary<Type, SessionStateTypeNode> _nodes;

    private SessionStateContract(Type rootType, Dictionary<Type, SessionStateTypeNode> nodes)
    {
        RootType = rootType;
        _nodes = nodes;
        Types = [.. nodes.Values
            .Select(node => new SessionStateTypeDescriptor(node.Type,
                [.. node.Members.Select(member => member.ToDescriptor())]))];
    }

    /// <summary>合同根类型。</summary>
    public Type RootType { get; }

    /// <summary>合同覆盖的所有对象类型描述符。</summary>
    public IReadOnlyList<SessionStateTypeDescriptor> Types { get; }

    /// <summary>从根类型反射创建合同，并校验根类型标注与成员规则。</summary>
    public static SessionStateContract Create(Type rootType, Func<string, Exception>? errorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        var factory = errorFactory ?? (message => new SessionStateContractException(message));
        if (!Attribute.IsDefined(rootType, typeof(SessionStateRootAttribute), inherit: false))
            throw factory($"SessionState root '{rootType.FullName}' must declare SessionStateRootAttribute.");
        var builder = new Builder(factory);
        builder.VisitObject(rootType, []);
        return new SessionStateContract(rootType, builder.Nodes);
    }

    /// <summary>
    ///     创建根类型的初始实例：优先使用接受 <see cref="BoundPlayerProfile" /> 的公开构造器，
    ///     否则使用公开无参构造器。
    /// </summary>
    public object CreateInitialInstance(BoundPlayerProfile playerProfile)
    {
        ArgumentNullException.ThrowIfNull(playerProfile);
        var profileConstructor = RootType.GetConstructor([typeof(BoundPlayerProfile)]);
        if (profileConstructor is not null) return profileConstructor.Invoke([playerProfile]);

        var defaultConstructor = RootType.GetConstructor(Type.EmptyTypes)
                                 ?? throw new SessionStateContractException(
                                     $"SessionState root '{RootType.FullName}' must expose a public parameterless constructor or a public constructor accepting BoundPlayerProfile.");
        return defaultConstructor.Invoke(parameters: null);
    }

    internal IReadOnlyList<SessionStateMemberNode> GetMembers(Type type)
    {
        return _nodes.TryGetValue(ResolveKnownType(type), out var node)
            ? node.Members
            : throw new SessionStateContractException(
                $"Type '{type.FullName}' is not part of SessionState contract.");
    }

    internal bool IsStateObjectType(Type type)
    {
        return _nodes.ContainsKey(ResolveKnownType(type));
    }

    internal bool TryGetMember(Type type, string propertyName, out SessionStateMemberNode member)
    {
        var lookupType = type.BaseType is not null && _nodes.ContainsKey(type.BaseType) ? type.BaseType : type;
        if (_nodes.TryGetValue(NormalizeType(lookupType), out var node))
        {
            foreach (var candidate in node.Members)
            {
                if (!string.Equals(candidate.Property.Name, propertyName, StringComparison.Ordinal))
                    continue;

                member = candidate;
                return true;
            }
        }

        member = null!;
        return false;
    }

    private static Type NormalizeType(Type type)
    {
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    private Type ResolveKnownType(Type type)
    {
        type = NormalizeType(type);
        if (_nodes.ContainsKey(type)) return type;
        if (type.BaseType is { } baseType && _nodes.ContainsKey(baseType)) return baseType;
        return type;
    }

    private sealed class Builder(Func<string, Exception> errorFactory)
    {
        public Dictionary<Type, SessionStateTypeNode> Nodes { get; } = [];

        public void VisitObject(Type type, Stack<Type> stack)
        {
            type = NormalizeType(type);
            if (Nodes.ContainsKey(type)) return;
            if (stack.Contains(type))
                throw errorFactory($"SessionState type graph contains cycle at '{type.FullName}'.");
            if (type.IsSealed) throw errorFactory($"SessionState type '{type.FullName}' must not be sealed.");
            if (!type.IsClass)
                throw errorFactory($"SessionState type '{type.FullName}' must be a class or record class.");

            stack.Push(type);
            // 扫描非公开属性以发现 [SessionStateMember]/[AiStateMember]，属刻意的可访问性绕过（发现后由 ValidateProperty 拒绝非公开访问器）。
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property => Attribute.IsDefined(property, typeof(SessionStateMemberAttribute), inherit: false) ||
                                   Attribute.IsDefined(property, typeof(AiStateMemberAttribute), inherit: false))
                .ToArray();
            var orderSet = new HashSet<int>();
            var members = new List<SessionStateMemberNode>(properties.Length);
            foreach (var property in properties)
            {
                var sessionAttribute = property.GetCustomAttribute<SessionStateMemberAttribute>(inherit: false);
                var aiAttribute = property.GetCustomAttribute<AiStateMemberAttribute>(inherit: false);
                if (sessionAttribute is not null && aiAttribute is not null)
                {
                    throw errorFactory(
                        $"SessionState member '{type.FullName}.{property.Name}' cannot declare both SessionStateMemberAttribute and AiStateMemberAttribute; AiStateMember already implies persistence.");
                }

                ValidateProperty(type, property, aiAttribute, orderSet);
                VisitReachableType(property.PropertyType, stack);
                members.Add(new SessionStateMemberNode(property, ToJsonName(property.Name), aiAttribute is not null,
                    aiAttribute?.Order, aiAttribute?.Description, aiAttribute?.UpdateRule,
                    ParseBound(aiAttribute?.Min, property, "Min"), ParseBound(aiAttribute?.Max, property, "Max")));
            }

            Nodes[type] = new SessionStateTypeNode(type,
                [.. members.OrderBy(member => member.AiFacing ? member.Order : int.MaxValue).ThenBy(member => member.JsonName, StringComparer.Ordinal)]
            );
            stack.Pop();
        }

        private void VisitReachableType(Type type, Stack<Type> stack)
        {
            while (true)
            {
                type = NormalizeType(type);
                if (IsLeaf(type)) return;
                if (type.IsArray || IsUnsupportedCollectionType(type) || !type.IsClass)
                    throw errorFactory($"SessionState member type '{type.FullName}' is not supported.");
                if (!type.IsGenericType) break;

                var genericType = type.GetGenericTypeDefinition();
                if (genericType != typeof(TrackedList<>) && genericType != typeof(TrackedDictionary<>)) break;
                type = type.GetGenericArguments()[0];
            }

            if (type.IsInterface || type == typeof(object))
                throw errorFactory($"SessionState member type '{type.FullName}' is not supported.");
            VisitObject(type, stack);
        }

        private void ValidateProperty(Type declaringType, PropertyInfo property, AiStateMemberAttribute? attribute,
            HashSet<int> orderSet)
        {
            if (attribute is not null)
            {
                if (!orderSet.Add(attribute.Order))
                {
                    throw errorFactory(
                        string.Create(CultureInfo.InvariantCulture, $"SessionState type '{declaringType.FullName}' declares duplicate AI-facing member order '{attribute.Order}'."));
                }

                if (string.IsNullOrWhiteSpace(attribute.Description))
                {
                    throw errorFactory(
                        $"AiState member '{declaringType.FullName}.{property.Name}' requires Description.");
                }
            }

            if (property.GetIndexParameters().Length != 0)
            {
                throw errorFactory(
                    $"SessionState member '{declaringType.FullName}.{property.Name}' cannot be an indexer.");
            }

            if (property.GetMethod is null || !property.GetMethod.IsPublic || property.SetMethod?.IsPublic != true ||
                !property.GetMethod.IsVirtual || !property.SetMethod.IsVirtual ||
                property.GetMethod.IsFinal || property.SetMethod.IsFinal)
            {
                throw errorFactory(
                    $"SessionState member '{declaringType.FullName}.{property.Name}' must have public virtual get and set accessors.");
            }

            if (attribute is not null && !IsNumeric(NormalizeType(property.PropertyType)) &&
                (attribute.Min is not null || attribute.Max is not null))
            {
                throw errorFactory(
                    $"AiState member '{declaringType.FullName}.{property.Name}' cannot declare Min/Max on a non-numeric type.");
            }

            if (ParseBound(attribute?.Min, property, "Min") is { } min &&
                ParseBound(attribute?.Max, property, "Max") is { } max && min > max)
            {
                throw errorFactory(
                    $"AiState member '{declaringType.FullName}.{property.Name}' declares Min greater than Max.");
            }
        }

        private int? ParseBound(string? value, PropertyInfo property, string name)
        {
            if (value is null) return null;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw errorFactory(
                    $"AiState member '{property.DeclaringType?.FullName}.{property.Name}' has invalid {name} value '{value}'.");
        }

        private static bool IsLeaf(Type type)
        {
            return type == typeof(string) || type == typeof(char) || type == typeof(bool) || type.IsEnum ||
                   IsNumeric(type);
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);
        }

        private static bool IsUnsupportedCollectionType(Type type)
        {
            if (type == typeof(string)) return false;
            if (!type.IsGenericType) return typeof(IEnumerable).IsAssignableFrom(type);

            var genericType = type.GetGenericTypeDefinition();
            if (genericType == typeof(TrackedList<>) || genericType == typeof(TrackedDictionary<>)) return false;

            return typeof(IEnumerable).IsAssignableFrom(type);
        }

        private static string ToJsonName(string propertyName)
        {
            return JsonNamingPolicy.CamelCase.ConvertName(propertyName);
        }
    }
}

internal sealed record SessionStateTypeNode(Type Type, IReadOnlyList<SessionStateMemberNode> Members);

internal sealed record SessionStateMemberNode(
    PropertyInfo Property,
    string JsonName,
    bool AiFacing,
    int? Order,
    string? Description,
    string? UpdateRule,
    int? Min,
    int? Max)
{
    public SessionStateMemberDescriptor ToDescriptor()
    {
        return new SessionStateMemberDescriptor(Property.Name, JsonName, Property.PropertyType, AiFacing, Order,
            Description, UpdateRule, Min, Max);
    }
}
