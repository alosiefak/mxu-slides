<#
.SYNOPSIS
  Takes MxU Slides off this PC for the current user: the program in
  %LOCALAPPDATA%\Programs\MxU Slides, its Start menu and desktop shortcuts,
  and its file types (file-types.ps1 -Action Unregister). Your library
  (songs, services, media in %APPDATA%\MxU Slides, or wherever Settings
  points) and your backups are never touched.

.PARAMETER DryRun
  Says what it would do, and does nothing.

.EXAMPLE
  pwsh apps/windows/scripts/uninstall.ps1
#>
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\MxU Slides'
if (-not $DryRun) {
    Get-Process MxUSlides -ErrorAction SilentlyContinue | ForEach-Object {
        throw "MxU Slides is running. Close it, then run this again."
    }
}

& (Join-Path $PSScriptRoot 'file-types.ps1') -Action Unregister -Exe (Join-Path $target 'MxUSlides.exe') -DryRun:$DryRun

$links = @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) |
    ForEach-Object { Join-Path $_ 'MxU Slides.lnk' } | Where-Object { Test-Path $_ }
foreach ($link in $links) {
    if ($DryRun) { Write-Host "would remove $link" } else { Remove-Item -LiteralPath $link -Force }
}
if (Test-Path (Join-Path $target 'MxUSlides.exe')) {
    if ($DryRun) { Write-Host "would remove $target" } else { Remove-Item -LiteralPath $target -Recurse -Force }
}
Write-Host $(if ($DryRun) { "Nothing changed (dry run)." } else { "MxU Slides is uninstalled. Your library and backups are where they were." })
