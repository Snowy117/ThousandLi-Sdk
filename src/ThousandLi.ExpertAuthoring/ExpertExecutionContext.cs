using System.Text.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The restricted execution context held by a runtime-facing concrete expert: exactly four
/// capabilities (<see cref="BasicAi"/>, <see cref="GetExpertSettingsAsync{TSettings}"/>,
/// <see cref="PlayerProfile"/>, <see cref="Logger"/>). The full game-backend execution context is
/// deliberately absent so game-backend concerns stay unreachable at compile time from expert code.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertExecutionContext
{
    IRuntimeBasicAi BasicAi { get; }

    /// <summary>
    /// Returns the typed effective settings resolved with the local two-layer policy
    /// (schema defaults + optional local override file), resolved once per context and cached.
    /// </summary>
    ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(CancellationToken cancellationToken = default)
        where TSettings : class, new();

    BoundPlayerProfile PlayerProfile { get; }

    ILogger Logger { get; }
}

/// <summary>
/// Composition-friendly implementation of <see cref="IExpertExecutionContext"/> for locally
/// executed concrete experts. The settings layers are supplied here by the composition root;
/// credentials never flow through this type because they live behind
/// <see cref="IRuntimeBasicAi"/> implementations.
/// </summary>
public sealed class LocalExpertExecutionContext(
    IRuntimeBasicAi basicAi,
    BoundPlayerProfile playerProfile,
    ILogger logger,
    JsonElement? schemaDefaults = null,
    FileInfo? settingsOverrideFile = null) : IExpertExecutionContext
{
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private JsonElement? _resolvedSettings;

    public IRuntimeBasicAi BasicAi { get; } = basicAi ?? throw new ArgumentNullException(nameof(basicAi));

    public BoundPlayerProfile PlayerProfile { get; } =
        playerProfile ?? throw new ArgumentNullException(nameof(playerProfile));

    public ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(
        CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        var settings = await ResolveSettingsAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return settings.Deserialize<TSettings>(ExpertSettingsJson.Options)
                ?? throw new InvalidOperationException(
                    $"The resolved expert settings deserialized to null for '{typeof(TSettings)}'.");
        }
        catch (JsonException exception)
        {
            throw new LocalExpertSettingsException(
                $"The resolved expert settings could not be deserialized into '{typeof(TSettings)}'.",
                exception);
        }
    }

    private async ValueTask<JsonElement> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        if (_resolvedSettings is { } resolved)
            return resolved;
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolvedSettings is { } cached)
                return cached;
            var merged = await LocalExpertSettingsResolver
                .ResolveAsync(schemaDefaults, settingsOverrideFile, cancellationToken)
                .ConfigureAwait(false);
            _resolvedSettings = merged;
            return merged;
        }
        finally
        {
            _settingsLock.Release();
        }
    }
}
