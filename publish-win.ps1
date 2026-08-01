# Builds a self-contained Windows x64 build of Scrye and zips it for sharing.
#
# Run from the repo root:
#     ./publish-win.ps1
#
# Your friend needs nothing installed - they unzip and run Scrye.App.exe.
# For another target such as win-arm64, copy win-x64.pubxml to <rid>.pubxml then run:
#     ./publish-win.ps1 -Rid win-arm64

param(
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root       = $PSScriptRoot
$proj       = Join-Path $root 'src/Scrye.App/Scrye.App.csproj'
$publishDir = Join-Path $root "src/Scrye.App/bin/publish/$Rid"
$distDir    = Join-Path $root 'dist'
$zipPath    = Join-Path $distDir "Scrye-$Rid.zip"

Write-Host "Publishing Scrye for $Rid, self-contained single-file..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $proj -p:PublishProfile=$Rid
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "Zipping..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# "$publishDir\*" includes the bundled plugins subfolder recursively.
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath

$sizeMb = [math]::Round(((Get-Item $zipPath).Length / 1MB), 1)
Write-Host ""
Write-Host "Done. Created $zipPath - $sizeMb MB." -ForegroundColor Green
Write-Host "Send that zip to your friend; they unzip it and run Scrye.App.exe." -ForegroundColor Green
Write-Host "First launch may show a Windows SmartScreen warning for an unsigned build:" -ForegroundColor DarkGray
Write-Host "they click 'More info' then 'Run anyway'." -ForegroundColor DarkGray
