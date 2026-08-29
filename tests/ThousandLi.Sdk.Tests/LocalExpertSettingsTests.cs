using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

public sealed class LocalExpertSettingsTests
{
    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    private sealed class SettingsDoc
    {
        [JsonPropertyName("greeting")]
        public string Greeting { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("nested")]
        public NestedDoc Nested { get; init; } = new();
    }

    // Deserialized through System.Text.Json reflection; members are set by the serializer.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class NestedDoc
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("label")]
        public string Label { get; init; } = string.Empty;

        [JsonPropertyName("arr")]
        public int[] Arr { get; init; } = [];
    }

    [Fact]
    public void NullLayersMergeToAnEmptyObject()
    {
        var merged = LocalExpertSettingsResolver.Merge(null, null);
        Assert.Equal(JsonValueKind.Object, merged.ValueKind);
        Assert.False(merged.EnumerateObject().Any());
    }

    [Fact]
    public void UndefinedLayersAreRejected()
    {
        var undefined = default(JsonElement);
        Assert.Throws<ArgumentException>(() => LocalExpertSettingsResolver.Merge(undefined, null));
        Assert.Throws<ArgumentException>(() => LocalExpertSettingsResolver.Merge(null, undefined));
    }

    [Fact]
    public void NonObjectLayersAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => LocalExpertSettingsResolver.Merge(TestSupport.Json("[1]"), null));
        Assert.Throws<ArgumentException>(
            () => LocalExpertSettingsResolver.Merge(null, TestSupport.Json("\"scalar\"")));
    }

    [Fact]
    public void OverridesWinOnScalarConflictsAndNewKeysAreAdded()
    {
        var defaults = TestSupport.Json("""{"a":1,"b":"keep"}""");
        var overrides = TestSupport.Json("""{"a":9,"c":true}""");

        var merged = LocalExpertSettingsResolver.Merge(defaults, overrides);

        using var document = JsonDocument.Parse(merged.GetRawText());
        Assert.Equal(9, document.RootElement.GetProperty("a").GetInt32());
        Assert.Equal("keep", document.RootElement.GetProperty("b").GetString());
        Assert.True(document.RootElement.GetProperty("c").GetBoolean());
    }

    [Fact]
    public void NestedObjectsMergeRecursivelyWhileArraysReplaceWhole()
    {
        var defaults = TestSupport.Json("""{"nested":{"x":1,"y":{"deep":false}},"arr":[1,2,3],"scalar":"old"}""");
        var overrides = TestSupport.Json("""{"nested":{"y":{"deep":true},"z":5},"arr":[9]}""");

        var merged = LocalExpertSettingsResolver.Merge(defaults, overrides);

        using var document = JsonDocument.Parse(merged.GetRawText());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("nested").GetProperty("x").GetInt32());
        Assert.True(root.GetProperty("nested").GetProperty("y").GetProperty("deep").GetBoolean());
        Assert.Equal(5, root.GetProperty("nested").GetProperty("z").GetInt32());
        Assert.Equal([9], [.. root.GetProperty("arr").EnumerateArray().Select(value => value.GetInt32())]);
        Assert.Equal("old", root.GetProperty("scalar").GetString());
    }

    private static async Task<(string Directory, string FilePath)> WriteOverrideFileAsync(string json)
    {
        var directory = Directory.CreateTempSubdirectory("thousandli-settings-");
        var filePath = Path.Combine(directory.FullName, "settings.json");
        await File.WriteAllTextAsync(filePath, json);
        return (directory.FullName, filePath);
    }

    [Fact]
    public async Task ResolveMergesDefaultsWithTheOverrideFile()
    {
        var defaults = TestSupport.Json(
            """{"greeting":"hello","count":1,"nested":{"enabled":true,"label":"base","arr":[1,2]}}""");
        var (directory, filePath) = await WriteOverrideFileAsync(
            """{"count":7,"nested":{"label":"override","extra":true,"arr":[3]}}""");
        try
        {
            var resolved = await LocalExpertSettingsResolver.ResolveAsync(defaults, new FileInfo(filePath), TestSupport.CancellationToken);

            var settings = resolved.Deserialize<SettingsDoc>();
            Assert.NotNull(settings);
            Assert.Equal("hello", settings.Greeting);
            Assert.Equal(7, settings.Count);
            Assert.True(settings.Nested.Enabled);
            Assert.Equal("override", settings.Nested.Label);
            Assert.Equal([3], settings.Nested.Arr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingOverrideFilesYieldTheDefaults()
    {
        var defaults = TestSupport.Json("""{"greeting":"hi"}""");
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var resolved = await LocalExpertSettingsResolver.ResolveAsync(defaults, missing, TestSupport.CancellationToken);

        var settings = resolved.Deserialize<SettingsDoc>();
        Assert.NotNull(settings);
        Assert.Equal("hi", settings.Greeting);
    }

    [Fact]
    public async Task MalformedOverrideFilesFailWithPathAndInnerException()
    {
        var (directory, filePath) = await WriteOverrideFileAsync("""{"count": """);
        try
        {
            var exception = await Assert.ThrowsAsync<LocalExpertSettingsException>(
                () => LocalExpertSettingsResolver.ResolveAsync(null, new FileInfo(filePath), TestSupport.CancellationToken).AsTask());

            Assert.Equal(filePath, exception.FilePath);
            Assert.IsType<JsonException>(exception.InnerException, exactMatch: false);
            Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NonObjectOverrideFilesFail()
    {
        var (directory, filePath) = await WriteOverrideFileAsync("[1,2]");
        try
        {
            var exception = await Assert.ThrowsAsync<LocalExpertSettingsException>(
                () => LocalExpertSettingsResolver.ResolveAsync(null, new FileInfo(filePath), TestSupport.CancellationToken).AsTask());

            Assert.Equal(filePath, exception.FilePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnreadableOverrideFilesFailFast()
    {
        var (directory, filePath) = await WriteOverrideFileAsync("{}");
        try
        {
            await using var locked = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

            var exception = await Assert.ThrowsAsync<LocalExpertSettingsException>(
                () => LocalExpertSettingsResolver.ResolveAsync(null, new FileInfo(filePath), TestSupport.CancellationToken).AsTask());

            Assert.Equal(filePath, exception.FilePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContextResolvesSettingsOnceAndCachesThem()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello","count":1}""");
        var (directory, filePath) = await WriteOverrideFileAsync("""{"count":2}""");
        try
        {
            var context = new LocalExpertExecutionContext(
                new RecordedBasicAi(["m"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious"),
                NullLogger.Instance,
                defaults,
                new FileInfo(filePath));

            var first = await context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken);
            await File.WriteAllTextAsync(filePath, """{"count":99}""", TestSupport.CancellationToken);
            var second = await context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken);

            Assert.Equal(2, first.Count);
            Assert.Equal(2, second.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TypedDeserializationFailuresSurfaceAsSettingsExceptions()
    {
        var defaults = TestSupport.Json("""{"count":"not-a-number"}""");
        var context = new LocalExpertExecutionContext(
            new RecordedBasicAi(["m"], []),
            new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious"),
            NullLogger.Instance,
            defaults);

        var exception = await Assert.ThrowsAsync<LocalExpertSettingsException>(
            async () => await context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken));

        Assert.Contains(nameof(SettingsDoc), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullLiteralOverrideFilesYieldTheDefaults()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello"}""");
        var (directory, filePath) = await WriteOverrideFileAsync("null");
        try
        {
            var resolved = await LocalExpertSettingsResolver.ResolveAsync(
                defaults, new FileInfo(filePath), TestSupport.CancellationToken);

            var settings = resolved.Deserialize<SettingsDoc>();
            Assert.NotNull(settings);
            Assert.Equal("hello", settings.Greeting);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyOverrideObjectsPreserveEveryDefault()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello","nested":{"label":"base"}}""");
        var (directory, filePath) = await WriteOverrideFileAsync("{}");
        try
        {
            var resolved = await LocalExpertSettingsResolver.ResolveAsync(
                defaults, new FileInfo(filePath), TestSupport.CancellationToken);

            using var document = JsonDocument.Parse(resolved.GetRawText());
            Assert.Equal("hello", document.RootElement.GetProperty("greeting").GetString());
            Assert.Equal("base", document.RootElement.GetProperty("nested").GetProperty("label").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OverridesMayReplaceNestedObjectsWithScalars()
    {
        var defaults = TestSupport.Json("""{"nested":{"x":1,"y":2},"top":3}""");
        var overrides = TestSupport.Json("""{"nested":5}""");

        var merged = LocalExpertSettingsResolver.Merge(defaults, overrides);

        using var document = JsonDocument.Parse(merged.GetRawText());
        Assert.Equal(5, document.RootElement.GetProperty("nested").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("top").GetInt32());
    }

    [Fact]
    public async Task ConcurrentFirstResolutionsAgreeAndCacheTheResult()
    {
        var defaults = TestSupport.Json("""{"greeting":"hello","count":1}""");
        var (directory, filePath) = await WriteOverrideFileAsync("""{"count":2}""");
        try
        {
            var context = new LocalExpertExecutionContext(
                new RecordedBasicAi(["m"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious"),
                NullLogger.Instance,
                defaults,
                new FileInfo(filePath));

            var results = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken).AsTask()));

            Assert.All(results, settings => Assert.Equal(2, settings.Count));

            File.Delete(filePath);
            var warm = await context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken);
            Assert.Equal(2, warm.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolutionFailuresAreNotCached()
    {
        var (directory, filePath) = await WriteOverrideFileAsync("""{"count": """);
        try
        {
            var context = new LocalExpertExecutionContext(
                new RecordedBasicAi(["m"], []),
                new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious"),
                NullLogger.Instance,
                TestSupport.Json("""{"count":1}"""),
                new FileInfo(filePath));

            await Assert.ThrowsAsync<LocalExpertSettingsException>(
                () => context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken).AsTask());

            await File.WriteAllTextAsync(filePath, """{"count":9}""", TestSupport.CancellationToken);
            var settings = await context.GetExpertSettingsAsync<SettingsDoc>(TestSupport.CancellationToken);

            Assert.Equal(9, settings.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ContextConstructorRejectsNullCapabilities()
    {
        var profile = new BoundPlayerProfile(new PlayerId("player-1"), "Creator", "curious");
        var logger = NullLogger.Instance;
        Assert.Throws<ArgumentNullException>(() =>
            new LocalExpertExecutionContext(null!, profile, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new LocalExpertExecutionContext(new RecordedBasicAi(["m"], []), null!, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new LocalExpertExecutionContext(new RecordedBasicAi(["m"], []), profile, null!));
    }
}
