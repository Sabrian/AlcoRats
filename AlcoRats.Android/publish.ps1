# Release APK for physical arm64 devices.
# -m:1 avoids flaky out-of-proc MSBuild task-host crashes during Android packaging.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Stale nested obj (e.g. AlcoRats.Bot) can leak into the shared project as duplicate AssemblyInfo.
dotnet clean (Join-Path $PSScriptRoot 'AlcoRats.Android.csproj') -c Release -v:q | Out-Null
Remove-Item -Recurse -Force (Join-Path $root 'obj'), (Join-Path $root 'bin') -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $PSScriptRoot 'obj'), (Join-Path $PSScriptRoot 'bin') -ErrorAction SilentlyContinue

dotnet publish AlcoRats.Android.csproj `
  -c Release `
  -f net10.0-android `
  -m:1 `
  -p:RuntimeIdentifier=android-arm64 `
  -p:AndroidPackageFormat=apk `
  -p:PublishTrimmed=false `
  -p:RunAOTCompilation=false `
  -p:AndroidLinkMode=None

if ($LASTEXITCODE -ne 0) {
  Write-Error "publish failed with exit code $LASTEXITCODE"
  exit $LASTEXITCODE
}

$apk = Get-ChildItem -Path 'bin\Release\net10.0-android' -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if ($apk) {
  Write-Host "APK: $($apk.FullName)"
} else {
  Write-Host 'Publish finished (look under bin\Release\net10.0-android for the APK).'
}
