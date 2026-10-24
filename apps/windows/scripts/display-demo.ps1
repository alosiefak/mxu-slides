<#
.SYNOPSIS
  Shows MxU Slides on a real second screen (a projector, TV or monitor) and photographs it.

.DESCRIPTION
  Starts the app on a throwaway library (your real library and settings are not used) with its
  audience output on your second display. It then steps through a song, a background and an
  alert with the Local API, and saves a picture of what the screen showed after each step.

  A picture is only taken while MxU Slides' own output window covers that screen, so nothing
  else on the screen is ever captured.

.EXAMPLE
  pwsh apps/windows/scripts/display-demo.ps1
  pwsh apps/windows/scripts/display-demo.ps1 -Display '\\.\DISPLAY2' -Keep
#>
param(
  # The Windows device name of the screen to use; by default the first one that isn't the main screen.
  [string]$Display,
  [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'MxU Slides display demo'),
  [int]$Port = 6990,
  # Leave the app running at the end (close it yourself; the throwaway library stays until then).
  [switch]$Keep
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace MxU -Name Native -MemberDefinition @'
public struct POINT { public int X; public int Y; }
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
public static IntPtr WindowAt(int x, int y) { var p = new POINT(); p.X = x; p.Y = y; return WindowFromPoint(p); }
'@
# Real pixels, whatever the screens' scaling.
[void][MxU.Native]::SetProcessDpiAwarenessContext([IntPtr]-4)

$screen = [System.Windows.Forms.Screen]::AllScreens |
  Where-Object { if ($Display) { $_.DeviceName -eq $Display } else { -not $_.Primary } } | Select-Object -First 1
if (-not $screen) { throw 'No second screen found. Extend your desktop to a projector, TV or monitor (Windows key + P, Extend), then run this again.' }
$bounds = $screen.Bounds
Write-Host "Using $($screen.DeviceName): $($bounds.Width)x$($bounds.Height) at $($bounds.X),$($bounds.Y)"

$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'src\MxU.Slides.Windows\MxU.Slides.Windows.csproj') -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) "MxU Slides display demo library $(Get-Random)"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out, "$sandbox\data", "$sandbox\local" | Out-Null
# The audience on that screen, no stage screen, the Local API on so this script can drive the show.
@{
  AudienceDisplay = $screen.DeviceName; AudienceDisplayName = 'Display demo'
  OutputsOnAtLaunch = $true; LocalApiEnabled = $true; LocalApiPort = $Port
} | ConvertTo-Json | Set-Content -Encoding utf8 "$sandbox\data\settings.json"
$env:MXU_SLIDES_DATA = "$sandbox\data"
$env:MXU_SLIDES_LOCAL = "$sandbox\local"
$exe = Get-ChildItem (Join-Path $root 'src\MxU.Slides.Windows\bin\Debug') -Recurse -Filter MxUSlides.exe | Select-Object -First 1
$app = Start-Process -FilePath $exe.FullName -PassThru

function Stop-Demo {
  if (-not $Keep) {
    if (-not $app.HasExited) { [void]$app.CloseMainWindow(); if (-not $app.WaitForExit(10000)) { Stop-Process -Id $app.Id -Force } }
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
  }
}

try {
  # The app keeps its API key encrypted for your Windows account; read this throwaway library's key.
  $keyFile = "$sandbox\data\local-api-default-key.bin"
  $deadline = (Get-Date).AddSeconds(60)
  while (-not (Test-Path $keyFile) -and (Get-Date) -lt $deadline -and -not $app.HasExited) { Start-Sleep -Milliseconds 250 }
  if (-not (Test-Path $keyFile)) { throw 'The app did not start its Local API.' }
  $secret = powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.Security; [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes('$keyFile'), `$null, 'CurrentUser'))"
  $headers = @{ Authorization = "Bearer $($secret.Trim())" }
  $base = "http://localhost:$Port/v1"
  function Api([string]$method, [string]$path, $body) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Depth 5 } else { $null }
    Invoke-RestMethod -Method $method -Uri "$base$path" -Headers $headers -ContentType 'application/json' -Body $json
  }
  while ((Get-Date) -lt $deadline) { try { [void](Api GET '/status'); break } catch { Start-Sleep -Milliseconds 300 } }

  $shots = 0
  function Shot([string]$name) {
    # Only when the app's own window covers the middle of that screen (never someone else's).
    $hwnd = [MxU.Native]::WindowAt($bounds.X + [int]($bounds.Width / 2), $bounds.Y + [int]($bounds.Height / 2))
    $top = [MxU.Native]::GetAncestor($hwnd, 2)
    [uint32]$owner = 0
    [void][MxU.Native]::GetWindowThreadProcessId($top, [ref]$owner)
    if ($owner -ne $app.Id) { Write-Warning "$($name): MxU Slides isn't covering $($screen.DeviceName), so no picture was taken."; return }
    $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $script:shots++
    $file = Join-Path $Out ('{0:00}-{1}.png' -f $script:shots, $name)
    $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "  saved $file"
  }

  Start-Sleep -Seconds 2
  Shot 'output-on'
  # ForEach-Object unrolls the list Invoke-RestMethod hands back as one object.
  $songs = @(Api GET '/library/presentations' | ForEach-Object { $_ })
  $song = $songs | Where-Object { $_.name -like '*Amazing Grace*' } | Select-Object -First 1
  if (-not $song) { $song = $songs | Select-Object -First 1 }
  if ($song) {
    Write-Host "Song: $($song.name)"
    # Slide 1: a song's first slide is often blank (so the words can come in on cue).
    Api POST '/show/slide' @{ presentationId = $song.id; slideIndex = 1 } | Out-Null
    Start-Sleep -Seconds 2; Shot 'song-first-slide'
    Api POST '/show/advance' @{ steps = 1 } | Out-Null
    Start-Sleep -Milliseconds 150; Shot 'song-transition-middle'
    Start-Sleep -Seconds 2; Shot 'song-second-slide'
  }
  $summary = Api GET '/library'
  $mediaKind = $summary.counts.PSObject.Properties.Name | Where-Object { $_ -like 'media*' } | Select-Object -First 1
  $media = if ($mediaKind) { Api GET "/library/$mediaKind" | ForEach-Object { $_ } | Select-Object -First 1 } else { $null }
  if ($media) {
    Write-Host "Background: $($media.name)"
    Api POST "/media/$($media.id)/fire" $null | Out-Null
    Start-Sleep -Seconds 2; Shot 'background-behind-lyrics'
  }
  # Alerts go to the stage screen unless told otherwise (as on the Mac).
  Api POST '/alerts/fire' @{ message = 'Welcome! The service begins in 5 minutes.'; target = 'audience' } | Out-Null
  Start-Sleep -Seconds 2; Shot 'alert'
  Api POST '/alerts/dismiss' $null | Out-Null
  Api POST '/show/clear-all' $null | Out-Null
  Start-Sleep -Seconds 2; Shot 'cleared'
  Write-Host "Pictures of the screen: $Out"
}
finally {
  Stop-Demo
}
