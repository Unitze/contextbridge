<#
.SYNOPSIS
  Builds ContextBridge on Windows: native handler (MSVC), settings app, sparse MSIX, installer.

.EXAMPLE
  ./scripts/build.ps1                 # x64 + arm64, with tests
  ./scripts/build.ps1 -Arch x64 -SkipTests

.NOTES
  Requires: Visual Studio 2022+ (C++ workload, x64/ARM64 tools), CMake, .NET 10 SDK, Python 3.
  Optional: Windows SDK (MakeAppx.exe; otherwise scripts/build_msix.py is used),
            NSIS 3 (makensis.exe; otherwise the installer step is skipped).
  The release build normally runs on Linux (scripts/build.sh); this script is for local Windows builds.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Version = (Get-Content (Join-Path $Root 'VERSION') -Raw).Trim()
$winParts = @([regex]::Matches($Version, '\d+') | ForEach-Object { $_.Value })
while ($winParts.Count -lt 4) { $winParts += '0' }
$WinVersion = ($winParts[0..3]) -join '.'
$Dist = Join-Path $Root 'dist'
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

if (-not $SkipTests) {
    Write-Host '== native tests (host)' -ForegroundColor Cyan
    $tb = Join-Path $Root 'build/tests'
    Invoke-Checked cmake @('-S', $Root, '-B', $tb, '-A', 'x64')
    Invoke-Checked cmake @('--build', $tb, '--config', 'Release', '--target', 'cb_core_tests')
    Invoke-Checked ctest @('--test-dir', $tb, '-C', 'Release', '--output-on-failure')

    Write-Host '== .NET tests' -ForegroundColor Cyan
    Invoke-Checked dotnet @('test', (Join-Path $Root 'tests/ContextBridge.Core.Tests'), '-c', 'Release')
}

$makeAppx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
$makensis = @((Get-Command makensis -ErrorAction SilentlyContinue).Source, "${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

foreach ($a in $Arch) {
    Write-Host "== $a" -ForegroundColor Cyan
    $stage = Join-Path $Dist "stage-$a"
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # Native handler
    $cmakeArch = if ($a -eq 'arm64') { 'ARM64' } else { 'x64' }
    $nb = Join-Path $Root "build/shell-$a"
    Invoke-Checked cmake @('-S', $Root, '-B', $nb, '-A', $cmakeArch, '-DCB_BUILD_TESTS=OFF')
    Invoke-Checked cmake @('--build', $nb, '--config', 'Release', '--target', 'ContextBridge.Shell')
    Copy-Item (Join-Path $nb 'Release/ContextBridge.Shell.dll') $stage

    # Settings app (self-contained single file)
    $pub = Join-Path $Root "build/app-$a"
    Invoke-Checked dotnet @('publish', (Join-Path $Root 'src/ContextBridge.App'), '-c', 'Release', '-r', "win-$a", '-o', $pub)
    Copy-Item (Join-Path $pub 'ContextBridge.exe') $stage

    # Sparse package
    $msix = Join-Path $stage 'ContextBridge.msix'
    if ($makeAppx) {
        $layout = Join-Path $Root "build/msix-$a"
        Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets') | Out-Null
        Invoke-Checked python @((Join-Path $Root 'scripts/build_msix.py'), '--arch', $a, '--version', $Version, '--out', $msix,
                                '--manifest-only', (Join-Path $layout 'AppxManifest.xml'))
        Copy-Item (Join-Path $Root 'package/Assets/*.png') (Join-Path $layout 'Assets')
        Invoke-Checked $makeAppx.FullName @('pack', '/d', $layout, '/p', $msix, '/nv', '/o')
    }
    else {
        Invoke-Checked python @((Join-Path $Root 'scripts/build_msix.py'), '--arch', $a, '--version', $Version, '--out', $msix)
    }

    Copy-Item (Join-Path $Root 'installer/register.ps1') $stage
    Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $stage 'LICENSE.txt')

    # Portable zip (for manual installs) and installer
    $zip = Join-Path $Dist "ContextBridge-$Version-$a-portable.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    if ($makensis) {
        Invoke-Checked $makensis @('-V2', '-WX', "-DARCH=$a", "-DVERSION=$Version", "-DWINVERSION=$WinVersion", "-DSTAGE=$stage", "-DOUTDIR=$Dist",
                                   (Join-Path $Root 'installer/ContextBridge.nsi'))
    }
    else {
        Write-Warning 'NSIS (makensis.exe) not found: installer skipped.'
    }
}

Write-Host "Done. Output: $Dist" -ForegroundColor Green
