# Architect Studio Pro: Release publish (self-contained win-x64, DemoKit included) + Inno Setup compile.
# Usage (from any folder):  powershell -NoProfile -ExecutionPolicy Bypass -File Installer\build-installer.ps1 [-SkipPublish]
# Output: Installer\Output\ArchitectStudioPro-<version>-DeveloperEdition-Setup.exe
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish\win-x64'

if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & dotnet publish (Join-Path $root 'AIArchitectStudio.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $publish 'AIArchitectStudio.exe'))) { throw "No publish output in $publish" }
if (-not (Test-Path (Join-Path $publish 'DemoKit'))) { throw "DemoKit is missing in $publish" }

$candidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }
$iscc = $candidates | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install: winget install --id JRSoftware.InnoSetup -e" }

& $iscc /Qp (Join-Path $PSScriptRoot 'AIArchitectStudio.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter *.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
