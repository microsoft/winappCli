# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
Run the real native-element binding capture lifetime gate on a desktop host.
.DESCRIPTION
Requires the existing Windows App Runtime. Creates one hidden in-process window;
does not launch the CLI, register a package, or install a runtime. Zero/skip/fail
results fail this runner. Ordinary headless test runs leave this gate disabled.
#>
param([string]$ResultsDirectory = "$PSScriptRoot\..\artifacts\TestResults\binding-lifetime")
$ErrorActionPreference = 'Stop'
$project = "$PSScriptRoot\..\src\winapp-devtools\test\WinApp.DevTools.Managed.Tests\WinApp.DevTools.Managed.Tests.csproj"
$provider = $null
$drive = $null
$ResultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $ResultsDirectory, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'ResultsDirectory must be a filesystem path.' }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$report = "binding-lifetime-$([Guid]::NewGuid().ToString('N')).trx"
$previous = $env:WINAPP_BINDING_LIFETIME_TESTS
try {
    $env:WINAPP_BINDING_LIFETIME_TESTS = '1'
    dotnet run --project $project -f net10.0-windows10.0.19041.0 `
        -p:OutputPath="$PSScriptRoot\..\artifacts\binding-lifetime\" `
        --results-directory $ResultsDirectory --report-trx --report-trx-filename $report `
        --filter FullyQualifiedName~BindingCaptureLifetimeTests
    $testExit = $LASTEXITCODE
} finally { $env:WINAPP_BINDING_LIFETIME_TESTS = $previous }
if (-not (Test-Path "$ResultsDirectory\$report")) { throw 'No binding lifetime TRX was produced.' }
[xml]$trx = Get-Content "$ResultsDirectory\$report"
$counts = $trx.TestRun.ResultSummary.Counters
Write-Host "Binding lifetime: total=$($counts.total) passed=$($counts.passed) failed=$($counts.failed) skipped=$($counts.notExecuted)"
if ($testExit -ne 0 -or [int]$counts.total -ne 1 -or [int]$counts.passed -ne 1) {
    throw "The real binding lifetime gate must pass exactly one test, with no skips (exit $testExit)."
}
