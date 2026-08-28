using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.Sdk.Tests;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local — 设置 fixture 的 init 访问器由 JSON 反序列化反射调用

public sealed class ExpertExecutionContextTests
{
    [Fact]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        var profile = new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona");

        Assert.Throws<ArgumentNullException>(
            () => new LocalExpertExecutionContext(null!, profile, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new LocalExpertExecutionContext(new StubBasicAi(), null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new LocalExpertExecutionContext(new StubBasicAi(), profile, null!));
    }

    [Fact]
    public void FourCapabilities_AreTheComposedInstances()
    {
        var basicAi = new StubBasicAi();
        var profile = new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona");
        var context = new LocalExpertExecutionContext(basicAi, profile, NullLogger.Instance);

        Assert.Same(basicAi, context.BasicAi);
        Assert.Same(profile, context.PlayerProfile);
        Assert.Same(NullLogger.Instance, context.Logger);
    }

    [Fact]
    public async Task GetExpertSettingsAsync_WithoutLayers_YieldsTypeDefaults()
    {
        var context = CreateContext();

        var settings = await context.GetExpertSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

        Assert.Equal("balanced", settings.Style);
        Assert.Equal(0.4, settings.Temperature);
    }

    [Fact]
    public async Task GetExpertSettingsAsync_AppliesSchemaDefaults()
    {
        var context = CreateContext(TestSupport.Json("""{"style":"concise"}"""));

        var settings = await context.GetExpertSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

        Assert.Equal("concise", settings.Style);
        Assert.Equal(0.4, settings.Temperature);
    }

    [Fact]
    public async Task GetExpertSettingsAsync_OverrideFileWinsOverSchemaDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("tl-expert-exec-context-");
        try
        {
            var file = Path.Combine(directory.FullName, "settings.override.json");
            await File.WriteAllTextAsync(file, """{"style":"formal","temperature":0.9}""", TestSupport.CancellationToken);
            var context = CreateContext(TestSupport.Json("""{"style":"concise"}"""), new FileInfo(file));

            var settings = await context.GetExpertSettingsAsync<SampleSettings>(TestSupport.CancellationToken);

            Assert.Equal("formal", settings.Style);
            Assert.Equal(0.9, settings.Temperature);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetExpertSettingsAsync_TypeMismatch_ThrowsLocalExpertSettingsException()
    {
        var context = CreateContext(TestSupport.Json("""{"temperature":"hot"}"""));

        var exception = await Assert.ThrowsAsync<LocalExpertSettingsException>(
            () => context.GetExpertSettingsAsync<SampleSettings>(TestSupport.CancellationToken).AsTask());

        Assert.NotNull(exception.InnerException as JsonException);
    }

    private static LocalExpertExecutionContext CreateContext(
        JsonElement? schemaDefaults = null,
        FileInfo? settingsOverrideFile = null) =>
        new(new StubBasicAi(), new BoundPlayerProfile(TestSupport.PlayerId, "Tester", "persona"),
            NullLogger.Instance, schemaDefaults, settingsOverrideFile);

    private sealed class SampleSettings
    {
        public string Style { get; init; } = "balanced";

        public double Temperature { get; init; } = 0.4;
    }

    private sealed class StubBasicAi : IRuntimeBasicAi
    {
        public IReadOnlyList<BasicAiModelDescriptor> AvailableModels => [];

        public IAsyncEnumerable<BasicAiStreamEvent> StreamAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The stub BasicAi is never invoked.");

        public Task<BasicAiCompletionResult> CompleteAsync(
            BasicAiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The stub BasicAi is never invoked.");
    }
}
