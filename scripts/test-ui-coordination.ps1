<#
.SYNOPSIS
Runs the gated cooperative desktop-turn tests and proves they actually ran.

.DESCRIPTION
The UI coordination suites (issue #764 §18.2/§18.3) drive real winapp.exe child processes against a
real foreground window, so they are gated behind WINAPP_UI_MULTIPROCESS_TESTS and stay off the
canonical build. This script sets that gate and runs them on an interactive machine or CI lane where
the published CLI binaries already exist.

The gate itself is the reason this is a script rather than a plain `dotnet run`. When the gate is
unset, or the published-binary lookup regresses, every test reports Inconclusive — and the run still
exits 0. Trusting the exit code would mean a green build that verified nothing, which is exactly what
happened before these suites were wired into CI at all. So the result is asserted against the TRX:
zero tests is a broken filter, any skip is a broken gate, and either fails the run.

.PARAMETER ResultsDirectory
Where to write the TRX. Default: artifacts/TestResults/ui-coordination under the repo root.

.PARAMETER Configuration
Build configuration for the test project. Default: Debug.

.PARAMETER Filter
Test filter. Default: the multiprocess and real-app coordination suites.

.EXAMPLE
.\test-ui-coordination.ps1
Run every gated coordination test and fail on zero-matched, skipped, or failing tests.

.EXAMPLE
.\test-ui-coordination.ps1 -Filter "FullyQualifiedName~InteractiveDesktopRealAppTests"
Run only the real-app suite.
#>

param(
    [string]$ResultsDirectory,
    [string]$Configuration = 'Debug',
    [string]$Filter = 'FullyQualifiedName~InteractiveDesktopMultiprocessTests|FullyQualifiedName~InteractiveDesktopRealAppTests'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$testProject = Join-Path $repoRoot 'src\winapp-CLI\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj'

if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repoRoot 'artifacts\TestResults\ui-coordination'
}

New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

# The suites resolve the published winapp.exe for the current architecture themselves; this only has
# to turn them on. Scoped to the child process so an interactive shell is not left gated.
$env:WINAPP_UI_MULTIPROCESS_TESTS = '1'

dotnet run --project $testProject -c $Configuration `
    --results-directory $ResultsDirectory --report-trx --report-trx-filename ui-coordination.trx `
    --filter $Filter
$testExit = $LASTEXITCODE

$trx = Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) { throw "No TRX produced: the UI coordination tests did not run." }

[xml]$doc = Get-Content $trx.FullName
$counters = $doc.TestRun.ResultSummary.Counters
$total = [int]$counters.total
$passed = [int]$counters.passed
$failed = [int]$counters.failed
# MSTest reports Assert.Inconclusive (the gate's skip path) under notExecuted.
$skipped = [int]$counters.notExecuted + [int]$counters.inconclusive
Write-Host "UI coordination tests: total=$total passed=$passed failed=$failed skipped=$skipped"

# Asserted dynamically rather than pinned to today's count, so adding coverage does not fail the
# build while a filter that stops matching still does.
if ($total -eq 0) { throw "The UI coordination filter matched no tests — it no longer selects the gated suites." }
if ($skipped -gt 0) { throw "$skipped UI coordination test(s) skipped; the gate must run them here, not skip them." }
if ($failed -gt 0 -or $testExit -ne 0) { throw "UI coordination tests failed (exit $testExit)." }
