# Sends MEDIA_PLAY_PAUSE (keycode 85) to a connected Android device/emulator via adb.
$ErrorActionPreference = "Stop"

function Find-Adb {
    $fromPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $candidates = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ANDROID_HOME\platform-tools\adb.exe",
        "$env:ANDROID_SDK_ROOT\platform-tools\adb.exe"
    )

    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            return $path
        }
    }

    throw "adb.exe not found. Install Android platform-tools or add adb to PATH."
}

$adb = Find-Adb
$serial = $args | Where-Object { $_ -and $_ -notlike "-*" } | Select-Object -First 1

Write-Host "Using adb: $adb"

$devices = & $adb devices | Select-Object -Skip 1 |
    Where-Object { $_ -match "\tdevice$" } |
    ForEach-Object { ($_ -split "\t")[0] }

if (-not $devices) {
    throw "No connected Android devices/emulators found."
}

if (-not $serial) {
    if ($devices.Count -gt 1) {
        Write-Host "Multiple devices found:"
        $devices | ForEach-Object { Write-Host "  $_" }
        throw "Pass a serial, e.g.: .\send-play-pause.ps1 emulator-5554"
    }
    $serial = $devices | Select-Object -First 1
}

Write-Host "Sending KEYCODE_MEDIA_PLAY_PAUSE (85) to $serial ..."
& $adb -s $serial shell input keyevent 85
if ($LASTEXITCODE -ne 0) {
    throw "adb failed with exit code $LASTEXITCODE"
}

Write-Host "Done."
