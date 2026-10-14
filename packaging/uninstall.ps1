# Removes Bellboard installed by install.ps1. Settings and session data are kept unless -RemoveData.
param(
    [string]$InstallDir = $PSScriptRoot,
    [switch]$RemoveData,
    [switch]$KeepRegistration
)
$ErrorActionPreference = "Stop"
$exe = Join-Path $InstallDir "bell.exe"
if (Test-Path $exe) { & $exe quit 2>$null | Out-Null; Start-Sleep -Seconds 2 }
Get-Process Bellboard, bellboard-remote -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -contains $InstallDir) {
    $parts = $userPath -split ";" | Where-Object { $_ -ne $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", ($parts -join ";"), "User")
}
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "Bellboard.lnk") -ErrorAction SilentlyContinue
if (-not $KeepRegistration) { Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Bellboard" -Recurse -ErrorAction SilentlyContinue }
if ($RemoveData) {
    Remove-Item (Join-Path $env:APPDATA "Bellboard") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA "Bellboard") -Recurse -Force -ErrorAction SilentlyContinue
}
# The script lives in the folder it deletes: finish from a temp copy.
$cleanup = Join-Path $env:TEMP "bellboard-uninstall-$PID.cmd"
"@echo off`r`nping -n 2 127.0.0.1 >nul`r`nrmdir /s /q `"$InstallDir`"`r`ndel `"%~f0`"" | Set-Content -Encoding ASCII $cleanup
Start-Process cmd.exe -ArgumentList "/c `"$cleanup`"" -WindowStyle Hidden
Write-Host "Bellboard uninstalled$(if ($RemoveData) { ' (settings and session removed)' } else { ' (settings and session kept)' })"
