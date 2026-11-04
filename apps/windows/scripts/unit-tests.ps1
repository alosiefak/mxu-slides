<#
.SYNOPSIS
  Runs every unit test, three test projects at a time, once no other heavy test run on this
  PC is going (see test-lock.ps1). Extra arguments go to dotnet test.

.EXAMPLE
  pwsh apps/windows/scripts/unit-tests.ps1
  pwsh apps/windows/scripts/unit-tests.ps1 --no-build
#>
param([int]$Parallel = 3, [Parameter(ValueFromRemainingArguments)][string[]]$Rest = @())
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\test-lock.ps1"
$windows = Split-Path -Parent $PSScriptRoot
# A few projects at a time: the tests that measure real time fail at random when every project
# runs at once (HOW-IT-WORKS section 26). It runs from apps/windows, whose global.json picks the
# Microsoft Testing Platform runner: from anywhere else dotnet test falls back to the old runner,
# which doesn't know --solution.
Use-TestLock 'unit tests' {
  Push-Location $windows
  try { dotnet test --solution MxUSlides.slnx --max-parallel-test-modules $Parallel @Rest }
  finally { Pop-Location }
}
exit $LASTEXITCODE
