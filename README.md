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

Both the sample game and the game template require the official long-text-writing category
contract `thousandli.expert/long-text-writing` — the stable id carried by the category anchor in
the `ThousandLi.Contracts` package (`AbstractLongTextWritingExpert.ContractId`). There is no
version negotiation and no fingerprint: the type is the contract. Games consume the category
through the typed expert facade (`context.Experts.Use<AbstractLongTextWritingExpert>()`).

> **Compatibility note (0.5.0-preview.1).** The `0.5.0-preview` line is a preview-breaking
> refactor of the expert contract surface (type-as-contract: `ExpertBase` CRTP anchors, pure-id
> `[ExpertContract]`, structured invokers, composition engine). The former slice-1 binary
> compatibility promise — pinned by `Slice1CompatFixture` against pre-compiled DLLs — is
> **retired** with this line; recompile expert and game packages against the new packages
> instead of expecting binary drop-in compatibility.

## Create a game

Install `ThousandLi.Templates`, then create a backend, Vue frontend, and Fake Expert scenario together:

```bash
dotnet new install ThousandLi.Templates
dotnet new thousandli-game -n MyGame --authorId my-studio --packageName my-game
```

The generated project references the public NuGet packages. Its build produces a validated Package Artifact
directory and ZIP containing `package.json`, backend binaries, and the frontend build under `frontendRoot`.

## Create an expert package

The same `ThousandLi.Templates` package also ships an expert template. Generate a concrete
long-text-writing expert skeleton next to a generated game:

```bash
dotnet new thousandli-expert -n MyExpert --authorId my-studio --packageName my-expert
```

The generated project derives from `AbstractLongTextWritingExpert`, declares
`[assembly: ExpertPackageEntryPoint(typeof(AbstractLongTextWritingExpert), typeof(MyExpert.LongTextWritingExpert))]`,
references the released `ThousandLi.Contracts` / `ThousandLi.ExpertAuthoring` NuGet packages, and builds
straight into a loadable Expert Package Artifact (`IsThousandLiExpertPackageArtifact`). Build both sides
and run them together in the DevHost:

```bash
dotnet build MyGame/MyGame.slnx
dotnet build MyExpert/MyExpert.slnx
export THOUSANDLI_GATEWAY_API_KEY=<your key>
dotnet run --project src/ThousandLi.DevHost -- \
  --artifact MyGame/bin/Debug/net10.0/PackageArtifact \
  --fake-scenarios MyGame/fake-scenarios.json \
  --experts local \
  --expert-artifact MyExpert/bin/Debug/net10.0/PackageArtifact \
  --gateway-endpoint https://your-openai-compatible-endpoint/v1/chat/completions \
  --gateway-model your-model-id \
  --ephemeral
```

See "Run an expert locally with a real gateway" below for the full flag reference, including explicit
`--expert-binding` values when several expert packages serve the same contract.

## Author a concrete expert

A concrete expert derives from the abstract category anchor shipped with its contract package. For the official
long-text-writing category, derive from `AbstractLongTextWritingExpert` (`ThousandLi.Contracts`) and override the
two execution cores:

```csharp
[assembly: ExpertPackageEntryPoint(typeof(AbstractLongTextWritingExpert), typeof(MyExpert))]

public sealed class MyExpert : AbstractLongTextWritingExpert
{
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken)
    {
        // Call RuntimeContext.BasicAi, stream the model output through the configured primary
        // output / feature callbacks, and return the turn metadata.
    }

    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken)
    {
        // Non-streaming variant; most experts reuse the streaming core.
    }
}
```

Key points:

- **The type is the contract.** `AbstractLongTextWritingExpert.ContractId` is the stable id
  (`thousandli.expert/long-text-writing`); the anchor's `[ExpertContract]` attribute carries it. There is no
  version negotiation and no fingerprint — the shared `LongTextWritingExpertInvoker` decodes structured
  invocation JSON onto the fluent API and aggregates the completion frame, so experts contain no JSON parsing
  boilerplate.
- **`RuntimeContext` exposes exactly four capabilities**: `BasicAi`, `GetExpertSettingsAsync<TSettings>()`
  (local policy: schema defaults plus an optional `expert-settings.json` override file), `PlayerProfile`,
  and `Logger`.
- **Credentials stay in the composition root.** The gateway API key is read from an environment variable
  by DevHost; expert code and frontend JS only ever see an authenticated `ILocalBasicAi`.
- **Per-invocation lifecycle.** The composition creates a fresh expert instance for every invocation and the
  base class enforces bind-once plus single-execution; never reuse instances across calls.

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
  --experts local \
  --expert-artifact samples/ThousandLi.SampleExpert/bin/Debug/net10.0/PackageArtifact \
  --gateway-endpoint https://your-openai-compatible-endpoint/v1/chat/completions \
  --gateway-model your-model-id \
  --ephemeral
```

`--expert-artifact`, `--expert-binding contractId=expertPackageId`, `--contract-assembly`,
`--gateway-endpoint`, `--gateway-model`, and `--gateway-api-key-env` require `--experts local`.
When several loaded expert packages serve the same contract, add an explicit
`--expert-binding thousandli.expert/long-text-writing=<packageId>` (multiple candidates without a binding is a
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
