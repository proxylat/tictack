<#
.SYNOPSIS
    Runs the TicTack test suite on Windows, including the Windows-gated tests.

.DESCRIPTION
    On Linux the [Trait("Category","Windows")] tests early-return, so they only
    ever execute on Windows. This runner mirrors what windows-latest does in CI
    and adds an opt-in switch for the elevated tests.

    Default (no switches): full suite, unelevated, no coverage.
    -WindowsOnly:          only Category=Windows tests.
    -Coverage:             collect XPlat code coverage via tests/coverlet.runsettings.
    -Elevated:             sets TICTACK_ELEVATED_TESTS=1 so the admin-gated tests run.
                           The shell must already be elevated (Run as Administrator).
    -NoBuild:              pass --no-build to dotnet test.

.EXAMPLE
    ./tools/test-windows.ps1
    ./tools/test-windows.ps1 -WindowsOnly
    ./tools/test-windows.ps1 -Elevated -Coverage

.NOTES
    See docs/windows-testing.md for what each Windows test proves and the
    prerequisites for the elevated set.
#>
[CmdletBinding()]
param(
    [switch]$WindowsOnly,
    [switch]$Coverage,
    [switch]$Elevated,
    [switch]$NoBuild,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $repoRoot 'tests/TicTack.Tests.csproj'
$runSettings = Join-Path $repoRoot 'tests/coverlet.runsettings'

if (-not (Test-Path $testProj)) { throw "Test project not found: $testProj" }

# --- prerequisites ---------------------------------------------------------
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw "A .NET 10 SDK is required. Installed: $($sdks -join '; ')"
}

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Elevated -and -not $isAdmin) {
    Write-Warning "-Elevated was requested but this shell is not elevated; the admin-gated tests will skip themselves."
}

# --- run -------------------------------------------------------------------
$testArgs = @('test', $testProj, '--configuration', $Configuration)
if ($NoBuild) { $testArgs += '--no-build' }
if ($WindowsOnly) { $testArgs += @('--filter', 'Category=Windows') }
if ($Coverage) { $testArgs += @('--settings', $runSettings, '--collect', 'XPlat Code Coverage') }
$testArgs += @('--blame-hang', '--blame-hang-timeout', '180000')

if ($Elevated) {
    $env:TICTACK_ELEVATED_TESTS = '1'
    Write-Host "Elevated tests enabled (TICTACK_ELEVATED_TESTS=1)."
}

Write-Host "dotnet $($testArgs -join ' ')" -ForegroundColor Cyan
& dotnet @testArgs
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host "Tests FAILED (exit $code). Hang dumps, if any, are under tests/TestResults/." -ForegroundColor Red
} else {
    Write-Host "Tests passed." -ForegroundColor Green
}
exit $code
