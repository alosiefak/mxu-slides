<#
.SYNOPSIS
  Builds MxU Slides and runs its built-in self-test against a throwaway
  library (your real library is not used). Prints the report; pictures of
  every step are saved alongside it.

  -Only runs the basics plus just the sections named (their names are in
  SelfTest.cs: Looks, Scripture, Library…), to check one area quickly.
  Only one self-test or full unit run goes at a time on a PC (test-lock.ps1):
  a second one waits for the first.

.EXAMPLE
  pwsh apps/windows/scripts/self-test.ps1
  pwsh apps/windows/scripts/self-test.ps1 -Only Looks,Scripture -Out "$env:TEMP\MxU looks"
#>
param([string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'MxU Slides self-test'), [string[]]$Only = @())
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\test-lock.ps1"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MxU.Slides.Windows\MxU.Slides.Windows.csproj'
dotnet build $project -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if ($Only.Count -gt 0) { $env:MXU_SLIDES_SELFTEST_ONLY = $Only -join ',' }
else { Remove-Item Env:MXU_SLIDES_SELFTEST_ONLY -ErrorAction SilentlyContinue }

Use-TestLock 'self-test' {
  $sandbox = Join-Path ([IO.Path]::GetTempPath()) "MxU Slides self-test library $(Get-Random)"
  foreach ($dir in $Out) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
  New-Item -ItemType Directory -Force $Out, "$sandbox\data", "$sandbox\local" | Out-Null
  $env:MXU_SLIDES_DATA = "$sandbox\data"
  $env:MXU_SLIDES_LOCAL = "$sandbox\local"
  $exe = Get-ChildItem (Join-Path $root 'src\MxU.Slides.Windows\bin\Debug') -Recurse -Filter MxUSlides.exe | Select-Object -First 1
  $process = Start-Process -FilePath $exe.FullName -ArgumentList '--self-test', "`"$Out`"" -PassThru
  # Up to 45 minutes: the full run takes about 10 on a laptop, but about 25 on GitHub's test machine,
  # which has no graphics card (WPF draws every window in software there).
  $finished = $process.WaitForExit(2700000)
  if (-not $finished) { Stop-Process -Id $process.Id -Force }
  # Keep the app's log beside the pictures (CI uploads the folder), then throw the library away.
  Copy-Item -Recurse -Force "$sandbox\local\Logs" (Join-Path $Out 'Logs') -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
  if (-not $finished) {
    # What it got through before it stopped (written as it goes), and where it stuck.
    $partial = Join-Path $Out 'report.partial.txt'
    if (Test-Path $partial) { Get-Content $partial | Select-Object -Last 15 }
    throw 'Self-test timed out'
  }
}

$report = Join-Path $Out 'report.txt'
if (-not (Test-Path $report)) { throw 'The self-test did not write a report' }
Get-Content $report
Write-Host "Pictures of each step: $Out"
if (-not (Select-String -Path $report -Pattern 'RESULT: PASS' -Quiet)) { exit 1 }
