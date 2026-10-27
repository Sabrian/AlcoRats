# Run AlcoRats.Bot Release container (foreground by default — console logs stream here)
# Usage:
#   .\AlcoRats.Bot\docker-run.ps1
#   .\AlcoRats.Bot\docker-run.ps1 -Detach
#   .\AlcoRats.Bot\docker-run.ps1 -Tag alcorats-bot:latest -Name alcorats-bot
#   .\AlcoRats.Bot\docker-run.ps1 -ConfigPath .\configuration.json
#   .\AlcoRats.Bot\docker-run.ps1 -Rebuild

[CmdletBinding()]
param(
    [string]$Tag = 'alcorats-bot:latest',
    [string]$Name = 'alcorats-bot',
    [string]$DataDir = '',
    [string]$ConfigPath = '',
    [switch]$Detach,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error 'docker is not available in PATH. Install Docker Desktop and ensure docker.exe is on PATH.'
    exit 1
}

docker info 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error @"
Docker daemon is not running (cannot reach dockerDesktopLinuxEngine).

Fix: start Docker Desktop, wait until Engine is running, then retry.
"@
    exit 1
}

if ($Rebuild) {
    & (Join-Path $PSScriptRoot 'docker-build.ps1') -Tag $Tag
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $PSScriptRoot 'data'
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'chromium-profile') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

$existing = docker ps -a --filter "name=^/${Name}$" --format '{{.ID}}'
if ($existing) {
    Write-Host "Removing existing container: $Name"
    docker rm -f $Name | Out-Null
}

$dockerArgs = @(
    'run'
    '--name', $Name
    '--init'
    '--shm-size', '1g'
    '--restart', 'unless-stopped'
    '-e', 'ChromiumExecutablePath=/opt/google/chrome/chrome'
    '-e', 'JitsiUserDataDir=/data/chromium-profile'
    '-e', 'JitsiRemoteDebuggingPort=9333'
    '-e', 'JitsiHeadless=true'
    '-e', 'LiteDbPath=/data/data.db'
    '-e', 'LogDirectory=/data/logs'
    '-p', '9333:9333'
    '-v', "${DataDir}:/data"
)

if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $configFull = (Resolve-Path $ConfigPath).Path
    $dockerArgs += @('-v', "${configFull}:/app/configuration.json:ro")
}
elseif (Test-Path (Join-Path $PSScriptRoot 'configuration.json')) {
    $configFull = (Resolve-Path (Join-Path $PSScriptRoot 'configuration.json')).Path
    $dockerArgs += @('-v', "${configFull}:/app/configuration.json:ro")
}
elseif (Test-Path (Join-Path $DataDir 'configuration.json')) {
    $configFull = (Resolve-Path (Join-Path $DataDir 'configuration.json')).Path
    $dockerArgs += @('-v', "${configFull}:/app/configuration.json:ro")
}

if ($Detach) {
    $dockerArgs += '-d'
}
else {
    $dockerArgs += '-it'
}

$dockerArgs += $Tag

Write-Host "Image:     $Tag"
Write-Host "Container: $Name"
Write-Host "Data dir:  $DataDir"
Write-Host ''

docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker run failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if ($Detach) {
    Write-Host ''
    Write-Host "Started in background. Logs: docker logs -f $Name"
}
