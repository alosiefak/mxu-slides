<#
.SYNOPSIS
  One heavy test run at a time on this PC. Dot-source it, then wrap the run:

    . "$PSScriptRoot\test-lock.ps1"
    Use-TestLock 'self-test' { ...the run... }

  The self-test and the full unit suite measure real time (a 1.2 s slide timer, sound in step
  with a video, a recording at 30 pictures a second). Two of them at once on one PC slow each
  other down and fail checks that pass on their own. So each waits here, on a lock that every
  copy of these scripts shares (a named mutex), until the other has finished: from any folder
  or worktree, in any window. A run that crashes releases it; Windows hands it on.
#>

function Use-TestLock([string]$What, [scriptblock]$Run) {
  $lock = [Threading.Mutex]::new($false, 'Local\MxU Slides heavy tests')
  try {
    $held = $false
    try { $held = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) {
      Write-Host "Waiting for another test run on this PC to finish before the $What..."
      try { $null = $lock.WaitOne() } catch [Threading.AbandonedMutexException] { }
    }
    & $Run
  }
  finally {
    try { $lock.ReleaseMutex() } catch [ApplicationException] { }
    $lock.Dispose()
  }
}
