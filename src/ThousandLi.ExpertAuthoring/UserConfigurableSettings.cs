using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.ExpertAuthoring;

/// <summary>Marks an expert settings type whose members are user-configurable.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UserConfigurableSettingsAttribute : Attribute;

/// <summary>Marks a user-configurable member of a settings type and carries its display copy.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UserConfigurableSettingAttribute(
    string title,
    string shortDescription,
    string longDescription) : Attribute
{
    public string Title { get; } = title;

    public string ShortDescription { get; } = shortDescription;

    public string LongDescription { get; } = longDescription;
}

/// <summary>Describes one enum value for frontend dropdown rendering.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record EnumValueDescriptor(int Value, string DisplayName, string? Description);

/// <summary>The structured description of a single configurable settings member.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record UserConfigurableSettingDescriptor(
    string MemberName,
    Type ValueType,
    string Title,
    string ShortDescription,
    string LongDescription,
    IReadOnlyList<EnumValueDescriptor>? EnumValues = null);

/// <summary>A settings schema together with its default value snapshot.</summary>
public sealed record UserConfigurableSettingsSchema(
    Type SettingsType,
    JsonElement DefaultValue,
    IReadOnlyList<UserConfigurableSettingDescriptor> Members)
{
    public object CreateTypedInstance(JsonElement snapshot)
    {
        if (snapshot.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Expert settings snapshot cannot be undefined.", nameof(snapshot));

        var json = snapshot.Clone().GetRawText();
        return JsonSerializer.Deserialize(json, SettingsType, ExpertSettingsJson.Options)
               ?? throw new InvalidOperationException(
                   $"Expert settings for '{SettingsType.FullName}' could not be materialized.");
    }

    [UsedImplicitly(ImplicitUseKindFlags.Access)]
    public object CreateDefaultInstance()
    {
        return CreateTypedInstance(DefaultValue);
    }
}

internal static class ExpertSettingsJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static JsonElement Serialize(object value, Type type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        return JsonSerializer.SerializeToElement(value, type, Options).Clone();
    }
}

internal static class SettingsContractHelper
{
    private static readonly HashSet<Type> s_numericTypes =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
        typeof(ulong), typeof(float), typeof(double), typeof(decimal),
    ];

    internal static bool IsSupportedMemberType(Type type)
    {
        return type == typeof(bool) ||
               type == typeof(string) ||
               type.IsEnum ||
               s_numericTypes.Contains(type);
    }

    internal static EnumValueDescriptor[]? BuildEnumValues(Type type)
    {
        if (!type.IsEnum) return null;

        return [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field =>
            {
                var value = Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture);
                var display = field.GetCustomAttribute<DisplayAttribute>(inherit: false);
                var displayName = display?.GetName() ?? field.Name;
                var description = display?.GetDescription();
                return new EnumValueDescriptor(value, displayName, description);
            })
            .OrderBy(v => v.Value, Comparer<int>.Default)];
    }
}

/// <summary>
/// Discovers the optional <see cref="UserConfigurableSettingsAttribute"/> settings type of an
/// expert package assembly. At most one settings type may be declared; member declarations on
/// unmarked types are rejected.
/// </summary>
public static class UserConfigurableSettingsContract
{
    public static UserConfigurableSettingsSchema? Discover(Assembly assembly, Func<string, Exception> errorFactory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(errorFactory);

        var markedTypes = assembly.GetTypes()
            .Where(type => Attribute.IsDefined(type, typeof(UserConfigurableSettingsAttribute), inherit: false))
            .ToArray();
        if (markedTypes.Length > 1)
        {
            throw errorFactory(
                $"Package Assembly '{assembly.GetName().Name}' declares more than one user-configurable settings type.");
        }

        foreach (var type in assembly.GetTypes())
        {
            var attributedProperties = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property => Attribute.IsDefined(property, typeof(UserConfigurableSettingAttribute), inherit: false))
                .ToArray();
            if (attributedProperties.Length > 0 && markedTypes.All(markedType => markedType != type))
            {
                throw errorFactory(
                    $"Settings property declarations on '{type.FullName}' require [UserConfigurableSettings].");
            }
        }

        return markedTypes.Length == 0 ? null : BuildSchema(markedTypes[0], errorFactory);
    }

    private static UserConfigurableSettingsSchema BuildSchema(Type settingsType, Func<string, Exception> errorFactory)
    {
        if (settingsType.IsAbstract)
            throw errorFactory($"Settings type '{settingsType.FullName}' must be default-instantiable.");

        var constructor = settingsType.GetConstructor(Type.EmptyTypes);
        if (constructor is null || !constructor.IsPublic)
        {
            throw errorFactory(
                $"Settings type '{settingsType.FullName}' must expose a public parameterless constructor.");
        }

        var nullability = new NullabilityInfoContext();
        var properties = settingsType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => Attribute.IsDefined(property, typeof(UserConfigurableSettingAttribute), inherit: false))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var members = new List<UserConfigurableSettingDescriptor>(properties.Length);
        var instance = constructor.Invoke(parameters: null);

        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttribute<UserConfigurableSettingAttribute>(inherit: false)!;
            if (string.IsNullOrWhiteSpace(attribute.Title))
                throw new ArgumentException($"Settings attribute Title on '{settingsType.FullName}.{property.Name}' is null or whitespace.", nameof(settingsType));
            if (string.IsNullOrWhiteSpace(attribute.ShortDescription))
                throw new ArgumentException($"Settings attribute ShortDescription on '{settingsType.FullName}.{property.Name}' is null or whitespace.", nameof(settingsType));
            if (string.IsNullOrWhiteSpace(attribute.LongDescription))
                throw new ArgumentException($"Settings attribute LongDescription on '{settingsType.FullName}.{property.Name}' is null or whitespace.", nameof(settingsType));

            if (property.GetIndexParameters().Length != 0)
            {
                throw errorFactory(
                    $"Settings property '{settingsType.FullName}.{property.Name}' cannot be an indexer.");
            }

            if (property.GetMethod is null || !property.GetMethod.IsPublic || property.SetMethod is null ||
                !property.SetMethod.IsPublic)
            {
                throw errorFactory(
                    $"Settings property '{settingsType.FullName}.{property.Name}' must be public, readable, and writable.");
            }

            if (!SettingsContractHelper.IsSupportedMemberType(property.PropertyType))
            {
                throw errorFactory(
                    $"Settings property '{settingsType.FullName}.{property.Name}' uses unsupported type '{property.PropertyType.FullName}'.");
            }

            if (property.PropertyType == typeof(string) &&
                (nullability.Create(property).ReadState == NullabilityState.Nullable ||
                 nullability.Create(property).WriteState == NullabilityState.Nullable))
            {
                throw errorFactory(
                    $"Settings property '{settingsType.FullName}.{property.Name}' must be non-null by contract.");
            }

            _ = property.GetValue(instance) ?? throw errorFactory(
                    $"Settings property '{settingsType.FullName}.{property.Name}' default value must be non-null.");
            members.Add(new UserConfigurableSettingDescriptor(
                property.Name,
                property.PropertyType,
                attribute.Title,
                attribute.ShortDescription,
                attribute.LongDescription,
                SettingsContractHelper.BuildEnumValues(property.PropertyType)));
        }

        return new UserConfigurableSettingsSchema(
            settingsType,
            ExpertSettingsJson.Serialize(instance, settingsType),
            members);
    }
}
