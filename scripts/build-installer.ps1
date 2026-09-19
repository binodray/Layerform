<#
.SYNOPSIS
    Builds the Layer Form installer and the update feed for a release.

.DESCRIPTION
    1. Publishes a self-contained Release build of the app for win-x64.
    2. Compiles installer\LayerForm.iss with Inno Setup 6 into artifacts\installer.
    3. Writes site\update.json, which installed copies of Layer Form read to find updates.
       Its download URL points at the installer attached to the matching GitHub release.

    The version comes from Directory.Build.props; release notes come from that version's
    section in CHANGELOG.md.

.EXAMPLE
    .\scripts\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
if (-not (Test-Path $Iscc)) { throw "Inno Setup 6 not found at $Iscc. Install it from https://jrsoftware.org/isdl.php or pass -Iscc." }
Write-Host "Layer Form $version" -ForegroundColor Cyan

$publish = Join-Path $root 'artifacts\publish\win-x64'
$installerDir = Join-Path $root 'artifacts\installer'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host 'Publishing...'
& "$PSScriptRoot\dotnet.ps1" publish src/Compositor.App/Compositor.App.csproj `
    -c Release -r win-x64 -p:Platform=x64 --self-contained `
    -p:DebugType=none -p:DebugSymbols=false -o $publish -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
foreach ($required in 'LayerForm.exe', 'LayerForm.pri', 'App.xbf') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Publish output is missing $required; the installed app would not start." }
}

Write-Host 'Compiling installer...'
& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutputDir=$installerDir" installer\LayerForm.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setupName = "LayerForm-Setup-$version.exe"
$setup = Join-Path $installerDir $setupName
$sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $setup).Length

# Release notes: the lines under "## [<version>]" up to the next "## " heading.
$notes = ''
$capture = $false
foreach ($line in Get-Content CHANGELOG.md -Encoding utf8) {
    if ($line -match '^## ') {
        if ($capture) { break }
        $capture = $line -match "^## \[$([regex]::Escape($version))\]"
        continue
    }
    if ($capture) { $notes += $line + "`n" }
}

$feed = [ordered]@{
    version  = $version
    url      = "https://github.com/binodray/Layerform/releases/download/v$version/$setupName"
    sha256   = $sha256
    size     = $size
    released = (Get-Date -Format 'yyyy-MM-dd')
    notes    = $notes.Trim()
}
$feedPath = Join-Path $root 'site\update.json'
[IO.File]::WriteAllText($feedPath, ($feed | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))

# The website as one zip for the hosting file manager. tar writes forward-slash paths,
# which Linux hosts need; PowerShell 5's Compress-Archive writes backslashes.
$siteZip = Join-Path $root 'artifacts\layerform-site.zip'
if (Test-Path $siteZip) { Remove-Item $siteZip -Force }
Push-Location (Join-Path $root 'site')
try { tar.exe -a -c -f $siteZip .htaccess index.html update.json favicon.png assets }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Could not create the website zip.' }

Write-Host ''
Write-Host "Installer : $setup ($([math]::Round($size / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "SHA-256   : $sha256"
Write-Host "Feed      : $feedPath"
Write-Host "Website   : $siteZip"
Write-Host ''
Write-Host 'To publish:'
Write-Host "  1. Create GitHub release v$version and attach $setupName."
Write-Host '  2. Upload update.json (or the whole website zip) to layerform.hastamev.com.'
