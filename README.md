# Kuberkynesis Agent

This public repo is a filtered export of the private Kuberkynesis monorepo. It includes the local agent, the agent-facing shared contracts, the Live Surface libraries the agent depends on, agent-focused tests, and packaged binaries. It intentionally does not include the browser UI source tree.

## Included source

- `src/Kuberkynesis.Agent`
- `src/Kuberkynesis.Agent.Core`
- `src/Kuberkynesis.Agent.Kube`
- `src/Kuberkynesis.Agent.Transport`
- `src/Kuberkynesis.LiveSurface`
- `src/Kuberkynesis.LiveSurface.AspNetCore`
- `src/Kuberkynesis.Ui.Shared`
- `tests/Kuberkynesis.Agent.Tests`
- `tests/Kuberkynesis.Agent.Integration`

## Build locally

```powershell
dotnet restore .\KuberKynesis.slnx
dotnet build .\KuberKynesis.slnx -c Release --no-restore
dotnet test .\tests\Kuberkynesis.Agent.Tests\Kuberkynesis.Agent.Tests.csproj -c Release --no-restore
dotnet test .\tests\Kuberkynesis.Agent.Integration\Kuberkynesis.Agent.Integration.csproj -c Release --no-restore
```

## Package binaries

```powershell
.\scripts\Publish-Agent.ps1
```

Packaged archives and checksum files are published under `artifacts/agent/<rid>/`.

## Sync metadata

- Source repository: Quaverflow/KuberKynesis
- Source commit: fb7ea1989c061b74077d0d87ba7950a15f2e0804
- Full sync manifest: `SYNC-METADATA.json`
