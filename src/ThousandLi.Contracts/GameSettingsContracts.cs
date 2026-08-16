using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>标记 GameBackend 可供用户配置的 Game Settings 类型。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GameSettingsAttribute : Attribute;

/// <summary>标记 Game Settings 中可供用户配置的成员描述。</summary>
[AttributeUsage(AttributeTargets.Property)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class GameSettingsMemberAttribute(
    string title,
    string shortDescription,
    string longDescription) : Attribute
{
    /// <summary>成员标题。</summary>
    public string Title { get; } = title;

    /// <summary>成员短描述。</summary>
    public string ShortDescription { get; } = shortDescription;

    /// <summary>成员长描述。</summary>
    public string LongDescription { get; } = longDescription;
}

/// <summary>Game Settings 的持久化存储端口（由运行时注入，按用户 + 游戏包维度存取）。</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IGameSettingsStore
{
    /// <summary>读取某用户在某游戏包下的设置快照；无存储时返回 null。</summary>
    Task<JsonElement?> GetAsync(UserId userId, string gamePackageId, CancellationToken cancellationToken = default);

    /// <summary>写入某用户在某游戏包下的设置快照。</summary>
    Task SetAsync(UserId userId, string gamePackageId, JsonElement settings, CancellationToken cancellationToken = default);

    /// <summary>删除某用户在某游戏包下的设置快照。</summary>
    Task<bool> DeleteAsync(UserId userId, string gamePackageId, CancellationToken cancellationToken = default);
}

/// <summary>游戏设置相关的前端请求处理扩展方法。</summary>
public static class GameSettingsExtensions
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     处理标准 type="getGameSettings" / type="updateGameSettings" 前端请求。
    ///     当 <paramref name="context" /> 的 <see cref="FrontendRequestContext.GameSettingsStore" /> 为 null 时抛出异常。
    /// </summary>
    /// <typeparam name="TSettings">Package 声明的游戏设置类型。</typeparam>
    /// <param name="context">已注入 <see cref="IGameSettingsStore" /> 的前端请求上下文。</param>
    /// <param name="request">前端请求信封。</param>
    /// <param name="userId">当前用户标识符。</param>
    /// <param name="gamePackageId">规范化游戏包标识字符串（如 "official_xuezhixia@1.0.0"）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>getGameSettings 返回设置 JSON；updateGameSettings 返回 { ok: true }。</returns>
    /// <exception cref="NotSupportedException">请求类型不是 getGameSettings 或 updateGameSettings。</exception>
    /// <exception cref="InvalidOperationException">context.GameSettingsStore 为 null。</exception>
    public static async ValueTask<FrontendRequestResult> HandleSettingsRequestAsync<TSettings>(
        this FrontendRequestContext context,
        FrontendRequestEnvelope request,
        UserId userId,
        string gamePackageId,
        CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePackageId);

        if (context.GameSettingsStore is null)
        {
            throw new InvalidOperationException(
                "GameSettingsStore is not available in the frontend request context.");
        }

        var requestType = request.Payload.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        return requestType switch
        {
            "getGameSettings" => await HandleGetSettingsAsync<TSettings>(
                context, userId, gamePackageId, cancellationToken).ConfigureAwait(false),
            "updateGameSettings" => await HandleUpdateSettingsAsync<TSettings>(
                context, request, userId, gamePackageId, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Frontend request type '{requestType ?? "null"}' is not supported by HandleSettingsRequestAsync.")
        };
    }

    private static async ValueTask<FrontendRequestResult> HandleGetSettingsAsync<TSettings>(
        FrontendRequestContext context,
        UserId userId,
        string gamePackageId,
        CancellationToken cancellationToken)
        where TSettings : class, new()
    {
        var stored = await context.GameSettingsStore!.GetAsync(userId, gamePackageId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null)
            return new FrontendRequestResult(stored.Value);

        var defaultInstance = new TSettings();
        var defaultJson = JsonSerializer.SerializeToElement(defaultInstance, SettingsJsonOptions);
        return new FrontendRequestResult(defaultJson);
    }

    private static async ValueTask<FrontendRequestResult> HandleUpdateSettingsAsync<TSettings>(
        FrontendRequestContext context,
        FrontendRequestEnvelope request,
        UserId userId,
        string gamePackageId,
        CancellationToken cancellationToken)
        where TSettings : class, new()
    {
        var requestedSettings = request.Payload.TryGetProperty("settings", out var settingsElement)
            ? settingsElement
            : throw new ArgumentException(
                "Frontend request with type 'updateGameSettings' must include a 'settings' property.", nameof(request));

        if (requestedSettings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("Game settings update payload cannot be undefined or null.", nameof(request));

        var stored = await context.GameSettingsStore!.GetAsync(userId, gamePackageId, cancellationToken)
            .ConfigureAwait(false);
        var baseJson = stored ?? JsonSerializer.SerializeToElement(new TSettings(), SettingsJsonOptions);
        using var mergedDocument = JsonDocument.Parse(baseJson.GetRawText());
        var merged =
            JsonSerializer.Deserialize<TSettings>(mergedDocument.RootElement.GetRawText(), SettingsJsonOptions) ??
            throw new InvalidOperationException(
                $"Stored game settings for '{typeof(TSettings).FullName}' could not be materialized.");
        var requested = JsonSerializer.Deserialize<TSettings>(requestedSettings.GetRawText(), SettingsJsonOptions) ??
                        throw new InvalidOperationException(
                            $"Game settings update for '{typeof(TSettings).FullName}' could not be materialized.");
        foreach (var property in typeof(TSettings).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite)
                continue;

            var jsonName = SettingsJsonOptions.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
            if (!requestedSettings.TryGetProperty(jsonName, out _))
                continue;

            property.SetValue(merged, property.GetValue(requested));
        }

        var normalized = JsonSerializer.SerializeToElement(merged, SettingsJsonOptions);
        await context.GameSettingsStore!.SetAsync(userId, gamePackageId, normalized, cancellationToken)
            .ConfigureAwait(false);

        return new FrontendRequestResult(JsonSerializer.SerializeToElement(new { ok = true }, SettingsJsonOptions));
    }
}
