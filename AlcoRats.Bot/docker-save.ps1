# Save alcorats-bot image to AlcoRats.Bot/docker/
# Usage:
#   .\AlcoRats.Bot\docker-save.ps1
#   .\AlcoRats.Bot\docker-save.ps1 -Tag alcorats-bot:1.0.0

[CmdletBinding()]
param(
    [string]$Tag = 'alcorats-bot:latest'
)

$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot 'docker'
$safeName = ($Tag -replace '[:/\\]', '-')
$outFile = Join-Path $outDir "$safeName.tar"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error 'docker is not available in PATH. Install Docker Desktop and ensure docker.exe is on PATH.'
    exit 1
}

docker info 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error @"
Docker daemon is not running.

Fix: start Docker Desktop, wait until Engine is running, then retry.
"@
    exit 1
}

docker image inspect $Tag 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Image '$Tag' not found. Build it first: .\docker-build.ps1"
    exit 1
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Tag:    $Tag"
Write-Host "Output: $outFile"
Write-Host ''

docker save -o $outFile $Tag
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker save failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$sizeMb = [math]::Round((Get-Item $outFile).Length / 1MB, 1)
Write-Host ''
Write-Host "OK: saved $Tag -> $outFile ($sizeMb MB)"
Write-Host 'Load: docker load -i' $outFile
