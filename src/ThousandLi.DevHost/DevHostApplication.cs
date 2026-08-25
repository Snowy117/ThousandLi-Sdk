using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

[PublicAPI]
public sealed class DevHostApplicationState : IDisposable
{
    private readonly List<LoadedExpertPackage> _expertPackages;
    private readonly HttpClient? _gatewayClient;

    internal DevHostApplicationState(
        LoadedGamePackage package,
        LocalGameRuntime runtime,
        BoundPlayerProfile player,
        List<LoadedExpertPackage> expertPackages,
        HttpClient? gatewayClient,
        PlaygroundService playground)
    {
        Package = package;
        Runtime = runtime;
        Player = player;
        Playground = playground;
        _expertPackages = expertPackages;
        _gatewayClient = gatewayClient;
    }

    public LoadedGamePackage Package { get; }
    public LocalGameRuntime Runtime { get; }
    public BoundPlayerProfile Player { get; }
    public PlaygroundService Playground { get; }

    public void Dispose()
    {
        foreach (var expertPackage in _expertPackages)
            expertPackage.Dispose();
        _gatewayClient?.Dispose();
        Package.Dispose();
    }
}

public static class DevHostApplication
{
    public static async Task<WebApplication> BuildAsync(
        DevHostOptions options,
        string[]? webApplicationArgs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ExpertComposition.ValidateExecutorOptions(options);
        var fakeExperts = ScriptedFakeExpertExecutor.Load(options.FakeScenariosPath);
        var contracts = ExpertComposition.BuildContractRegistry(options.ContractAssemblies);
        var useLocalExperts = options.ExpertExecutor == DevHostOptions.LocalExecutorName;

        var gamePackage = GamePackageLoader.Load(
            options.ArtifactDirectory,
            useLocalExperts
                ? [.. fakeExperts.Contracts, .. contracts.Registry.Contracts
                    .Select(contract => contract.ToDescriptor())
                    .Where(descriptor => fakeExperts.Contracts.All(
                        existing => existing.Id != descriptor.Id))]
                : fakeExperts.Contracts);
        try
        {
            var sessionId = new SessionId(options.SessionId);
            ILocalSessionStore store = options.Ephemeral
                ? new InMemoryLocalSessionStore()
                : new FileLocalSessionStore(gamePackage.Manifest.PackageId, options.WorkspaceId, options.DataRoot);
            if (options.Reset) await store.ResetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var player = new BoundPlayerProfile(
                new PlayerId("dev-player"),
                "Local Creator",
                "Local development player");
            var expertPackages = new List<LoadedExpertPackage>();
            HttpClient? gatewayClient = null;
            LocalExpertExecutor? localExperts = null;
            try
            {
                if (useLocalExperts)
                {
                    gatewayClient = new HttpClient();
                    localExperts = ExpertComposition.CreateLocalExecutor(
                        options, contracts, player, gatewayClient, expertPackages);
                }

                var runtime = await LocalGameRuntime.CreateAsync(
                    gamePackage.Manifest.PackageId,
                    gamePackage.Backend,
                    player,
                    localExperts is not null ? localExperts : fakeExperts,
                    store,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);
                IExpertRecordingStore recordingStore = options.Ephemeral
                    ? new InMemoryExpertRecordingStore()
                    : new FileExpertRecordingStore(options.WorkspaceId, options.DataRoot);
                var builder = WebApplication.CreateBuilder(webApplicationArgs ?? []);
                builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
                builder.Services.Configure<JsonOptions>(json =>
                    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
                // The playground logger comes from the host's own logging pipeline; the factories
                // keep PlaygroundService and DevHostApplicationState eagerly resolvable singletons.
                builder.Services.AddSingleton(serviceProvider => new PlaygroundService(
                    fakeExperts,
                    localExperts,
                    contracts.Registry,
                    recordingStore,
                    serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PlaygroundService>()));
                builder.Services.AddSingleton(serviceProvider => new DevHostApplicationState(
                    gamePackage,
                    runtime,
                    player,
                    expertPackages,
                    gatewayClient,
                    serviceProvider.GetRequiredService<PlaygroundService>()));
                var app = builder.Build();
                var state = app.Services.GetRequiredService<DevHostApplicationState>();
                app.Lifetime.ApplicationStopped.Register(state.Dispose);
                MapRoutes(app, options, state);
                return app;
            }
            catch
            {
                foreach (var expertPackage in expertPackages)
                    expertPackage.Dispose();
                gatewayClient?.Dispose();
                throw;
            }
        }
        catch
        {
            gamePackage.Dispose();
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
            expertExecutor = options.ExpertExecutor,
            expertPackageId = options.ExpertExecutor,
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

        MapPlaygroundRoutes(app, state);

        if (state.Package.Manifest.FrontendRoot is not { } frontendRoot) return;
        var physicalRoot = GamePackageLoader.ResolveArtifactPath(state.Package.ArtifactDirectory, frontendRoot);
        if (!Directory.Exists(physicalRoot)) return;
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(physicalRoot),
            RequestPath = "/game"
        });
    }

    private static void MapPlaygroundRoutes(WebApplication app, DevHostApplicationState state)
    {
        app.MapGet("/playground", () => Results.Content(PlaygroundPage(), "text/html", Encoding.UTF8));

        app.MapGet("/api/playground/contracts", () => Results.Json(state.Playground.GetContracts()));

        app.MapGet("/api/playground/history", () => Results.Json(state.Playground.History));

        app.MapGet("/api/playground/recordings", async (HttpContext httpContext) =>
        {
            var recordings = await state.Playground.ListRecordingsAsync(httpContext.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(recordings);
        });

        app.MapGet("/api/playground/recordings/{recordingId}", async (string recordingId, HttpContext httpContext) =>
        {
            var recording = await state.Playground.LoadRecordingAsync(
                recordingId, httpContext.RequestAborted).ConfigureAwait(false);
            return recording is null
                ? Results.Problem(
                    "The recording does not exist.", statusCode: StatusCodes.Status404NotFound)
                : Results.Json(recording);
        });

        app.MapPost("/api/playground/replay", async (HttpContext httpContext) =>
        {
            PlaygroundReplayCommand? command;
            try
            {
                command = await JsonSerializer.DeserializeAsync<PlaygroundReplayCommand>(
                    httpContext.Request.Body,
                    SCommandJsonOptions,
                    cancellationToken: httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            if (command is null)
                return Results.Problem("The replay body must be a JSON object.", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var report = await state.Playground.ReplayAsync(command, httpContext.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Json(new
                {
                    matches = report.Matches,
                    strictPayloads = report.StrictPayloads,
                    diagnostics = report.Diagnostics,
                    divergences = report.Divergences
                });
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/api/playground/invoke", async (HttpContext httpContext) =>
        {
            PlaygroundInvokeCommand? command;
            try
            {
                command = await JsonSerializer.DeserializeAsync<PlaygroundInvokeCommand>(
                    httpContext.Request.Body,
                    SCommandJsonOptions,
                    cancellationToken: httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            if (command is null)
                return Results.Problem("The invoke body must be a JSON object.", statusCode: StatusCodes.Status400BadRequest);

            // Command validation happens before any event is written, so a bad command still maps
            // to a 400 problem response instead of a half-started SSE stream.
            try
            {
                httpContext.Response.ContentType = "text/event-stream";
                var sink = new SseSemanticEventSink(httpContext);
                var outcome = await state.Playground.InvokeAsync(command, sink, httpContext.RequestAborted)
                    .ConfigureAwait(false);
                await WriteTerminalAsync(httpContext, outcome).ConfigureAwait(false);
                return Results.Empty;
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    private static readonly JsonSerializerOptions SCommandJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static async Task WriteTerminalAsync(HttpContext httpContext, PlaygroundInvocationOutcome outcome)
    {
        if (outcome.Diagnostics.Status == PlaygroundService.StatusCommitted && outcome.Result is not null)
        {
            await WriteServerSentEventAsync(httpContext, new
            {
                type = "invocation",
                invocationId = outcome.Result.InvocationId,
                channelKey = outcome.Diagnostics.ChannelKey,
                durationMs = outcome.Diagnostics.DurationMs,
                output = outcome.Result.Output,
                recordingId = outcome.Diagnostics.RecordingId,
                recordingError = outcome.Diagnostics.RecordingError
            }).ConfigureAwait(false);
            return;
        }

        await WriteServerSentEventAsync(httpContext, new
        {
            type = outcome.Diagnostics.Status == PlaygroundService.StatusAborted ? "aborted" : "error",
            channelKey = outcome.Diagnostics.ChannelKey,
            durationMs = outcome.Diagnostics.DurationMs,
            detail = outcome.Error?.Message,
            recordingId = outcome.Diagnostics.RecordingId,
            recordingError = outcome.Diagnostics.RecordingError
        }).ConfigureAwait(false);
    }

    private static async Task WriteServerSentEventAsync(HttpContext httpContext, object payload)
    {
        await httpContext.Response.WriteAsync(
            "data: " + JsonSerializer.Serialize(payload) + "\n\n",
            httpContext.RequestAborted).ConfigureAwait(false);
        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
    }

    private sealed class SseSemanticEventSink(HttpContext httpContext) : IExpertSemanticEventSink
    {
        public async ValueTask WriteAsync(
            ExpertSemanticEvent semanticEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(semanticEvent);
            await httpContext.Response.WriteAsync(
                "data: " + JsonSerializer.Serialize(new
                {
                    type = "event",
                    eventType = semanticEvent.EventType,
                    payload = semanticEvent.Payload
                }) + "\n\n",
                cancellationToken).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string PlaygroundPage() => """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>ThousandLi Expert Playground</title>
      <style>
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; background: #111318; color: #e6e8ee; font: 14px/1.5 -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1080px; margin: 0 auto; padding: 20px; display: grid; gap: 20px; grid-template-columns: 380px 1fr; }
        h1 { font-size: 18px; margin: 0; }
        h2 { font-size: 14px; margin: 0 0 8px; color: #9aa3b5; text-transform: uppercase; letter-spacing: 0.06em; }
        section { background: #1a1d24; border: 1px solid #2a2e39; border-radius: 10px; padding: 16px; }
        label { display: block; margin: 10px 0 4px; color: #9aa3b5; font-size: 12px; }
        select, input, textarea { width: 100%; background: #111318; color: #e6e8ee; border: 1px solid #2a2e39; border-radius: 6px; padding: 6px 8px; font: inherit; }
        textarea { min-height: 120px; font-family: ui-monospace, monospace; font-size: 12px; }
        button { background: #5b6abf; color: white; border: 0; border-radius: 6px; padding: 8px 14px; font: inherit; cursor: pointer; }
        button.secondary { background: #2a2e39; }
        button:disabled { opacity: 0.5; cursor: wait; }
        .hint { color: #6b7385; font-size: 12px; }
        #events { font-family: ui-monospace, monospace; font-size: 12px; white-space: pre-wrap; max-height: 420px; overflow-y: auto; background: #111318; border-radius: 6px; padding: 10px; min-height: 200px; }
        #result { font-family: ui-monospace, monospace; font-size: 12px; white-space: pre-wrap; background: #111318; border-radius: 6px; padding: 10px; min-height: 60px; margin: 0; }
        table { width: 100%; border-collapse: collapse; font-size: 12px; }
        th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid #2a2e39; }
        th { color: #9aa3b5; font-weight: normal; }
        .status-committed { color: #7bd88f; }
        .status-aborted { color: #f5a97f; }
        .status-error { color: #ed8796; }
        .ev { margin: 0 0 2px; }
        .ev .t { color: #8aadf4; }
        .terminal { margin-top: 8px; padding-top: 8px; border-top: 1px dashed #2a2e39; }
        .ok { color: #7bd88f; }
        .bad { color: #ed8796; }
        .row { display: flex; gap: 8px; align-items: center; }
        .row > * { flex: 1; }
        .check { display: flex; align-items: center; gap: 6px; margin-top: 10px; }
        .check label { margin: 0; }
        .check input { width: auto; }
        header { grid-column: 1 / -1; display: flex; justify-content: space-between; align-items: center; }
        .wide { grid-column: 1 / -1; }
        #left { display: grid; gap: 0; }
        </style>
    </head>
    <body>
    <main>
      <header>
        <h1>ThousandLi Expert Playground</h1>
        <span class="hint"><a href="/" style="color:#8aadf4">← back to preview shell</a></span>
      </header>

      <section id="left">
        <h2>Invoke</h2>
        <label for="contract">Contract</label>
        <select id="contract"></select>
        <label for="executor">Executor</label>
        <select id="executor"></select>
        <label for="scenario">Scenario id <span class="hint">(fake executor)</span></label>
        <input id="scenario" placeholder="advance">
        <label for="package">Expert package <span class="hint">(local override, optional)</span></label>
        <select id="package"><option value="">— configured binding —</option></select>
        <label for="input">Input JSON</label>
        <textarea id="input">{"turn":1,"player":"traveler","action":"look around"}</textarea>
        <div class="check"><input type="checkbox" id="record"><label for="record">Record invocation</label></div>
        <div class="row" style="margin-top:12px">
          <button id="invoke" disabled>Invoke</button>
          <button id="refresh" class="secondary">Refresh</button>
        </div>
      </section>

      <section>
        <h2>Stream</h2>
        <div id="events"><span class="hint">No invocation yet.</span></div>
        <h2 style="margin-top:16px">Terminal result</h2>
        <pre id="result"><span class="hint">—</span></pre>
      </section>

      <section>
        <h2>History</h2>
        <table><thead><tr><th>channel</th><th>contract</th><th>executor</th><th>status</th><th>ms</th><th>recording</th></tr></thead>
        <tbody id="history"><tr><td colspan="6" class="hint">empty</td></tr></tbody></table>
      </section>

      <section>
        <h2>Recordings &amp; replay</h2>
        <table><thead><tr><th>recording</th><th>contract</th><th>status</th><th>when (UTC)</th><th></th></tr></thead>
        <tbody id="recordings"><tr><td colspan="5" class="hint">empty</td></tr></tbody></table>
        <div class="check"><input type="checkbox" id="strict"><label for="strict">Strict payload comparison</label></div>
        <div id="replayResult" class="hint" style="margin-top:8px"></div>
      </section>
    </main>
    <script>
    const $ = id => document.getElementById(id)
    const state = { contracts: [], replayRecordingId: null }

    async function loadContracts() {
      const contracts = await (await fetch('/api/playground/contracts')).json()
      state.contracts = contracts
      const contractSelect = $('contract'), packageSelect = $('package')
      contractSelect.innerHTML = ''
      contracts.forEach(contract => {
        const option = document.createElement('option')
        option.value = contract.contractId
        option.textContent = `${contract.contractId} ${contract.version}`
        contractSelect.appendChild(option)
      })
      contractSelect.onchange = syncPackages
      syncPackages()
    }

    function syncPackages() {
      const contract = state.contracts.find(entry => entry.contractId === $('contract').value)
      const packageSelect = $('package')
      packageSelect.innerHTML = '<option value="">— configured binding —</option>'
      ;(contract ? contract.expertPackageIds : []).forEach(packageId => {
        const option = document.createElement('option')
        option.value = packageId
        option.textContent = packageId
        packageSelect.appendChild(option)
      })
      // Executors come from the contracts endpoint: only executors actually serving the selected
      // contract are selectable. With no contract selected, fall back to the union of available
      // executors so the control reflects what this DevHost actually loaded.
      const executorSelect = $('executor')
      const previous = executorSelect.value
      const executors = contract
        ? contract.executors
        : [...new Set(state.contracts.flatMap(entry => entry.executors))]
      executorSelect.innerHTML = executors
        .map(name => `<option value="${name}">${name}</option>`).join('')
      if (executors.includes(previous)) executorSelect.value = previous
      $('invoke').disabled = !(contract && executors.length > 0)
    }

    async function loadHistory() {
      const history = await (await fetch('/api/playground/history')).json()
      $('history').innerHTML = history.length === 0
        ? '<tr><td colspan="6" class="hint">empty</td></tr>'
        : history.map(entry => `<tr>
            <td>${entry.channelKey}</td><td>${entry.contractId}</td><td>${entry.executor}</td>
            <td class="status-${entry.status}">${entry.status}</td><td>${entry.durationMs}</td>
            <td class="hint">${entry.recordingId ?? (entry.recordingError ? '⚠ ' + entry.recordingError : '—')}</td>
          </tr>`).join('')
    }

    async function loadRecordings() {
      const recordings = await (await fetch('/api/playground/recordings')).json()
      $('recordings').innerHTML = recordings.length === 0
        ? '<tr><td colspan="5" class="hint">empty</td></tr>'
        : recordings.map(entry => `<tr>
            <td>${entry.recordingId}</td><td>${entry.contractId}</td>
            <td class="status-${entry.status}">${entry.status}</td><td>${entry.recordedAtUtc}</td>
            <td><button class="secondary" data-replay="${entry.recordingId}" data-executor="${entry.executor ?? 'fake'}">Replay</button></td>
          </tr>`).join('')
      document.querySelectorAll('[data-replay]').forEach(button => {
        button.onclick = () => replay(button.dataset.replay, button.dataset.executor)
      })
    }

    async function replay(recordingId, executor) {
      state.replayRecordingId = recordingId
      $('replayResult').textContent = `Replaying ${recordingId}…`
      try {
        const response = await fetch('/api/playground/replay', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            recordingId,
            executor,
            strictPayloads: $('strict').checked,
            scenarioId: $('scenario').value || undefined,
            expertPackageId: $('package').value || undefined,
          }),
        })
        const body = await response.json()
        if (!response.ok) {
          $('replayResult').innerHTML = `<span class="bad">${body.detail ?? response.status}</span>`
          console.error(`Replay rejected (${response.status}):`, body.detail ?? response.status)
          return
        }
        const verdict = body.matches
          ? '<span class="ok">✔ replay matches the recording</span>'
          : '<span class="bad">✘ replay diverged:</span>'
        const divergences = (body.divergences ?? [])
          .map(divergence => `<div>${divergence.layer}: ${divergence.detail}</div>`).join('')
        $('replayResult').innerHTML = `${verdict}${body.strictPayloads ? ' <span class="hint">(strict)</span>' : ''}${divergences}`
        await loadHistory()
      } catch (error) {
        $('replayResult').innerHTML = `<span class="bad">${String(error)}</span>`
        console.error('Replay failed:', error)
      }
    }

    function appendLine(text, cssClass) {
      const events = $('events')
      if (events.querySelector('.hint')) events.innerHTML = ''
      const line = document.createElement('div')
      line.className = 'ev' + (cssClass ? ' ' + cssClass : '')
      line.innerHTML = text
      events.appendChild(line)
      events.scrollTop = events.scrollHeight
    }

    async function invoke() {
      let input
      try { input = JSON.parse($('input').value) }
      catch (error) {
        appendLine(`<span class="bad">Input JSON is invalid: ${String(error)}</span>`)
        console.error('Input JSON is invalid:', error)
        return
      }
      const command = {
        contractId: $('contract').value,
        executor: $('executor').value,
        input,
        scenarioId: $('scenario').value || undefined,
        expertPackageId: $('package').value || undefined,
        record: $('record').checked,
      }
      $('invoke').disabled = true
      $('events').innerHTML = '<span class="hint">Invoking…</span>'
      $('result').innerHTML = '<span class="hint">—</span>'
      try {
        const response = await fetch('/api/playground/invoke', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(command),
        })
        if (!response.ok) {
          let detail = response.status
          try { detail = (await response.json()).detail ?? detail } catch { /* problem body optional */ }
          appendLine(`<span class="bad">Rejected (${response.status}): ${detail}</span>`)
          console.error(`Invocation rejected (${response.status}):`, detail)
          return
        }
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })
          let separator
          while ((separator = buffer.indexOf('\n\n')) >= 0) {
            const frame = buffer.slice(0, separator)
            buffer = buffer.slice(separator + 2)
            if (frame.startsWith('data: ')) handleFrame(JSON.parse(frame.slice(6)))
          }
        }
        await loadHistory()
        if ($('record').checked) await loadRecordings()
      } catch (error) {
        appendLine(`<span class="bad">Stream failed: ${String(error)}</span>`)
        console.error('Invocation stream failed:', error)
      } finally {
        $('invoke').disabled = false
      }
    }

    function handleFrame(frame) {
      if (frame.type === 'event') {
        appendLine(`<span class="t">${frame.eventType}</span> ${JSON.stringify(frame.payload)}`)
        return
      }
      const recording = frame.recordingId
        ? `<div class="hint">recording: ${frame.recordingId}</div>`
        : frame.recordingError ? `<div class="bad">${frame.recordingError}</div>` : ''
      if (frame.type === 'invocation') {
        appendLine(`<span class="ok">◆ committed in ${frame.durationMs} ms (channel ${frame.channelKey})</span>`, 'terminal')
        $('result').textContent = JSON.stringify(frame.output, null, 2)
      } else {
        appendLine(`<span class="bad">◆ ${frame.type} in ${frame.durationMs} ms: ${frame.detail ?? ''}</span>`, 'terminal')
        $('result').innerHTML = `<span class="bad">${frame.detail ?? frame.type}</span>`
        console.error(`Invocation ${frame.type} (${frame.channelKey}):`, frame.detail ?? '')
      }
      if (recording) appendLine(recording, 'terminal')
    }

    $('invoke').onclick = invoke
    $('refresh').onclick = async () => { await loadContracts(); await loadHistory(); await loadRecordings() }
    loadContracts().then(loadHistory).then(loadRecordings)
    </script>
    </body>
    </html>
    """;

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
