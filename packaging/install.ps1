# Bellboard per-user installer: no admin rights needed.
#   .\install.ps1                         install from this folder to %LOCALAPPDATA%\Programs\Bellboard
#   .\install.ps1 -InstallDir D:\Tools\Bellboard -NoPath -NoShortcut -NoRegister   (sandboxed / portable)
param(
    [string]$Source = $PSScriptRoot,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\Bellboard"),
    [switch]$NoPath,
    [switch]$NoShortcut,
    [switch]$NoRegister,
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$Source = (Resolve-Path $Source).Path
if (-not (Test-Path (Join-Path $Source "Bellboard.exe"))) { throw "Bellboard.exe not found in $Source" }

# A running Bellboard from this folder locks its files.
$running = Get-Process Bellboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "Bellboard is running from $InstallDir; quit it first (bell quit)" }

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Get-ChildItem $Source | Where-Object { $_.Name -notin @("install.ps1") } | Copy-Item -Destination $InstallDir -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $InstallDir -Force -ErrorAction SilentlyContinue
$version = (Get-Item (Join-Path $InstallDir "Bellboard.exe")).VersionInfo.ProductVersion
Write-Host "installed Bellboard $version to $InstallDir"

if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if (($userPath -split ";") -notcontains $InstallDir) {
        [Environment]::SetEnvironmentVariable("Path", ($userPath.TrimEnd(";") + ";" + $InstallDir), "User")
        Write-Host "added $InstallDir to your user PATH (open a new terminal to use 'bell')"
    }
}
if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "Bellboard.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $InstallDir "Bellboard.exe"
    $s.WorkingDirectory = $InstallDir
    $s.Description = "Bellboard - terminal multiplexer for AI coding agents"
    $s.Save()
    Write-Host "Start menu: Bellboard"
}
if (-not $NoRegister) {
    # Shows up in Settings > Apps > Installed apps, with a working Uninstall button.
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Bellboard"
    New-Item -Force $key | Out-Null
    $size = [int]((Get-ChildItem $InstallDir -Recurse | Measure-Object Length -Sum).Sum / 1KB)
    Set-ItemProperty $key DisplayName "Bellboard"
    Set-ItemProperty $key DisplayVersion $version
    Set-ItemProperty $key Publisher "Bellboard"
    Set-ItemProperty $key DisplayIcon (Join-Path $InstallDir "Bellboard.exe")
    Set-ItemProperty $key InstallLocation $InstallDir
    Set-ItemProperty $key EstimatedSize $size -Type DWord
    Set-ItemProperty $key NoModify 1 -Type DWord
    Set-ItemProperty $key NoRepair 1 -Type DWord
    Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
}
# Bellboard was called wmux before 0.3. Settings and the session move over on first start; the old
# program stays until its owner removes it, because agent hooks may still point at it.
$legacy = Join-Path $env:LOCALAPPDATA "Programs\wmux"
if (Test-Path (Join-Path $legacy "uninstall.ps1")) {
    Write-Host ""
    Write-Host "An older wmux install is in $legacy. To switch over:"
    Write-Host "  bell hooks install; bell mcp install; bell skill install   (replace the wmux entries)"
    Write-Host "  & '$legacy\uninstall.ps1'                                 (then remove wmux)"
}
if ($Launch) { Start-Process (Join-Path $InstallDir "Bellboard.exe") }
