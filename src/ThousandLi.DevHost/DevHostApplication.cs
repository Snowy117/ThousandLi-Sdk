using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

[PublicAPI]
public sealed class DevHostApplicationState : IDisposable
{
    internal DevHostApplicationState(
        LoadedGamePackage package,
        LocalGameRuntime runtime,
        BoundPlayerProfile player)
    {
        Package = package;
        Runtime = runtime;
        Player = player;
    }

    public LoadedGamePackage Package { get; }
    public LocalGameRuntime Runtime { get; }
    public BoundPlayerProfile Player { get; }

    public void Dispose() => Package.Dispose();
}

public static class DevHostApplication
{
    public static async Task<WebApplication> BuildAsync(
        DevHostOptions options,
        string[]? webApplicationArgs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fakeExperts = ScriptedFakeExpertExecutor.Load(options.FakeScenariosPath);
        var expertFacade = ScriptedLongTextWritingExpertFacade.Load(options.FakeScenariosPath);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var package = GamePackageLoader.Load(options.ArtifactDirectory, fakeExperts.Contracts);
        try
        {
            var sessionId = new SessionId(options.SessionId);
            ILocalSessionStore store = options.Ephemeral
                ? new InMemoryLocalSessionStore()
                : new FileLocalSessionStore(package.Manifest.PackageId, options.WorkspaceId, options.DataRoot);
            if (options.Reset) await store.ResetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var player = new BoundPlayerProfile(
                new PlayerId("dev-player"),
                "Local Creator",
                "Local development player");
            var runtime = await LocalGameRuntime.CreateAsync(
                package.Manifest.PackageId,
                package.Backend,
                player,
                fakeExperts,
                store,
                sessionId,
                expertFacade,
                buckets: null,
                gameSettingsStore: null,
                logger: loggerFactory.CreateLogger($"ThousandLi.GameBackend/{package.Manifest.PackageId}"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var state = new DevHostApplicationState(package, runtime, player);

            var builder = WebApplication.CreateBuilder(webApplicationArgs ?? []);
            builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
            builder.Services.Configure<JsonOptions>(json =>
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
            builder.Services.AddSingleton(state);
            var app = builder.Build();
            app.Lifetime.ApplicationStopped.Register(state.Dispose);
            MapRoutes(app, options, state);
            return app;
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    private static void MapRoutes(WebApplication app, DevHostOptions options, DevHostApplicationState state)
    {
        app.MapGet("/", () => Results.Content(PreviewShell(options.FrontendUrl), "text/html", Encoding.UTF8));
        app.MapGet("/api/session", () => Results.Json(new
        {
            sessionId = state.Runtime.SessionId.Value,
            gamePackageId = state.Package.Manifest.PackageId,
            defaultBranchId = LocalGameRuntime.MainBranchId.Value,
            playerId = state.Player.PlayerId.Value,
            playerName = state.Player.PlayerName,
            persona = state.Player.Persona,
            expertPackageId = "fake",
            committedState = state.Runtime.CommittedState
        }));

        app.MapPost("/api/actions", async httpContext =>
        {
            var payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                httpContext.Request.Body,
                cancellationToken: httpContext.RequestAborted).ConfigureAwait(false);
            var action = new PlayerActionEnvelope(state.Player.PlayerId, payload);
            httpContext.Response.ContentType = "application/x-ndjson";
            await foreach (var runtimeEvent in state.Runtime.HandleActionAsync(action, httpContext.RequestAborted)
                               .ConfigureAwait(false))
            {
                await httpContext.Response.WriteAsync(
                    WireContracts.SerializeLine(WireContracts.ToWire(runtimeEvent)),
                    httpContext.RequestAborted).ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
            }
        });

        app.MapPost("/api/frontend-requests", async (HttpContext httpContext) =>
        {
            var payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                httpContext.Request.Body,
                cancellationToken: httpContext.RequestAborted).ConfigureAwait(false);
            var result = await state.Runtime.HandleFrontendRequestAsync(
                new FrontendRequestEnvelope(state.Player.PlayerId, payload),
                httpContext.RequestAborted).ConfigureAwait(false);
            return Results.Json(result.Payload);
        });

        if (state.Package.Manifest.FrontendRoot is not { } frontendRoot) return;
        var physicalRoot = GamePackageLoader.ResolveArtifactPath(state.Package.ArtifactDirectory, frontendRoot);
        if (!Directory.Exists(physicalRoot)) return;
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(physicalRoot),
            RequestPath = "/game"
        });
    }

    private static string PreviewShell(string frontendUrl) => $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>ThousandLi DevHost</title>
  <style>
    html, body, iframe { width: 100%; height: 100%; margin: 0; border: 0; }
    body { background: #111318; }
  </style>
</head>
<body>
  <iframe id="game" src="{{System.Net.WebUtility.HtmlEncode(frontendUrl)}}" allow="clipboard-write"></iframe>
  <script>
    const frame = document.getElementById('game')
    window.addEventListener('message', async event => {
      if (event.source !== frame.contentWindow) return
      const message = event.data
      if (!message || typeof message.id !== 'string' || typeof message.type !== 'string' || !message.type.startsWith('@thousandli/')) return
      try {
        if (message.type === '@thousandli/getSessionContext') {
          const response = await fetch('/api/session')
          reply(message, '@thousandli/sessionContext', await response.json())
        } else if (message.type === '@thousandli/getActionUrl') {
          reply(message, '@thousandli/actionUrl', { url: '/api/actions' })
        } else if (message.type === '@thousandli/proxyRequest') {
          const request = message.payload || {}
          if (typeof request.url !== 'string' || !request.url.startsWith('/api/') || request.url.startsWith('//')) {
            throw new Error('Proxy requests must target a DevHost /api/ path.')
          }
          const response = await fetch(request.url, {
            method: request.method || 'GET',
            headers: request.body === undefined ? undefined : { 'content-type': 'application/json' },
            body: request.body === undefined ? undefined : JSON.stringify(request.body),
          })
          const text = await response.text()
          let body = null
          if (text) { try { body = JSON.parse(text) } catch { body = text } }
          reply(message, '@thousandli/proxyResponse', { status: response.status, body })
        }
      } catch (error) {
        frame.contentWindow.postMessage({ id: message.id, type: message.type, payload: null, error: String(error) }, '*')
      }
    })
    function reply(request, type, payload) {
      frame.contentWindow.postMessage({ id: request.id, type, payload }, '*')
    }
  </script>
</body>
</html>
""";
}
