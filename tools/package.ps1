# Builds Wisp's installer and portable zip.
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1            # version from Wisp.csproj
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Version 0.2.0
#
# Output (artifacts\releases):
#   Wisp-win-Setup.exe     per-user installer: Start menu + desktop shortcut, listed in Settings > Apps
#   Wisp-win-Portable.zip  extract anywhere and run Wisp.exe
#   *.nupkg / releases.*   update packages (host these to enable auto-update later)
#
# Needs the .NET 10 SDK and Velopack's CLI:  dotnet tool install -g vpk
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\Wisp\Wisp.csproj'
$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'artifacts\releases'
$env:PATH = "C:\Program Files\dotnet;$env:USERPROFILE\.dotnet\tools;$env:PATH"

if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
Write-Host "Packaging Wisp $Version" -ForegroundColor Cyan

# Self-contained: target PCs don't need .NET installed. ReadyToRun: faster startup.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Native speech-to-text libraries ship for every OS/CPU; keep only Windows x64.
Get-ChildItem (Join-Path $publish 'runtimes') -Recurse -Directory |
    Where-Object { $_.Name -match '^(linux|osx|android|ios|maccatalyst|tvos|browser|win-x86|win-arm64)' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

vpk pack --packId Wisp --packVersion $Version --packDir $publish --mainExe Wisp.exe `
    --packTitle Wisp --packAuthors Wisp --icon (Join-Path $root 'src\Wisp\wisp.ico') `
    --shortcuts 'StartMenuRoot,Desktop' --outputDir $releases --yes
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $releases | Where-Object { $_.Name -like "*Setup.exe" -or $_.Name -like "*Portable.zip" } |
    ForEach-Object { "{0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
