<#
.SYNOPSIS
  Builds a self-contained release of MxU Slides for Windows (no .NET install
  needed on the target PC) into apps/windows/artifacts/MxU Slides, plus a zip.

.EXAMPLE
  pwsh apps/windows/scripts/publish.ps1
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\MxU Slides'
$zip = Join-Path $root "artifacts\MxU-Slides-Windows-$Runtime.zip"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'src\MxU.Slides.Windows\MxU.Slides.Windows.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true `
    -p:DebugType=embedded -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root '..\..\LICENSE.md') $out
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Published to $out"
Write-Host "Zip: $zip"
