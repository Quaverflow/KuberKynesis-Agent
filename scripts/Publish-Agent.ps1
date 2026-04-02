param(
    [string[]]$Runtime = @("win-x64", "linux-x64", "osx-arm64"),
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/agent"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\Kuberkynesis.Agent\Kuberkynesis.Agent.csproj"

if (-not (Test-Path $projectPath)) {
    throw "Could not find agent project at '$projectPath'."
}

foreach ($rid in $Runtime) {
    $runtimeRoot = Join-Path $repoRoot (Join-Path $OutputRoot $rid)
    $publishRoot = Join-Path $runtimeRoot "publish"
    $zipPath = Join-Path $runtimeRoot "kuberkynesis-agent-$rid.zip"
    $hashPath = Join-Path $runtimeRoot "kuberkynesis-agent-$rid.sha256.txt"
    $readmePath = Join-Path $publishRoot "README.txt"

    if (Test-Path $runtimeRoot) {
        Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    & dotnet publish $projectPath `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -o $publishRoot

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime '$rid'."
    }

    $publishedLauncherName = if ($rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        "Kuberkynesis.Agent.exe"
    }
    else {
        "Kuberkynesis.Agent"
    }

    $packagedLauncherName = if ($rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        "kuberkynesis-agent.exe"
    }
    else {
        "kuberkynesis-agent"
    }

    $publishedLauncherPath = Join-Path $publishRoot $publishedLauncherName
    $packagedLauncherPath = Join-Path $publishRoot $packagedLauncherName

    if (-not (Test-Path $publishedLauncherPath)) {
        throw "Expected published launcher '$publishedLauncherPath' was not found."
    }

    Copy-Item -LiteralPath $publishedLauncherPath -Destination $packagedLauncherPath -Force

    $launchCommand = if ($rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        "kuberkynesis-agent.exe start --diagnostics"
    }
    else {
        "./kuberkynesis-agent start --diagnostics"
    }

    @"
Kuberkynesis Agent package for $rid

Quick start:
- Ensure your kubeconfig/auth environment is available on this machine.
- Review or edit appsettings.json before first launch if you need non-default origins or ports.
- Run: $launchCommand

Supported startup flags:
- start
- --port <loopback-port>
- --origin <additional-origin>
- --kubeconfig <path>
- --no-browser-open
- --diagnostics
"@ | Set-Content -Path $readmePath -Encoding ascii

    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -Path $hashPath -Encoding ascii

    Write-Host "Published $rid package:" -ForegroundColor Cyan
    Write-Host "  Publish folder: $publishRoot"
    Write-Host "  Zip: $zipPath"
    Write-Host "  SHA256: $hashPath"
}
