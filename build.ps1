# Builds Bellboard (app + CLI). Requires the .NET 9 SDK on Windows 10/11.
#   .\build.ps1                       framework-dependent build in .\dist\Bellboard (needs the .NET 9 Desktop Runtime)
#   .\build.ps1 -SelfContained        bundle the .NET runtime (no prerequisites)
#   .\build.ps1 -Package              self-contained zip + SHA256 + update feed + winget manifests in .\dist
#   .\build.ps1 -Install              install the build for the current user (PATH, Start menu, Apps & features)
#   .\build.ps1 -Test -Smoke          unit tests before, end-to-end smoke tests (app, remote) after
#   .\build.ps1 -Sign -CertThumbprint <sha1>   Authenticode-sign Bellboard's binaries (needs a code-signing cert)
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "dist\Bellboard"),
    [switch]$SelfContained,
    [switch]$Package,
    [switch]$Install,
    [switch]$Test,
    [switch]$Smoke,
    [switch]$Sign,
    [string]$CertThumbprint,
    [string]$DownloadUrlBase = "https://github.com/omer-yld/bellboard/releases/download"
)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if ($Package) { $SelfContained = $true }
    if ($Test) {
        dotnet test tests\Bellboard.Tests\Bellboard.Tests.csproj -c $Configuration -nologo
        if ($LASTEXITCODE) { throw "unit tests failed" }
    }

    $sc = if ($SelfContained) { "true" } else { "false" }
    foreach ($proj in "src\Bellboard.App\Bellboard.App.csproj", "src\Bellboard.Cli\Bellboard.Cli.csproj", "src\Bellboard.Remote\Bellboard.Remote.csproj") {
        dotnet publish $proj -c $Configuration -o $Output --self-contained $sc -nologo
        if ($LASTEXITCODE) { throw "build failed: $proj" }
    }
    Get-ChildItem $Output -Filter *.pdb | Remove-Item
    Copy-Item packaging\install.ps1, packaging\uninstall.ps1 $Output -Force
    $version = (Get-Item (Join-Path $Output "Bellboard.exe")).VersionInfo.ProductVersion -replace '\+.*$', ''
    Write-Host "built Bellboard $version ($(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })): $Output"

    if ($Sign) {
        if (-not $CertThumbprint) { throw "-Sign needs -CertThumbprint (a code-signing certificate in your store)" }
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)" }
        $files = Get-ChildItem $Output -Include Bellboard.exe, Bellboard.dll, bell.exe, bell.dll, bellboard-remote.exe, bellboard-remote.dll -Recurse
        & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $files.FullName
        if ($LASTEXITCODE) { throw "signing failed" }
    }

    if ($Package) {
        $dist = Split-Path $Output -Parent
        $name = "bellboard-$version-win-x64.zip"
        $zip = Join-Path $dist $name
        Remove-Item $zip -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zip -CompressionLevel Optimal
        $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$sha  $name" | Set-Content -Encoding ASCII "$zip.sha256"
        $url = "$DownloadUrlBase/v$version/$name"
        # Update feed read by `bell update` (set "updateFeed" in config.json to where this file is hosted).
        @{ version = $version; url = $url; sha256 = $sha; published = (Get-Date).ToString("o") } |
            ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dist "latest.json")
        # winget manifests (portable zip). Submit to microsoft/winget-pkgs once the URL is public.
        $wg = Join-Path $dist "winget\$version"
        New-Item -ItemType Directory -Force $wg | Out-Null
        @"
PackageIdentifier: omer-yld.Bellboard
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content -Encoding UTF8 (Join-Path $wg "omer-yld.Bellboard.yaml")
        @"
PackageIdentifier: omer-yld.Bellboard
PackageVersion: $version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: bell.exe
    PortableCommandAlias: bell
  - RelativeFilePath: Bellboard.exe
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $($sha.ToUpperInvariant())
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content -Encoding UTF8 (Join-Path $wg "omer-yld.Bellboard.installer.yaml")
        @"
PackageIdentifier: omer-yld.Bellboard
PackageVersion: $version
PackageLocale: en-US
Publisher: omer-yld
PublisherUrl: https://github.com/omer-yld
PackageUrl: https://github.com/omer-yld/bellboard
LicenseUrl: https://github.com/omer-yld/bellboard/blob/main/LICENSE
PackageName: Bellboard
License: MIT
ShortDescription: Native Windows terminal multiplexer for AI coding agents.
Tags: [terminal, multiplexer, ai, agents, claude-code]
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content -Encoding UTF8 (Join-Path $wg "omer-yld.Bellboard.locale.en-US.yaml")
        Write-Host "package: $zip"
        Write-Host "sha256:  $sha"
        Write-Host "feed:    $(Join-Path $dist 'latest.json')  (url: $url)"
    }

    if ($Smoke) {
        & (Join-Path $PSScriptRoot "tests\smoke.ps1") -BuildDir $Output
        if ($LASTEXITCODE) { throw "smoke test failed" }
        & (Join-Path $PSScriptRoot "tests\remote-smoke.ps1") -BuildDir $Output
        if ($LASTEXITCODE) { throw "remote smoke test failed" }
    }
    if ($Install) {
        & (Join-Path $Output "install.ps1") -Source $Output
    }
}
finally { Pop-Location }
