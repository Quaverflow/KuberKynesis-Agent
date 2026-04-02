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
- `tools/Kuberkynesis.Agent.BrowserBridge`
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

## Local browser extension bridge

For local hosted-UI bridge testing, load the unpacked extension under `tools/Kuberkynesis.Agent.BrowserBridge` from `chrome://extensions` or `edge://extensions`.

## Sync metadata

- Source repository: Quaverflow/KuberKynesis
- Source commit: e79a51d80eb72357944a9854e8e5de9e6d16d0ec
- Full sync manifest: `SYNC-METADATA.json`
