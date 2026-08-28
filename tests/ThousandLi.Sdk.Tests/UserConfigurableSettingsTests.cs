using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;
using ThousandLi.UserConfigurableSettingsFixture;

namespace ThousandLi.Sdk.Tests;

public sealed class UserConfigurableSettingsTests
{
    private static readonly Assembly s_testAssembly = typeof(UserConfigurableSettingsTests).Assembly;

    private static Exception ErrorFactory(string message) => new InvalidOperationException(message);


    [Fact]
    public void Discover_NullArguments_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => UserConfigurableSettingsContract.Discover(null!, ErrorFactory));
        Assert.Throws<ArgumentNullException>(
            () => UserConfigurableSettingsContract.Discover(s_testAssembly, null!));
    }

    [Fact]
    public void Discover_UnmarkedAssembly_ReturnsNull()
    {
        var schema = UserConfigurableSettingsContract.Discover(typeof(BoundPlayerProfile).Assembly, ErrorFactory);

        Assert.Null(schema);
    }

    [Fact]
    public void Discover_FixtureAssembly_BuildsSchemaForSingleMarkedType()
    {
        var schema = UserConfigurableSettingsContract.Discover(
            typeof(FixtureSettings).Assembly, ErrorFactory);

        Assert.NotNull(schema);
        Assert.Equal(typeof(FixtureSettings), schema!.SettingsType);
        Assert.Equal(JsonValueKind.Object, schema.DefaultValue.ValueKind);
        Assert.Equal(6, schema.Members.Count);
    }

    [Fact]
    public void Discover_TestAssembly_ThrowsOnOrphanedMemberDeclarations()
    {
        // This test assembly deliberately contains zero [UserConfigurableSettings] types and the
        // OrphanedMembers helper below with attributed properties, so Discover must reject it.
        var exception = Assert.Throws<InvalidOperationException>(
            () => UserConfigurableSettingsContract.Discover(s_testAssembly, ErrorFactory));

        Assert.Contains("require [UserConfigurableSettings]", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void BuildSchema_Members_AreOrderedByNameOrdinal()
    {
        var schema = BuildSchemaForType<MixedSettings>();

        Assert.Equal(
        [
            "Enabled", "Enabled2", "Name", "Ratio", "Style", "Tone", "Verbosity",
        ], [.. schema.Members.Select(member => member.MemberName)]);
    }

    [Fact]
    public void BuildSchema_AllSupportedTypes_AreAccepted()
    {
        var schema = BuildSchemaForType<MixedSettings>();

        Assert.Equal(7, schema.Members.Count);
        Assert.Equal(typeof(bool), schema.Members[0].ValueType);
        Assert.Equal(typeof(byte), schema.Members[1].ValueType);
        Assert.Equal(typeof(string), schema.Members[2].ValueType);
        Assert.Equal(typeof(double), schema.Members[3].ValueType);
        Assert.Equal(typeof(string), schema.Members[4].ValueType);
        Assert.Equal(typeof(AnnotatedTone), schema.Members[5].ValueType);
        Assert.Equal(typeof(int), schema.Members[6].ValueType);
        Assert.All(schema.Members.Where(member => member.ValueType != typeof(AnnotatedTone)),
            member => Assert.Null(member.EnumValues));
    }

    [Fact]
    public void BuildSchema_EnumWithDisplay_UsesDisplayNamesAndOrdersByValue()
    {
        var schema = BuildSchemaForType<MixedSettings>();
        var tone = schema.Members.Single(member => member.MemberName == "Tone");

        Assert.NotNull(tone.EnumValues);
        Assert.Equal(3, tone.EnumValues!.Count);
        Assert.Equal(0, tone.EnumValues[0].Value);
        Assert.Equal("Beginner", tone.EnumValues[0].DisplayName);
        Assert.Equal("For new players", tone.EnumValues[0].Description);
        Assert.Equal(1, tone.EnumValues[1].Value);
        Assert.Equal("Standard", tone.EnumValues[1].DisplayName);
        Assert.Equal("Normal challenge", tone.EnumValues[1].Description);
        Assert.Equal(2, tone.EnumValues[2].Value);
        Assert.Equal("Expert", tone.EnumValues[2].DisplayName);
        Assert.Equal("Maximum challenge", tone.EnumValues[2].Description);
    }

    [Fact]
    public void BuildSchema_EnumWithoutDisplay_UsesFieldNames()
    {
        var schema = BuildSchemaForType<PlainEnumSettings>();
        var member = Assert.Single(schema.Members);

        Assert.NotNull(member.EnumValues);
        Assert.Equal(["Easy", "Normal", "Hard"], member.EnumValues!.Select(value => value.DisplayName));
        Assert.All(member.EnumValues, value => Assert.Null(value.Description));
    }

    [Fact]
    public void BuildSchema_DefaultValue_SerializesInstanceDefaults()
    {
        var schema = BuildSchemaForType<MixedSettings>();
        var defaults = Assert.IsType<MixedSettings>(schema.CreateDefaultInstance());

        Assert.True(defaults.Enabled);
        Assert.Equal((byte)7, defaults.Enabled2);
        Assert.Equal("neutral", defaults.Name);
        Assert.Equal(0.5, defaults.Ratio);
        Assert.Equal("balanced", defaults.Style);
        Assert.Equal(AnnotatedTone.Normal, defaults.Tone);
        Assert.Equal(3, defaults.Verbosity);
    }

    [Fact]
    public void BuildSchema_AbstractType_Throws()
    {
        var exception = InvokeBuildSchemaForException<AbstractSettings>();

        Assert.Contains("must be default-instantiable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_NoPublicParameterlessConstructor_Throws()
    {
        var exception = InvokeBuildSchemaForException<NoConstructorSettings>();

        Assert.Contains("public parameterless constructor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_Indexer_Throws()
    {
        var exception = InvokeBuildSchemaForException<IndexerSettings>();

        Assert.Contains("cannot be an indexer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_PrivateGetter_Throws()
    {
        var exception = InvokeBuildSchemaForException<PrivateGetterSettings>();

        Assert.Contains("must be public, readable, and writable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_PrivateSetter_Throws()
    {
        var exception = InvokeBuildSchemaForException<PrivateSetterSettings>();

        Assert.Contains("must be public, readable, and writable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_UnsupportedDateTimeType_Throws()
    {
        var exception = InvokeBuildSchemaForException<DateTimeSettings>();

        Assert.Contains("unsupported type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_UnsupportedListType_Throws()
    {
        var exception = InvokeBuildSchemaForException<ListSettings>();

        Assert.Contains("unsupported type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_NullableString_Throws()
    {
        var exception = InvokeBuildSchemaForException<NullableStringSettings>();

        Assert.Contains("must be non-null by contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_NullDefaultString_Throws()
    {
        var exception = InvokeBuildSchemaForException<NullDefaultSettings>();

        Assert.Contains("default value must be non-null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSchema_BlankAttributeTitle_ThrowsArgumentException()
    {
        // Blank attribute copy is a declaration bug, so it fails directly instead of through the factory.
        var exception = InvokeBuildSchemaForException<BlankTitleSettings>();

        Assert.IsType<ArgumentException>(exception);
        Assert.Contains("Title", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void CreateTypedInstance_ReturnsPopulatedObject()
    {
        var schema = BuildDummySchema(typeof(DummySettings));
        var json = JsonSerializer.SerializeToElement(new { value = 42 });

        var instance = Assert.IsType<DummySettings>(schema.CreateTypedInstance(json));

        Assert.Equal(42, instance.Value);
    }

    [Fact]
    public void CreateTypedInstance_UndefinedJson_Throws()
    {
        var schema = BuildDummySchema(typeof(DummySettings));

        var exception = Assert.Throws<ArgumentException>(() => schema.CreateTypedInstance(default));

        Assert.Contains("cannot be undefined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDefaultInstance_UsesDefaultValue()
    {
        var schema = BuildDummySchema(typeof(DummySettings));

        var instance = Assert.IsType<DummySettings>(schema.CreateDefaultInstance());

        Assert.Equal(100, instance.Value);
    }


    [Fact]
    public void UserConfigurableSettingDescriptor_StoresAllFields()
    {
        var enumValues = new List<EnumValueDescriptor>
        {
            new(0, "Zero", null),
            new(1, "One", "The first value"),
        };
        var descriptor = new UserConfigurableSettingDescriptor(
            "Prop", typeof(string), "Title", "Short", "Long", enumValues);

        Assert.Equal("Prop", descriptor.MemberName);
        Assert.Equal(typeof(string), descriptor.ValueType);
        Assert.Equal("Title", descriptor.Title);
        Assert.Equal("Short", descriptor.ShortDescription);
        Assert.Equal("Long", descriptor.LongDescription);
        Assert.Same(enumValues, descriptor.EnumValues);
    }

    [Fact]
    public void EnumValueDescriptor_StoresAllFields()
    {
        var descriptor = new EnumValueDescriptor(5, "Five", "The fifth element");

        Assert.Equal(5, descriptor.Value);
        Assert.Equal("Five", descriptor.DisplayName);
        Assert.Equal("The fifth element", descriptor.Description);
    }

    [Fact]
    public void UserConfigurableSettingAttribute_StoresConstructorArguments()
    {
        var attribute = typeof(FixtureSettings)
            .GetProperty(nameof(FixtureSettings.Style))!
            .GetCustomAttribute<UserConfigurableSettingAttribute>(inherit: false)!;

        Assert.Equal("Style", attribute.Title);
        Assert.Equal("Narrative style", attribute.ShortDescription);
        Assert.Equal("Controls the narrative style of generated text.", attribute.LongDescription);
    }

    [Fact]
    public void UserConfigurableSettingsAttribute_MarksTheFixtureType()
    {
        var attribute = typeof(FixtureSettings).GetCustomAttribute<UserConfigurableSettingsAttribute>(inherit: false);

        Assert.NotNull(attribute);
    }


    private static UserConfigurableSettingsSchema BuildSchemaForType<T>() =>
        (UserConfigurableSettingsSchema)GetBuildSchema().Invoke(null,
        [
            typeof(T),
            (Func<string, Exception>)(message => new InvalidOperationException(message)),
        ])!;

    private static Exception InvokeBuildSchemaForException<T>()
    {
        try
        {
            GetBuildSchema().Invoke(null,
            [
                typeof(T),
                (Func<string, Exception>)(message => new InvalidOperationException(message)),
            ]);
        }
        catch (TargetInvocationException invocationException)
        {
            return invocationException.InnerException ?? invocationException;
        }

        throw new InvalidOperationException("BuildSchema was expected to throw.");
    }

    private static MethodInfo GetBuildSchema() =>
        typeof(UserConfigurableSettingsContract)
            .GetMethod("BuildSchema", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("UserConfigurableSettingsContract.BuildSchema was not found.");

    private static UserConfigurableSettingsSchema BuildDummySchema(Type settingsType) =>
        new(
            settingsType,
            JsonSerializer.SerializeToElement(new { value = 100 }),
            [new UserConfigurableSettingDescriptor("Value", typeof(int), "Val", "Short", "Long")]);

    public enum AnnotatedTone
    {
        [Display(Name = "Beginner", Description = "For new players")]
        Easy = 0,

        [Display(Name = "Standard", Description = "Normal challenge")]
        Normal = 1,

        [Display(Name = "Expert", Description = "Maximum challenge")]
        Hard = 2,
    }

    public enum PlainTone
    {
        Easy = 0,
        Normal = 1,
        Hard = 2,
    }

    private sealed class DummySettings
    {
        public int Value { get; set; }
    }

    private sealed class OrphanedMembers
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int Value { get; set; }
    }

    private abstract class AbstractSettings
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int Value { get; set; }
    }

    private sealed class NoConstructorSettings(int value)
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int Value { get; } = value;
    }

    private sealed class IndexerSettings
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int this[int index]
        {
            get => 0;
            set { _ = value; }
        }
    }

    private sealed class PrivateGetterSettings
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int X { private get; set; }
    }

    private sealed class PrivateSetterSettings
    {
        [UserConfigurableSetting("X", "X short", "X long")]
        public int X { get; private set; }
    }

    private sealed class DateTimeSettings
    {
        [UserConfigurableSetting("Timestamp", "Timestamp", "A datetime value.")]
        public DateTime Timestamp { get; set; }
    }

    private sealed class ListSettings
    {
        [UserConfigurableSetting("Items", "Items", "A list of items.")]
        public List<string> Items { get; set; } = [];
    }

    private sealed class NullableStringSettings
    {
        [UserConfigurableSetting("Name", "Player name", "The player's display name.")]
        public string? Name { get; set; } = "Default";
    }

    private sealed class NullDefaultSettings
    {
        [UserConfigurableSetting("Name", "Player name", "The player's display name.")]
        public string Name { get; set; } = null!;
    }

    private sealed class BlankTitleSettings
    {
        [UserConfigurableSetting("", "X short", "X long")]
        public int Value { get; set; }
    }

    private sealed class MixedSettings
    {
        [UserConfigurableSetting("Verbosity", "Narrative verbosity", "Controls how verbose generated text is.")]
        public int Verbosity { get; set; } = 3;

        [UserConfigurableSetting("Tone", "Narrative tone", "Selects the narrative tone.")]
        public AnnotatedTone Tone { get; set; } = AnnotatedTone.Normal;

        [UserConfigurableSetting("Style", "Narrative style", "Controls the narrative style of generated text.")]
        public string Style { get; set; } = "balanced";

        [UserConfigurableSetting("Ratio", "Sampling ratio", "Controls the sampling ratio.")]
        public double Ratio { get; set; } = 0.5;

        [UserConfigurableSetting("Name", "Player name", "The player's display name.")]
        public string Name { get; set; } = "neutral";

        [UserConfigurableSetting("Enabled2", "Byte toggle", "A byte value.")]
        public byte Enabled2 { get; set; } = 7;

        [UserConfigurableSetting("Enabled", "Feature toggle", "Whether the feature is enabled.")]
        public bool Enabled { get; set; } = true;
    }

    private sealed class PlainEnumSettings
    {
        [UserConfigurableSetting("Tone", "Narrative tone", "Selects the narrative tone.")]
        public PlainTone Tone { get; set; } = PlainTone.Normal;
    }
}
