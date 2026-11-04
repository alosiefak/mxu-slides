<#
.SYNOPSIS
  Builds automerge.dll, the Automerge core that library sync uses, from source.

.DESCRIPTION
  The Mac keeps its library as Automerge documents, through automerge-swift 0.7.2, which builds
  the `automerge` Rust crate 0.7.2. This script builds that same crate for Windows, wrapped in the
  small C interface in apps/windows/native/automerge-ffi (src/lib.rs), and copies the result to
  src/MxU.Slides.Sync/Native/automerge.dll, next to its licence notices.

  The DLL is committed, so building the app (and GitHub's CI) doesn't need Rust. Run this script
  only after changing the Rust code or its versions; then run the Sync unit tests and commit the
  DLL, AUTOMERGE-NOTICES.txt and Cargo.lock together.

  Needs Rust's stable MSVC toolchain (rustup, from rust-lang.org) and the Visual Studio C++ build
  tools. Every crate version is pinned by Cargo.lock (--locked), which started from
  automerge-swift 0.7.2's own lock file, so the Windows core matches the Mac's.

.EXAMPLE
  pwsh apps/windows/tools/build-automerge.ps1
#>
$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$crate = Join-Path $windows 'native\automerge-ffi'
$native = Join-Path $windows 'src\MxU.Slides.Sync\Native'

# rustup puts cargo in %USERPROFILE%\.cargo\bin; a shell opened before Rust was installed
# doesn't have that on its PATH yet.
$cargo = (Get-Command cargo -ErrorAction SilentlyContinue)?.Source
if (-not $cargo) { $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
if (-not (Test-Path $cargo)) { throw 'cargo was not found. Install Rust from https://rustup.rs (stable, MSVC) first.' }
$rustc = Join-Path (Split-Path $cargo) 'rustc.exe'

# Built from the crate's own folder: cargo reads its settings (.cargo/config.toml, which links
# Microsoft's C runtime into the DLL) from the folder it's run in, not from the manifest's.
Push-Location $crate
try {
    & $cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force $native | Out-Null
$built = Join-Path $crate 'target\release\mxu_automerge.dll'
# A DLL that needs the Visual C++ Redistributable wouldn't load on a PC without it.
if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($built)) -match 'VCRUNTIME') {
    throw "$built needs VCRUNTIME*.dll: the static C runtime (.cargo/config.toml) wasn't used"
}
$dll = Join-Path $native 'automerge.dll'
Copy-Item $built $dll -Force
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()

# The licence notices: Automerge's MIT licence, then every crate compiled into the DLL.
$crates = & $cargo tree --manifest-path (Join-Path $crate 'Cargo.toml') --edges normal,no-proc-macro --prefix none `
    --format '{p} | {l}' --target x86_64-pc-windows-msvc --locked |
    Where-Object { $_ -notmatch '^mxu-automerge-ffi' -and $_ -notmatch '\(proc-macro\)' } |
    ForEach-Object { $_ -replace ' \(\*\)$', '' } | Sort-Object -Unique
$rustVersion = (& $rustc --version)
$notice = @"
automerge.dll: the Automerge core used by MxU Slides for Windows to sync the library with the Mac
=================================================================================================

Built from source by apps/windows/tools/build-automerge.ps1 with $rustVersion.
It is the "automerge" crate 0.7.2 (https://github.com/automerge/automerge, MIT), the version the
Mac's automerge-swift 0.7.2 builds, wrapped in a small C interface
(apps/windows/native/automerge-ffi). SHA-256 of this build: $hash

Automerge's licence
-------------------

$((Get-Content (Join-Path $crate 'LICENSE-automerge.txt') -Raw).TrimEnd())

Every crate compiled into the DLL
---------------------------------

Each is used under the MIT licence where it offers one (the text above, with that crate's own
authors as the copyright holders); ryu is used under BSL-1.0.

$($crates -join "`n")
"@
Set-Content -Path (Join-Path $native 'AUTOMERGE-NOTICES.txt') -Value $notice -Encoding utf8NoBOM
Write-Host "Built $dll"
Write-Host "SHA-256 $hash"
