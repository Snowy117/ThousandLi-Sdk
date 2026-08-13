# SDK Agent Instructions

This directory is the `ThousandLi-Sdk` Git submodule and an independent public repository. Its source is the single authority for public creator contracts, DevHost, templates, testing tools, and `@thousandli/sdk`.

- Work on a non-`main` branch inside this submodule.
- Never add source-project references from this SDK to the private parent platform repository.
- The parent platform consumes released NuGet/npm artifacts; submodule presence is for coordinated maintenance and compatibility checks only.
- Commit and push SDK changes in this repository before updating the parent repository's `sdk` gitlink.
- Keep public projects free of Host, EF persistence, AccessControl, production audit, official Expert implementation, and platform credential dependencies.

For every C# batch, run:

```bash
dotnet format --severity info --verify-no-changes | echo $?
dotnet test ThousandLi.Sdk.slnx --no-restore
```

Resolve every info/hint diagnostic; do not use `dotnet format` to rewrite source. Before a PR, also run:

```bash
jb inspectcode ThousandLi.Sdk.slnx -f=Xml -e=HINT -o=/tmp/thousandli-sdk-inspectcode.xml
npm ci
npm run type-check
npm test
npm run build
npm audit --audit-level=low
```

Frontend changes must preserve the framework-neutral action/postMessage contracts and generated Vue template. Local package DLLs are trusted creator-machine code, not a sandbox.
