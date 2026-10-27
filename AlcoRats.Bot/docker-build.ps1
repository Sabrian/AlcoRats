# Build Release Docker image for AlcoRats.Bot
# Usage (from anywhere):
#   .\AlcoRats.Bot\docker-build.ps1
#   .\AlcoRats.Bot\docker-build.ps1 -Tag alcorats-bot:1.0.0

[CmdletBinding()]
param(
    [string]$Tag = 'alcorats-bot:latest'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$dockerfile = Join-Path $PSScriptRoot 'Dockerfile'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error 'docker is not available in PATH. Install Docker Desktop and ensure docker.exe is on PATH.'
    exit 1
}

docker info 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error @"
Docker daemon is not running (cannot reach npipe:////./pipe/dockerDesktopLinuxEngine).

Fix:
  1. Start Docker Desktop and wait until it says 'Engine running'
  2. In Docker Desktop: Settings -> General -> ensure 'Use the WSL 2 based engine' is enabled if you use WSL
  3. Retry: .\docker-build.ps1
"@
    exit 1
}

Write-Host "Repo root:  $repoRoot"
Write-Host "Dockerfile: $dockerfile"
Write-Host "Tag:        $Tag"
Write-Host ''

Push-Location $repoRoot
try {
    docker build `
        -f $dockerfile `
        -t $Tag `
        --build-arg BUILD_CONFIGURATION=Release `
        .

    if ($LASTEXITCODE -ne 0) {
        Write-Error "docker build failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "OK: image built as $Tag"
Write-Host 'Run:  .\AlcoRats.Bot\docker-run.ps1'
