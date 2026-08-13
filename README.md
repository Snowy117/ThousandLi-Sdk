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

## Create a game

Install `ThousandLi.Templates`, then create a backend, Vue frontend, and Fake Expert scenario together:

```bash
dotnet new install ThousandLi.Templates
dotnet new thousandli-game -n MyGame --authorId my-studio --packageName my-game
```

The generated project references the public NuGet packages. Its build produces a validated Package Artifact
directory and ZIP containing `package.json`, backend binaries, and the frontend build under `frontendRoot`.

Local package DLLs run as trusted code on the creator machine; the development runtime is not a sandbox.
