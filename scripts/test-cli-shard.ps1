#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run one of three disjoint CLI test shards from an existing Debug build.
.DESCRIPTION
    Run shards in separate workspaces. Shard 3 owns NativeIntegration and requires
    the native dispatcher fixture. Of the remaining tests, shard 1 contains
    PackageCommandTests and shard 2 contains everything else, including new classes.
    In run 35270954877 the original two groups
    accounted for 1,781 and 1,719 summed test-seconds respectively (114 and 6,450 tests).
    These weights guide the split, not an assertion about wall-clock savings.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(1, 2, 3)]
    [int]$Shard,

    [Parameter(Mandatory)]
    [string]$TestProjectPath,

    [Parameter(Mandatory)]
    [string]$ResultsDirectory,

    [Parameter(Mandatory)]
    [string]$CoverageSettings
)

$ErrorActionPreference = 'Stop'
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$reportName = "WinApp.Cli.Tests.shard-$Shard"
$reportPath = Join-Path $ResultsDirectory "$reportName.trx"
$coveragePath = Join-Path $ResultsDirectory "$reportName.cobertura.xml"
$manifestPath = Join-Path $ResultsDirectory "cli-shard-$Shard.json"
foreach ($path in @($reportPath, $coveragePath, $manifestPath)) {
    if (Test-Path $path) { Remove-Item $path -Force }
}

# Native fixtures are built only by the native validation lane, never by artifact consumers.
if ($Shard -eq 3 -and (-not $env:WINAPP_NATIVE_TEST_FIXTURE -or
    -not (Test-Path -LiteralPath $env:WINAPP_NATIVE_TEST_FIXTURE -PathType Leaf))) {
    throw 'Shard 3 requires WINAPP_NATIVE_TEST_FIXTURE pointing to the freshly built native-runtime-tests.exe.'
}
# Category and class predicates partition every discovered case exactly once.
$className = 'WinApp.Cli.Tests.PackageCommandTests.'
$filters = @(
    "TestCategory!=NativeIntegration&FullyQualifiedName~$className",
    "TestCategory!=NativeIntegration&FullyQualifiedName!~$className",
    'TestCategory=NativeIntegration'
)
$runArgs = @('run', '--project', $TestProjectPath, '-c', 'Debug', '--no-build', '--')

function Get-DiscoveryCount([string]$Filter) {
    $argsForDiscovery = $runArgs + @('--list-tests', '--no-ansi')
    if ($Filter) { $argsForDiscovery += @('--filter', $Filter) }
    $output = & dotnet @argsForDiscovery 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "CLI test discovery failed for '$Filter': $($output -join "`n")"
    }
    $summaries = @([regex]::Matches(($output -join "`n"), 'Test discovery summary: found (\d+) test\(s\)'))
    if ($summaries.Count -ne 1) {
        throw "Expected one MTP discovery summary for '$Filter': $($output -join "`n")"
    }
    return [int]$summaries[0].Groups[1].Value
}

$allCount = Get-DiscoveryCount ''
$counts = @($filters | ForEach-Object { Get-DiscoveryCount $_ })
if (@($counts | Where-Object { $_ -le 0 }).Count -gt 0 -or
    ($counts | Measure-Object -Sum).Sum -ne $allCount) {
    throw "CLI shard discovery is empty or incomplete: All=$allCount, shards=$($counts -join ',')."
}
$expected = $counts[$Shard - 1]
$filter = $filters[$Shard - 1]
Write-Host "[SHARD] $Shard selects $expected of $allCount tests ($filter)."
@{
    shard = $Shard
    filter = $filter
    allTests = $allCount
    expectedTests = $expected
    shardCounts = $counts
} | ConvertTo-Json | Set-Content $manifestPath

# MTP's minimum counts executed tests, not intentional skips. The TRX total below
# must still account for every discovered case, including skipped parameterized rows.
& dotnet @runArgs --filter $filter --minimum-expected-tests 1 --no-ansi `
    --results-directory $ResultsDirectory --report-trx --report-trx-filename "$reportName.trx" `
    --coverage --coverage-settings $CoverageSettings --coverage-output-format cobertura `
    --coverage-output "$reportName.cobertura.xml"
$testExitCode = $LASTEXITCODE

if (-not (Test-Path $reportPath -PathType Leaf)) {
    throw "CLI shard $Shard did not produce its required report: $reportPath (exit $testExitCode)."
}
$report = [System.Xml.Linq.XDocument]::Load($reportPath)
$ns = [System.Xml.Linq.XNamespace]'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$counters = @($report.Descendants($ns + 'Counters'))
if ($counters.Count -ne 1 -or [int]$counters[0].Attribute('total').Value -ne $expected) {
    throw "CLI shard $Shard must report exactly $expected tests from discovery."
}
if (-not (Test-Path $coveragePath -PathType Leaf) -or (Get-Item $coveragePath).Length -eq 0) {
    throw "CLI shard $Shard did not produce coverage: $coveragePath."
}
if ($testExitCode -ne 0) { exit $testExitCode }
if ([int]$counters[0].Attribute('failed').Value -gt 0 -or
    [string]$report.Root.Element($ns + 'ResultSummary').Attribute('outcome').Value -ne 'Completed') {
    throw "CLI shard $Shard reported an unsuccessful test run despite a zero exit code."
}
$executed = [int]$counters[0].Attribute('executed').Value
$passed = [int]$counters[0].Attribute('passed').Value
$skipped = [int]$counters[0].Attribute('notExecuted').Value
if ($executed -lt 1 -or $passed -ne $executed -or ($executed + $skipped) -ne $expected) {
    throw "CLI shard $Shard has incomplete execution accounting: expected $expected, executed $executed, passed $passed, skipped $skipped."
}
if ($Shard -eq 3 -and $skipped -ne 0) {
    throw "Native integration shard must execute every case; $skipped tests were skipped."
}
foreach ($state in @('error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'disconnected', 'inProgress', 'pending')) {
    if ([int]$counters[0].Attribute($state).Value -ne 0) {
        throw "CLI shard $Shard reported an unsuccessful test state: $state."
    }
}
Write-Host "[SHARD] $Shard reported all $expected expected tests and coverage."
