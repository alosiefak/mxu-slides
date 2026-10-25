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
$solution = Join-Path (Split-Path -Parent $PSScriptRoot) 'MxUSlides.slnx'
# A few projects at a time: the tests that measure real time fail at random when every project
# runs at once (HOW-IT-WORKS section 26).
Use-TestLock 'unit tests' { dotnet test --solution $solution --max-parallel-test-modules $Parallel @Rest }
exit $LASTEXITCODE
