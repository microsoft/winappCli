#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run one of two complementary CLI test shards from an existing Debug build.
.DESCRIPTION
    Run shards in separate workspaces. Shard 1 contains PackageCommandTests and shard 2
    contains everything else, including new test classes. In run 35270954877 these groups
    accounted for 1,781 and 1,719 summed test-seconds respectively (114 and 6,450 tests).
    These weights guide the split, not an assertion about wall-clock savings.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(1, 2)]
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

# The second predicate is the exact complement, so new tests cannot fall between shards.
$className = 'WinApp.Cli.Tests.PackageCommandTests.'
$filters = @("FullyQualifiedName~$className", "FullyQualifiedName!~$className")
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
if ($counts[0] -le 0 -or $counts[1] -le 0 -or ($counts[0] + $counts[1]) -ne $allCount) {
    throw "CLI shard discovery is empty or incomplete: All=$allCount, shard 1=$($counts[0]), shard 2=$($counts[1])."
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
foreach ($state in @('error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'disconnected', 'inProgress', 'pending')) {
    if ([int]$counters[0].Attribute($state).Value -ne 0) {
        throw "CLI shard $Shard reported an unsuccessful test state: $state."
    }
}
Write-Host "[SHARD] $Shard reported all $expected expected tests and coverage."
