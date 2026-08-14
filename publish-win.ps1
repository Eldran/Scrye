# Builds a self-contained Scrye and packs it for sharing. The target machine needs
# nothing installed - no .NET, no runtime.
#
# Run from the repo root:
#     ./publish-win.ps1                      # Windows x64 (the default)
#     ./publish-win.ps1 -Rid linux-x64       # Linux x64, cross-published from Windows
#
# Any RID with a matching <rid>.pubxml under src/Scrye.App/Properties/PublishProfiles
# works; to add one (win-arm64, linux-arm64, osx-arm64), copy the nearest profile and
# change its RuntimeIdentifier and PublishDir.
#
# (The name says "win" for historical reasons - it publishes FROM Windows, not only FOR
# Windows.)
#
# KEEP THIS FILE PURE ASCII. Windows PowerShell 5.1 reads .ps1 as ANSI unless the file
# has a UTF-8 BOM, so a stray en/em dash or ellipsis decodes to mojibake containing a
# double quote, which terminates the enclosing string and produces a cascade of parse
# errors that point nowhere near the real character.

param(
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root       = $PSScriptRoot
$proj       = Join-Path $root 'src/Scrye.App/Scrye.App.csproj'
$profileXml = Join-Path $root "src/Scrye.App/Properties/PublishProfiles/$Rid.pubxml"
$publishDir = Join-Path $root "src/Scrye.App/bin/publish/$Rid"
$distDir    = Join-Path $root 'dist'

if (-not (Test-Path $profileXml)) {
    throw "No publish profile for '$Rid'. Expected $profileXml - copy an existing .pubxml and change its RuntimeIdentifier and PublishDir."
}

# Windows gets a .zip; everything else gets a .tar.gz, which is what a Linux or macOS
# user expects and which does not mangle the layout on extraction.
$isWindowsTarget = $Rid.StartsWith('win')
if ($isWindowsTarget) {
    $archive = Join-Path $distDir "Scrye-$Rid.zip"
} else {
    $archive = Join-Path $distDir "Scrye-$Rid.tar.gz"
}

Write-Host "Publishing Scrye for $Rid, self-contained single-file..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $proj -p:PublishProfile=$Rid
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "Packing..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $archive) { Remove-Item $archive -Force }

if ($isWindowsTarget) {
    # "$publishDir\*" includes the bundled plugins subfolder recursively.
    Compress-Archive -Path "$publishDir\*" -DestinationPath $archive
} else {
    # bsdtar ships with Windows 10/11. -C keeps the archive rooted at the publish folder
    # rather than burying it under the full source path.
    tar -czf $archive -C $publishDir .
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }
}

$sizeMb = [math]::Round(((Get-Item $archive).Length / 1MB), 1)
Write-Host ""
Write-Host "Done. Created $archive - $sizeMb MB." -ForegroundColor Green

if ($isWindowsTarget) {
    Write-Host "Send that zip to your friend; they unzip it and run Scrye.App.exe." -ForegroundColor Green
    Write-Host "First launch may show a Windows SmartScreen warning for an unsigned build:" -ForegroundColor DarkGray
    Write-Host "they click 'More info' then 'Run anyway'." -ForegroundColor DarkGray
} else {
    Write-Host "On the target machine:" -ForegroundColor Green
    Write-Host "    mkdir -p ~/scrye" -ForegroundColor Gray
    Write-Host "    tar -xzf Scrye-$Rid.tar.gz -C ~/scrye" -ForegroundColor Gray
    Write-Host "    chmod +x ~/scrye/Scrye.App" -ForegroundColor Gray
    Write-Host "    ~/scrye/Scrye.App" -ForegroundColor Gray
    Write-Host ""
    Write-Host "The chmod is not optional: NTFS has no executable bit, so nothing packed" -ForegroundColor DarkGray
    Write-Host "on Windows carries one. Any desktop distro already has the libraries" -ForegroundColor DarkGray
    Write-Host "Avalonia needs - libx11-6, libice6, libsm6, libfontconfig1 - but a minimal" -ForegroundColor DarkGray
    Write-Host "or headless image needs them installed, plus a monospaced font." -ForegroundColor DarkGray
}
