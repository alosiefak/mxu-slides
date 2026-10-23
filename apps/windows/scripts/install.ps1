<#
.SYNOPSIS
  Installs the published MxU Slides for the current user: copies it to
  %LOCALAPPDATA%\Programs\MxU Slides and adds Start menu and desktop
  shortcuts. Run publish.ps1 first. Your library (songs, services, media)
  lives separately in %APPDATA%\MxU Slides and is never touched.

.EXAMPLE
  pwsh apps/windows/scripts/install.ps1
#>
param([switch]$NoDesktopShortcut)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'artifacts\MxU Slides'
if (-not (Test-Path (Join-Path $source 'MxUSlides.exe'))) { throw "Nothing to install: run scripts/publish.ps1 first." }

$target = Join-Path $env:LOCALAPPDATA 'Programs\MxU Slides'
Get-Process MxUSlides -ErrorAction SilentlyContinue | ForEach-Object {
    throw "MxU Slides is running. Close it, then run this again."
}
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path "$source\*" -Destination $target -Recurse -Force

$exe = Join-Path $target 'MxUSlides.exe'
$shell = New-Object -ComObject WScript.Shell
$places = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $places += [Environment]::GetFolderPath('Desktop') }
foreach ($place in $places) {
    $link = $shell.CreateShortcut((Join-Path $place 'MxU Slides.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$exe,0"
    $link.Description = 'MxU Slides: lyrics, slides and media for church services'
    $link.Save()
}
Write-Host "Installed to $target"
Write-Host "Start it from the Start menu or the desktop: MxU Slides"
