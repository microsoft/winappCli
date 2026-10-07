# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    Measure attach-on-demand: how long `winapp devtools attach` takes on a running app with a
    large live tree, how long the app's UI thread stalls during it, and the first query after it.
.DESCRIPTION
    For each tree size, launches the devtools-perf bench app without DevTools, navigates it to a
    page and stays there, then attaches (headless and with --overlay) while pinging the UI thread.
    Writes ondemand.json and ondemand.md to -OutDir. No input is sent.
.EXAMPLE
    .\scripts\devtools-perf\ondemand.ps1
#>
[CmdletBinding()]
param(
    [string]$Winapp,
    [int]$Repetitions = 3,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\devtools-perf\ondemand')
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Winapp) { $Winapp = Join-Path $repoRoot 'artifacts\cli\win-x64\winapp.exe' }
$Winapp = (Resolve-Path $Winapp).Path
$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$work = Join-Path ([IO.Path]::GetTempPath()) 'winapp-devtools-perf'
$stage = Join-Path $work 'bench-app'
$control = Join-Path $work 'bench-control'
Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'PerfNative.cs'))

& (Join-Path $PSScriptRoot '..\devtools-perf.ps1') -Winapp $Winapp -Apps bench -PrepareOnly | Out-Null

$script:benchId = 0
function Wait-File([string]$Path, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path $Path)) { if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $Path" }; Start-Sleep -Milliseconds 50 }
    Get-Content -Raw $Path | ConvertFrom-Json
}
function Invoke-Bench([hashtable]$Command) {
    $script:benchId++
    $Command.id = $script:benchId
    $path = Join-Path $control 'cmd.json'
    Set-Content "$path.tmp" ($Command | ConvertTo-Json -Compress)
    Move-Item "$path.tmp" $path -Force
    $r = Wait-File (Join-Path $control "res-$($script:benchId).json") 300
    if ($r.error) { throw "Bench $($Command.name): $($r.error)" }
    $r
}
function Measure-Cli([string[]]$Arguments, [long]$Hwnd) {
    $pings = [DtpNative+PingRecorder]::new($Hwnd, 16, 5000)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Winapp @Arguments 2>&1 | Out-String
    $ms = $clock.Elapsed.TotalMilliseconds
    $p = $pings.Stop()
    if ($LASTEXITCODE) { throw "winapp $($Arguments -join ' ') failed: $out" }
    [ordered]@{ ms = [Math]::Round($ms, 1); uiMaxStallMs = [Math]::Round(($p | Measure-Object -Maximum).Maximum, 1)
        uiStallOver50Ms = @($p | Where-Object { $_ -gt 50 }).Count }
}

$sizes = @(
    @{ label = 'Heavy page'; page = 'heavy' },
    @{ label = 'BigTree 1,000 items'; page = 'bigtree'; count = 1000 },
    @{ label = 'BigTree 5,000 items'; page = 'bigtree'; count = 5000 }
)
$results = [ordered]@{ commit = (git -C $repoRoot rev-parse HEAD).Trim(); runs = @() }
foreach ($size in $sizes) {
    foreach ($overlay in $false, $true) {
        for ($i = 1; $i -le $Repetitions; $i++) {
            Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $($size.label) overlay=$overlay #$i"
            Get-ChildItem $control -File | Remove-Item -Force
            $j = & $Winapp run $stage --no-build --detach --json 2>$null | Out-String | ConvertFrom-Json
            $startup = Wait-File (Join-Path $control 'startup.json') 120
            $appPid = [int]$startup.pid
            try {
                $hwnd = (Get-Process -Id $appPid).MainWindowHandle.ToInt64()
                $goto = Invoke-Bench @{ name = 'goto'; page = $size.page; count = $size.count }
                Start-Sleep -Seconds 2
                $attachArgs = @('devtools', 'attach', '--pid', "$appPid", '--json')
                if ($overlay) { $attachArgs += '--overlay' }
                $attach = Measure-Cli $attachArgs $hwnd
                $query = Measure-Cli @('devtools', 'search', 'TextBlock', '-a', "$appPid", '--json') $hwnd
                $mem = Invoke-Bench @{ name = 'mem' }
                $results.runs += [ordered]@{ tree = $size.label; elements = $goto.elements; overlay = $overlay
                    attach = $attach; firstQuery = $query; privateMB = $mem.privateMB }
            }
            finally {
                try { Invoke-Bench @{ name = 'exit' } | Out-Null } catch { }
                $p = Get-Process -Id $appPid -ErrorAction SilentlyContinue
                if ($p -and -not $p.WaitForExit(15000)) { Stop-Process -Id $appPid -Force }
                Start-Sleep -Seconds 2
            }
            Set-Content (Join-Path $OutDir 'ondemand.json') ($results | ConvertTo-Json -Depth 8)
        }
    }
}

function Median([double[]]$v) { $s = @($v | Sort-Object); $s[[int][Math]::Floor(($s.Count - 1) / 2)] }
$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine('# Attach on demand')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Commit ``$($results.commit.Substring(0, 12))``. Median of $Repetitions launches. The app runs without DevTools, then ``winapp devtools attach`` is timed end to end while the UI thread is pinged every 16 ms.")
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Live tree | Elements | Overlay | `devtools attach`, ms | Longest UI stall during attach, ms | First `devtools search`, ms |')
[void]$sb.AppendLine('|---|---:|---|---:|---:|---:|')
foreach ($g in $results.runs | Group-Object { "$($_.tree)|$($_.overlay)" }) {
    $r = @($g.Group)
    [void]$sb.AppendLine("| $($r[0].tree) | $($r[0].elements) | $(if ($r[0].overlay) { 'yes' } else { 'no' }) | $(Median ($r | ForEach-Object { $_.attach.ms })) | $(Median ($r | ForEach-Object { $_.attach.uiMaxStallMs })) | $(Median ($r | ForEach-Object { $_.firstQuery.ms })) |")
}
Set-Content (Join-Path $OutDir 'ondemand.md') $sb.ToString()
Get-Content (Join-Path $OutDir 'ondemand.md')
