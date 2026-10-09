# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
Run headless managed binding contracts without requiring an installed Windows App Runtime.
.DESCRIPTION
Restore, build, discovery and execution have separate logs and bounded processes.
The desktop lifetime gate uses its own runner and retains App SDK initialization.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('net8.0-windows10.0.19041.0', 'net10.0-windows10.0.19041.0')]
    [string]$Framework,
    [string]$ResultsDirectory = "$PSScriptRoot\..\artifacts\TestResults\devtools",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\managed-tests"
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath("$PSScriptRoot\..\src\winapp-devtools\test\WinApp.DevTools.Managed.Tests\WinApp.DevTools.Managed.Tests.csproj")
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $Framework
$logs = Join-Path $ResultsDirectory "managed-$Framework"
New-Item -ItemType Directory -Path $logs, $output -Force | Out-Null
$report = "managed-$Framework.trx"
$reportPath = Join-Path $ResultsDirectory $report
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
if (-not $env:WINAPP_BINDING_TEST_FIXTURE -or
    -not (Test-Path -LiteralPath $env:WINAPP_BINDING_TEST_FIXTURE -PathType Leaf)) {
    throw 'WINAPP_BINDING_TEST_FIXTURE must name the freshly built binding-transfer-fixture.dll.'
}

function Invoke-ManagedPhase {
    param([string]$Name, [string]$FileName, [string[]]$Arguments, [int]$TimeoutSeconds)
    $prefix = Join-Path $logs $Name
    $start = [Diagnostics.ProcessStartInfo]::new($FileName)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $receipt = @{
        phase = $Name; framework = $Framework; executable = $FileName; arguments = $Arguments
        started = [DateTime]::UtcNow.ToString('O'); timeoutSeconds = $TimeoutSeconds
    }
    $receipt | ConvertTo-Json -Depth 4 | Set-Content "$prefix.json"
    Write-Host "[$Framework/$Name] $FileName $($Arguments -join ' ')"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = [IO.FileStream]::new("$prefix.stdout.log", [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1)
    $stderr = [IO.FileStream]::new("$prefix.stderr.log", [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $outCopy = $null
    $errCopy = $null
    try {
        if (-not $process.Start()) { throw "Could not start $Name" }
        $receipt.pid = $process.Id
        $receipt | ConvertTo-Json -Depth 4 | Set-Content "$prefix.json"
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $nextHeartbeat = 15
        while (-not $process.WaitForExit(1000)) {
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $receipt.timedOut = $true
                throw "$Framework/$Name exceeded ${TimeoutSeconds}s (PID $($process.Id)); see $prefix.*."
            }
            if ($watch.Elapsed.TotalSeconds -ge $nextHeartbeat) {
                Write-Host "[$Framework/$Name] PID $($process.Id), elapsed $([int]$watch.Elapsed.TotalSeconds)s; logs: $prefix.*"
                $nextHeartbeat += 15
            }
        }
        $receipt.exitCode = $process.ExitCode
        if ($process.ExitCode -ne 0) { throw "$Framework/$Name failed with exit $($process.ExitCode); see $prefix.*." }
    }
    catch {
        $receipt.error = $_.Exception.Message
        throw
    }
    finally {
        $cleanupError = $null
        if ($receipt.ContainsKey('pid')) {
            if (-not $process.HasExited) { $process.Kill($true) }
            if (-not $process.WaitForExit(10000)) { $cleanupError = "$Name PID $($process.Id) did not exit after termination." }
            if ($null -ne $outCopy -and $null -ne $errCopy) {
                if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outCopy, $errCopy), 10000)) {
                    $cleanupError = "$Name output streams did not close after termination."
                }
            }
        }
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
        $receipt.elapsedSeconds = $watch.Elapsed.TotalSeconds
        $receipt.finished = [DateTime]::UtcNow.ToString('O')
        if ($cleanupError) { $receipt.cleanupError = $cleanupError }
        $receipt | ConvertTo-Json -Depth 4 | Set-Content "$prefix.json"
        Get-Content "$prefix.stdout.log" -Tail 30 | ForEach-Object { Write-Host $_ }
        Get-Content "$prefix.stderr.log" -Tail 30 | ForEach-Object { Write-Host $_ }
        if ($cleanupError) { throw $cleanupError }
    }
}

# Even window-free contracts use XAML statics. Supply the runtime app-locally;
# the framework-dependent bootstrap can show a missing-runtime dialog before MTP.
$properties = @('-p:WinAppHeadlessTests=true',
    '-p:TreatWarningsAsErrors=true', "-p:OutputPath=$output\")
Invoke-ManagedPhase restore dotnet (@('restore', $project, '--verbosity', 'minimal') + $properties) 600
Invoke-ManagedPhase build dotnet (@('build', $project, '--framework', $Framework, '--no-restore', '--verbosity', 'minimal',
    "-bl:$logs\build.binlog") + $properties) 600

$executable = Join-Path $output 'WinApp.DevTools.Managed.Tests.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing built test executable: $executable" }
@('WinApp.DevTools.Managed.Tests.exe', 'WinApp.DevTools.Managed.Tests.dll',
    'WinApp.DevTools.Managed.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll') |
    ForEach-Object { Get-FileHash -LiteralPath (Join-Path $output $_) } |
    Select-Object Path, Hash | ConvertTo-Json | Set-Content (Join-Path $logs 'payload.json')
$filter = 'TestCategory!=RequiresDesktop'
Invoke-ManagedPhase discovery $executable @('--list-tests', '--filter', $filter, '--no-ansi') 60
$discovery = Get-Content (Join-Path $logs 'discovery.stdout.log') -Raw
$matches = [regex]::Matches($discovery, 'Test discovery summary: found (\d+) test\(s\)')
if ($matches.Count -ne 1 -or [int]$matches[0].Groups[1].Value -le 0) {
    throw "Expected a nonempty managed contract discovery summary: $discovery"
}
# MSTest defers the four Type-valued conversion DataRows until execution:
# today's 103 discovery nodes expand to 106 executed cases.
$minimum = [Math]::Max(106, [int]$matches[0].Groups[1].Value)
Invoke-ManagedPhase test $executable @('--filter', $filter, '--minimum-expected-tests', "$minimum",
    '--timeout', '4m', '--no-ansi', '--output', 'Detailed',
    '--diagnostic', '--diagnostic-output-directory', $logs,
    '--results-directory', $ResultsDirectory, '--report-trx', '--report-trx-filename', $report) 300
[xml]$trx = Get-Content -LiteralPath $reportPath
$counts = $trx.TestRun.ResultSummary.Counters
if ([int]$counts.total -lt $minimum -or [int]$counts.passed -ne [int]$counts.total -or
    [int]$counts.executed -ne [int]$counts.total) {
    throw "Managed contracts must execute and pass every case (minimum $minimum): $($counts.OuterXml)"
}
Write-Host "[$Framework] $($counts.passed)/$($counts.total) managed contracts passed; zero skips."
