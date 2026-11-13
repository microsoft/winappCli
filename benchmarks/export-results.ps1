<#
.SYNOPSIS
  Zips the result files of a benchmark output folder, without app sources, build output or event logs.
.DESCRIPTION
  A full run's output folder holds every generated app with its obj and bin folders, so it can be gigabytes.
  Analysis needs only the result and log files. This script finds them anywhere under -Path and keeps their
  relative paths:

  - apps: results.json, trial-result.json, state.json, test-results.json, live-output.txt, run-meta.json, upstream-pins.json
  - tasks: runs.jsonl, run-info*.json, summary.md, trials\*.json, and each run's check.json, agent-run.json, agent-run.log
  - *.log files directly in -Path

  App folders (app, obj, bin, node_modules) are never searched. -IncludeLogs also adds session-logs-dir folders
  and events.jsonl files (agent and judge events).
.EXAMPLE
  pwsh benchmarks\export-results.ps1 -Path C:\bench-out\full -Out C:\bench-out\full-results.zip
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Out,
    [switch]$IncludeLogs
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
$outFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$names = 'results.json', 'trial-result.json', 'state.json', 'test-results.json', 'live-output.txt', 'run-meta.json',
    'upstream-pins.json', 'runs.jsonl', 'summary.md', 'check.json', 'agent-run.json', 'agent-run.log'
$skipDirs = 'app', 'obj', 'bin', 'node_modules', '.git'

function Get-ExportFile {
    param([string]$Dir, [bool]$InLogs)
    foreach ($f in [IO.Directory]::EnumerateFiles($Dir)) {
        $name = [IO.Path]::GetFileName($f)
        $parent = [IO.Path]::GetFileName($Dir)
        $keep = $InLogs -or $name -in $names -or $name -like 'run-info*.json' -or
            ($parent -eq 'trials' -and $name -like '*.json') -or
            ($Dir -eq $root -and $name -like '*.log') -or
            ($IncludeLogs -and $name -eq 'events.jsonl')
        if ($keep -and $f -ne $outFull) { $f }
    }
    foreach ($d in [IO.Directory]::EnumerateDirectories($Dir)) {
        $name = [IO.Path]::GetFileName($d)
        if ($name -in $skipDirs) { continue }
        $isLogs = $name -eq 'session-logs-dir'
        if ($isLogs -and -not $IncludeLogs) { continue }
        if ([IO.File]::GetAttributes($d) -band [IO.FileAttributes]::ReparsePoint) { continue }
        Get-ExportFile -Dir $d -InLogs ($InLogs -or $isLogs)
    }
}

$files = @(Get-ExportFile -Dir $root -InLogs $false)
if (-not $files) { throw "No result files found under $root." }

if (Test-Path -LiteralPath $outFull) { Remove-Item -LiteralPath $outFull -Force }
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outFull)) | Out-Null
$zip = [IO.Compression.ZipFile]::Open($outFull, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $entry = $f.Substring($root.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

$mb = [Math]::Round((Get-Item -LiteralPath $outFull).Length / 1MB, 1)
Write-Host "Exported $($files.Count) files to $outFull ($mb MB)."
