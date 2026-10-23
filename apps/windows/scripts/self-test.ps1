<#
.SYNOPSIS
  Builds MxU Slides and runs its built-in self-test against a throwaway
  library (your real library is not used). Prints the report; pictures of
  every step are saved alongside it.

.EXAMPLE
  pwsh apps/windows/scripts/self-test.ps1
#>
param([string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'MxU Slides self-test'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MxU.Slides.Windows\MxU.Slides.Windows.csproj'
dotnet build $project -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) "MxU Slides self-test library $(Get-Random)"
foreach ($dir in $Out) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
New-Item -ItemType Directory -Force $Out, "$sandbox\data", "$sandbox\local" | Out-Null
$env:MXU_SLIDES_DATA = "$sandbox\data"
$env:MXU_SLIDES_LOCAL = "$sandbox\local"
$exe = Get-ChildItem (Join-Path $root 'src\MxU.Slides.Windows\bin\Debug') -Recurse -Filter MxUSlides.exe | Select-Object -First 1
$process = Start-Process -FilePath $exe.FullName -ArgumentList '--self-test', "`"$Out`"" -PassThru
if (-not $process.WaitForExit(180000)) { Stop-Process -Id $process.Id -Force; throw 'Self-test timed out' }
# Keep the app's log beside the pictures (CI uploads the folder), then throw the library away.
Copy-Item -Recurse -Force "$sandbox\local\Logs" (Join-Path $Out 'Logs') -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue

$report = Join-Path $Out 'report.txt'
if (-not (Test-Path $report)) { throw 'The self-test did not write a report' }
Get-Content $report
Write-Host "Pictures of each step: $Out"
if (-not (Select-String -Path $report -Pattern 'RESULT: PASS' -Quiet)) { exit 1 }
