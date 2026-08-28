# ThousandLi SDK

Public authoring contracts and local development tools for ThousandLi Game Packages.

## Local sample

```bash
dotnet build samples/ThousandLi.SampleGame/ThousandLi.SampleGame.csproj
dotnet run --project src/ThousandLi.DevHost -- \
  --artifact samples/ThousandLi.SampleGame/bin/Debug/net10.0/PackageArtifact \
  --fake-scenarios samples/ThousandLi.SampleGame/fake-scenarios.json \
  --ephemeral
```

Open `http://127.0.0.1:5180`. Omit `--ephemeral` to persist local development state. Use
`--reset` to clear only the selected local session before startup.

Both the sample game and the game template require the official narrator contract
`thousandli.expert/narrator` (1.0, fingerprint `c5144664…`) from the `ThousandLi.ExpertContracts`
package. Games reference the official descriptor (`AbstractNarratorExpert.Descriptor`) instead of
declaring a private copy of the contract.

## Create a game

Install `ThousandLi.Templates`, then create a backend, Vue frontend, and Fake Expert scenario together:

```bash
dotnet new install ThousandLi.Templates
dotnet new thousandli-game -n MyGame --authorId my-studio --packageName my-game
```

The generated project references the public NuGet packages. Its build produces a validated Package Artifact
directory and ZIP containing `package.json`, backend binaries, and the frontend build under `frontendRoot`.

## Create an expert package

The same `ThousandLi.Templates` package also ships an expert template. Generate a concrete narrator
expert skeleton next to a generated game:

```bash
dotnet new thousandli-expert -n MyExpert --authorId my-studio --packageName my-expert
```

The generated project derives from `AbstractNarratorExpert`, declares
`[assembly: ExpertPackageEntryPoint(typeof(AbstractNarratorExpert), typeof(MyExpert.NarratorExpert))]`,
references the released `ThousandLi.Contracts` / `ThousandLi.ExpertContracts` NuGet packages, and builds
straight into a loadable Expert Package Artifact (`IsThousandLiExpertPackageArtifact`). Build both sides
and run them together in the DevHost:

```bash
dotnet build MyGame/MyGame.slnx
dotnet build MyExpert/MyExpert.slnx
export THOUSANDLI_GATEWAY_API_KEY=<your key>
dotnet run --project src/ThousandLi.DevHost -- \
  --artifact MyGame/bin/Debug/net10.0/PackageArtifact \
  --fake-scenarios MyGame/fake-scenarios.json \
  --expert-executor local \
  --expert-artifact MyExpert/bin/Debug/net10.0/PackageArtifact \
  --gateway-endpoint https://your-openai-compatible-endpoint/v1/chat/completions \
  --gateway-model your-model-id \
  --ephemeral
```

See "Run an expert locally with a real gateway" below for the full flag reference, including explicit
`--expert-binding` values when several expert packages serve the same contract.

## Author a concrete expert

A concrete expert derives from the abstract base shipped with its contract package. For the official
narrator contract, derive from `AbstractNarratorExpert` (`ThousandLi.ExpertContracts.Narration`) and
implement `InvokeAsync`:

```csharp
[assembly: ExpertPackageEntryPoint(typeof(AbstractNarratorExpert), typeof(MyNarratorExpert))]

public sealed class MyNarratorExpert : AbstractNarratorExpert
{
    public override async Task<JsonElement> InvokeAsync(
        JsonElement input, IExpertSemanticEventSink events, CancellationToken cancellationToken = default)
    {
        // Bind the structured input, call RuntimeContext.BasicAi, forward semantic events through
        // the sink, and return the output JSON required by the contract.
    }
}
```

Key points:

- **Contract identity lives in the base class.** `AbstractNarratorExpert.Descriptor` carries the stable
  id, `ContractVersion`, and deterministic fingerprint; a package never re-declares them.
- **`RuntimeContext` exposes exactly four capabilities**: `BasicAi`, `GetExpertSettingsAsync<TSettings>()`
  (local policy: schema defaults plus an optional `expert-settings.json` override file), `PlayerProfile`,
  and `Logger`.
- **Credentials stay in the composition root.** The gateway API key is read from an environment variable
  by DevHost; expert code and frontend JS only ever see an authenticated `ILocalBasicAi`.
- **Per-invocation lifecycle.** The executor creates a fresh expert instance and sink for every
  invocation; never reuse instances across calls.

The `samples/ThousandLi.SampleExpert` project is a complete, minimal example: mark the project with
`IsThousandLiExpertPackageArtifact` and its build produces a loadable Expert Package Artifact
(`package.json` with `packageKind: "ExpertPackage"` plus `bin/` without the shared SDK DLLs, which the
DevHost always provides itself).

## Run an expert locally with a real gateway

```bash
dotnet build samples/ThousandLi.SampleExpert/ThousandLi.SampleExpert.csproj
export THOUSANDLI_GATEWAY_API_KEY=<your key>
dotnet run --project src/ThousandLi.DevHost -- \
  --artifact samples/ThousandLi.SampleGame/bin/Debug/net10.0/PackageArtifact \
  --expert-executor local \
  --expert-artifact samples/ThousandLi.SampleExpert/bin/Debug/net10.0/PackageArtifact \
  --gateway-endpoint https://your-openai-compatible-endpoint/v1/chat/completions \
  --gateway-model your-model-id \
  --ephemeral
```

`--expert-artifact`, `--expert-binding contractId=expertPackageId`, `--contract-assembly`,
`--gateway-endpoint`, `--gateway-model`, and `--gateway-api-key-env` require `--expert-executor local`.
When several loaded expert packages serve the same contract, add an explicit
`--expert-binding thousandli.expert/narrator=<packageId>` (multiple candidates without a binding is a
deterministic startup error).

## Playground

Open `http://127.0.0.1:5180/playground` to invoke any registered contract independently of the game:
pick a contract and executor (fake or local), stream semantic events live over SSE, inspect terminal
results and invocation history (channel key, duration, status), and record invocations for replay.

## Record and replay

Enable **Record invocation** in the playground (or use the recording APIs) to persist a semantic
recording (contract descriptor, input, ordered events, terminal result) under the DevHost data
directory — `--ephemeral` keeps recordings in memory only. **Replay** re-executes the recorded
invocation and reports divergences against the stored events, comparing event-type sequences,
structural shape, and terminal-result schema by default; strict payload comparison is opt-in.
Recordings are versioned development data; an incompatible format fails loudly instead of
partially loading.

Local package DLLs run as trusted code on the creator machine; the development runtime is not a sandbox.
