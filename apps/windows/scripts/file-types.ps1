<#
.SYNOPSIS
  Tells Windows, for the current user only, that MxU Slides opens .mxuslides
  presentations and .mxubackup library backups (double-click opens them; a file
  opened while MxU Slides is running goes to the copy already open), and lists
  MxU Slides under "Open with" for ProPresenter (.pro) and PowerPoint (.pptx)
  files without taking those over. install.ps1 runs it with -Action Register,
  uninstall.ps1 with -Action Unregister.

  Everything goes under HKEY_CURRENT_USER\Software\Classes: no administrator
  rights, nothing for other people who use this PC. Unregister only removes
  what points at MxU Slides: a type another app has taken over since is left.

.PARAMETER DryRun
  Prints each change instead of making it.

.PARAMETER ClassesKey
  Where under HKEY_CURRENT_USER the classes live (Software\Classes). Only
  changed to try the script out on a throwaway key.

.EXAMPLE
  pwsh apps/windows/scripts/file-types.ps1 -Action Register -Exe "$env:LOCALAPPDATA\Programs\MxU Slides\MxUSlides.exe" -DryRun
#>
param(
    [Parameter(Mandatory)][ValidateSet('Register', 'Unregister')][string]$Action,
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'Programs\MxU Slides\MxUSlides.exe'),
    [string]$ClassesKey = 'Software\Classes',
    [switch]$DryRun,
    [switch]$NoNotify
)
$ErrorActionPreference = 'Stop'

# The types MxU Slides owns (double-click opens them) and the ones it only offers to open.
$owned = @(
    @{ Extension = '.mxuslides'; ProgId = 'MxUSlides.Presentation'; Name = 'MxU Slides presentation'; Mime = 'application/vnd.mxu.slides' }
    @{ Extension = '.mxubackup'; ProgId = 'MxUSlides.Backup'; Name = 'MxU Slides library backup'; Mime = $null }
)
$offered = @('.pro', '.pptx')
$importProgId = 'MxUSlides.Import'
$appKey = 'Applications\MxUSlides.exe'

$hkcu = [Microsoft.Win32.Registry]::CurrentUser

# Each change is listed in a dry run (and with -Verbose).
function Show([string]$text) { if ($DryRun) { Write-Host "would $text" } else { Write-Verbose $text } }

function Set-Value([string]$key, [string]$name, [string]$value) {
    $label = if ($name) { $name } else { '(Default)' }
    Show "set HKCU\$ClassesKey\$key  $label = $value"
    if ($DryRun) { return }
    $k = $hkcu.CreateSubKey("$ClassesKey\$key", $true)
    try { $k.SetValue($name, $value, [Microsoft.Win32.RegistryValueKind]::String) } finally { $k.Dispose() }
}

function Get-Value([string]$key, [string]$name) {
    $k = $hkcu.OpenSubKey("$ClassesKey\$key")
    if (-not $k) { return $null }
    try { return $k.GetValue($name) } finally { $k.Dispose() }
}

function Remove-Key([string]$key) {
    if (-not $hkcu.OpenSubKey("$ClassesKey\$key")) { return }
    Show "remove HKCU\$ClassesKey\$key"
    if (-not $DryRun) { $hkcu.DeleteSubKeyTree("$ClassesKey\$key", $false) }
}

function Remove-Value([string]$key, [string]$name) {
    $k = $hkcu.OpenSubKey("$ClassesKey\$key", $true)
    if (-not $k) { return }
    try {
        if ($null -eq $k.GetValue($name)) { return }
        Show "remove HKCU\$ClassesKey\$key  $name"
        if (-not $DryRun) { $k.DeleteValue($name, $false) }
    } finally { $k.Dispose() }
}

function Set-Command([string]$progId, [string]$verbName) {
    Set-Value "$progId\shell\open" '' $verbName
    Set-Value "$progId\shell\open\command" '' "`"$Exe`" `"%1`""
}

if ($Action -eq 'Register') {
    if (-not $DryRun -and -not (Test-Path $Exe)) { throw "MxUSlides.exe isn't at $Exe" }
    foreach ($type in $owned) {
        Set-Value $type.ProgId '' $type.Name
        Set-Value $type.ProgId 'FriendlyTypeName' $type.Name
        Set-Value "$($type.ProgId)\DefaultIcon" '' "`"$Exe`",0"
        Set-Command $type.ProgId 'Open with MxU Slides'
        Set-Value $type.Extension '' $type.ProgId
        Set-Value "$($type.Extension)\OpenWithProgids" $type.ProgId ''
        if ($type.Mime) { Set-Value $type.Extension 'Content Type' $type.Mime }
    }
    # "Open with ›" for ProPresenter and PowerPoint files: listed, never made the default.
    Set-Value $importProgId '' 'Import into MxU Slides'
    Set-Value "$importProgId\DefaultIcon" '' "`"$Exe`",0"
    Set-Command $importProgId 'Import into MxU Slides'
    foreach ($extension in $offered) { Set-Value "$extension\OpenWithProgids" $importProgId '' }
    Set-Value $appKey 'FriendlyAppName' 'MxU Slides'
    Set-Command $appKey 'Open with MxU Slides'
    foreach ($extension in ($owned.Extension + $offered)) { Set-Value "$appKey\SupportedTypes" $extension '' }
}
else {
    foreach ($type in $owned) {
        # The extension only while it still points at MxU Slides (another app may have taken it since).
        if ((Get-Value $type.Extension '') -eq $type.ProgId) { Remove-Key $type.Extension }
        else { Remove-Value "$($type.Extension)\OpenWithProgids" $type.ProgId }
        Remove-Key $type.ProgId
    }
    foreach ($extension in $offered) { Remove-Value "$extension\OpenWithProgids" $importProgId }
    Remove-Key $importProgId
    Remove-Key $appKey
}

if ($DryRun) { return }
# Explorer reads the new icons and "Open with" lists straight away.
if (-not $NoNotify) {
    Add-Type -Namespace MxU -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);'
    [MxU.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
}
Write-Host $(if ($Action -eq 'Register') { "File types: .mxuslides and .mxubackup open in MxU Slides; .pro and .pptx list it under Open with." }
    else { "File types: MxU Slides' file types are removed." })
