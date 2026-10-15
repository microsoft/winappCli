# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Runs the same watch journeys under each hot reload approach and writes one comparison table.
.DESCRIPTION
  Approaches and the default journey list are in scoreboard.json: each approach is a set of environment variables (the
  pipeline and page-update switches) and MSBuild properties. For every approach x journey this runs Run-WatchJourney.ps1
  in its own PowerShell process with that approach's environment (each run takes the shared desktop lock itself), then
  reads every run's results.json and writes scoreboard.md and scoreboard.json:

  - saves live / restart / error, with the reason for each restart and error;
  - the journeys' UI checks, the parity gate's differences, and "state kept" (checks marked "state");
  - latency per save kind (xaml, cs, both, new type, package): p50/p90/max of the time from the save until the
    app shows it (for a restart: until the relaunched app is up);
  - whether every save stayed in the same process.

  -ReportOnly rebuilds the table from the newest run of each approach x journey already under -OutputRoot, so a column
  can be re-run alone (-Approach B) and the table rebuilt with the others.
.EXAMPLE
  .\Run-Scoreboard.ps1 -Approach A -WinUIPackageSource C:\mux-hr\PackageStore -OutputRoot C:\hr-scoreboard
.EXAMPLE
  .\Run-Scoreboard.ps1 -Approach B,C -WinappFor @{ B = 'C:\b\winapp.exe'; C = 'C:\b\winapp.exe' } -WinUIPackageSource C:\mux-hr\PackageStore -OutputRoot C:\hr-scoreboard
.EXAMPLE
  .\Run-Scoreboard.ps1 -ReportOnly -OutputRoot C:\hr-scoreboard
#>
[CmdletBinding()]
param(
    [string[]]$Approach,
    [string[]]$Journey,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-scoreboard'),
    [string]$Winapp = (Join-Path $PSScriptRoot '..\..\..\artifacts\cli\win-x64\winapp.exe'),
    # A CLI per approach, when an approach's code is on another branch: @{ B = 'C:\...\winapp.exe' }.
    [hashtable]$WinappFor = @{},
    [string]$WinUIPackageSource,
    [ValidateSet('Unpackaged', 'Packaged')][string]$Packaging = 'Unpackaged',
    [ValidateSet('Auto', 'On', 'Off')][string]$Parity = 'On',
    [string]$Config = (Join-Path $PSScriptRoot 'scoreboard.json'),
    [switch]$ReportOnly,
    [switch]$NoDesktopLock
)
$ErrorActionPreference = 'Stop'
$board = Get-Content $Config -Raw | ConvertFrom-Json
$approaches = @($board.approaches | Where-Object { -not $Approach -or $_.id -in $Approach })
if (-not $approaches) { throw "No approach matches '$($Approach -join ', ')'. Known: $(@($board.approaches.id) -join ', ')." }
$journeys = if ($Journey) { $Journey } else { @($board.journeys) }
$null = New-Item -ItemType Directory -Force $OutputRoot
$ansi = [regex]'\x1B\[[0-?]*[ -/]*[@-~]'

function Get-ApproachWinapp($A) {
    $path = if ($WinappFor.ContainsKey($A.id)) { $WinappFor[$A.id] } elseif ($A.winapp) { $A.winapp } else { $Winapp }
    return (Resolve-Path $path).Path
}

if (-not $ReportOnly) {
    $allNames = @($board.approaches | ForEach-Object { $_.env.PSObject.Properties.Name } | Sort-Object -Unique)
    foreach ($a in $approaches) {
        $cli = Get-ApproachWinapp $a
        $version = ($ansi.Replace((& $cli --version 2>&1 | Out-String), '')).Trim()
        # The commit the CLI was built from, when it sits in a checkout's artifacts folder.
        $commit = & git -C (Split-Path $cli) rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $commit) { $version += " @ $commit" }
        foreach ($j in $journeys) {
            $dir = Join-Path $OutputRoot "$($a.id)\$j"
            $null = New-Item -ItemType Directory -Force $dir
            $saved = @{}
            foreach ($name in $allNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
            foreach ($p in $a.env.PSObject.Properties) { [Environment]::SetEnvironmentVariable($p.Name, $(if ($null -eq $p.Value) { $null } else { [string]$p.Value })) }
            $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Run-WatchJourney.ps1'), '-JourneyName', $j, '-Winapp', $cli,
                '-OutputRoot', $dir, '-Packaging', $Packaging, '-Parity', $Parity)
            if ($WinUIPackageSource) { $arguments += @('-WinUIPackageSource', $WinUIPackageSource) }
            if ($NoDesktopLock) { $arguments += '-NoDesktopLock' }
            foreach ($property in @($a.buildProperties)) { if ($property) { $arguments += @('-BuildProperty', $property) } }
            $log = Join-Path $dir 'runner.log'
            Write-Host "[scoreboard] $($a.id) $j ..."
            $clock = [Diagnostics.Stopwatch]::StartNew()
            # The runner's own output, minus the lock's twice-a-minute "waiting" lines (they stay in the log).
            $lastWait = [datetime]::MinValue
            try {
                & pwsh @arguments *>&1 | Tee-Object -FilePath $log | ForEach-Object {
                    $line = "$_"
                    if ($line -match '^\[desktop-lock\] .* waiting ') {
                        if (((Get-Date) - $lastWait).TotalMinutes -lt 10) { return }
                        $lastWait = Get-Date
                    }
                    Write-Host "  $line"
                }
            }
            finally { foreach ($name in $allNames) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
            [pscustomobject]@{
                Approach = $a.id; Title = $a.title; Journey = $j; Winapp = $cli; WinappVersion = $version
                Env = $a.env; BuildProperties = @($a.buildProperties); Seconds = [int]$clock.Elapsed.TotalSeconds; Finished = (Get-Date -Format s)
                XamlCompilerService = $(if ($env:WINAPP_XAML_COMPILER_SERVICE) { $env:WINAPP_XAML_COMPILER_SERVICE } elseif (Test-Path 'C:\hr-shared\xcsvc\LATEST.txt') { "LATEST.txt: $((Get-Content 'C:\hr-shared\xcsvc\LATEST.txt' -Raw).Trim())" })
            } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'meta.json')
        }
    }
}

# ---------------------------------------------------------------- report
function Get-Percentile([double[]]$Values, [double]$P) {
    if (-not $Values) { return $null }
    $sorted = @($Values | Sort-Object)
    return $sorted[[Math]::Max(0, [Math]::Ceiling($P * $sorted.Count) - 1)]
}
function Format-Seconds($Ms) { if ($null -eq $Ms) { return '-' }; return ('{0:0.0}' -f ($Ms / 1000)) }

$cells = [System.Collections.Generic.List[object]]::new()
$reportApproaches = if ($ReportOnly -and -not $Approach) { @($board.approaches) } else { $approaches }
foreach ($a in $reportApproaches) {
    foreach ($j in $journeys) {
        $dir = Join-Path $OutputRoot "$($a.id)\$j"
        $run = Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'summary.json') } |
            Sort-Object Name -Descending | Select-Object -First 1
        $meta = if (Test-Path (Join-Path $dir 'meta.json')) { Get-Content (Join-Path $dir 'meta.json') -Raw | ConvertFrom-Json } else { $null }
        if (-not $run) {
            # meta.json is written when the run ends: without it the run is still queued or running.
            $status = if (Test-Path (Join-Path $dir 'meta.json')) { 'failed' } elseif (Test-Path (Join-Path $dir 'runner.log')) { 'running' } else { 'not run' }
            $cells.Add([pscustomobject]@{ Approach = $a.id; Journey = $j; Status = $status; Log = (Join-Path $dir 'runner.log') })
            continue
        }
        $rows = @(Get-ChildItem $run.FullName -Directory | ForEach-Object { Join-Path $_.FullName 'results.json' } | Where-Object { Test-Path $_ } |
            ForEach-Object { Get-Content $_ -Raw | ConvertFrom-Json } | ForEach-Object { $_ })
        $checks = @($rows | ForEach-Object { @($_.Checks) } | Where-Object { $_ })
        $parityChecks = @($checks | Where-Object { $_.Check -like 'parity *' })
        $ui = @($checks | Where-Object { $_.Check -notlike 'parity *' })
        $state = @($ui | Where-Object { $_.State })
        $cells.Add([pscustomobject]@{
            Approach = $a.id; Journey = $j; Status = 'ran'; Run = $run.FullName; WinappVersion = $meta.WinappVersion; Seconds = $meta.Seconds
            Saves = $rows.Count
            Live = @($rows | Where-Object { $_.Outcome -in 'applied', 'unchanged' }).Count
            Waiting = @($rows | Where-Object Outcome -eq 'waiting').Count
            Restart = @($rows | Where-Object Outcome -eq 'restart').Count
            Error = @($rows | Where-Object Outcome -eq 'error').Count
            Timeout = @($rows | Where-Object Outcome -eq 'timeout').Count
            ChecksOk = @($ui | Where-Object Ok).Count; Checks = $ui.Count
            StateOk = @($state | Where-Object Ok).Count; State = $state.Count
            StateLost = @($state | Where-Object { -not $_.Ok } | ForEach-Object { [string]$_.State } | Sort-Object -Unique)
            ParityOk = @($parityChecks | Where-Object Ok).Count; ParityChecks = $parityChecks.Count
            ParityDiffs = [int](@($parityChecks | ForEach-Object { if ($null -ne $_.Diffs) { [int]$_.Diffs } elseif (-not $_.Ok) { 1 } else { 0 } }) | Measure-Object -Sum).Sum
            SameProcess = -not @($rows | Where-Object { -not $_.SameProcess })
            Reasons = @($rows | Where-Object { $_.Outcome -in 'restart', 'error', 'waiting', 'timeout' } | ForEach-Object { [pscustomobject]@{ Action = $_.Action; Outcome = $_.Outcome; Reason = $_.Reason } })
            # What winapp told the developer alongside a save (D's "shows X here but Y after a restart", say).
            Notes = @($rows | ForEach-Object { $action = $_.Action; @([string]$_.Detail -split "`n" | Where-Object { $_ -match '^\s+note:' }) | ForEach-Object { [pscustomobject]@{ Action = $action; Note = (($_ -replace '^\s+note:\s*', '') -replace '\s*\(\d+ ms\)\s*$', '') } } })
            Saves_ = @($rows | ForEach-Object { [pscustomobject]@{ Kind = $(if ($_.Kind) { $_.Kind } else { 'other' }); WallMs = $_.WallMs; Ms = $_.Ms; Outcome = $_.Outcome } })
        })
    }
}

$ids = @($reportApproaches.id)
$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# Hot reload scoreboard ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))")
$md.Add('')
$md.Add('Approaches: ' + ((@($reportApproaches | ForEach-Object {
    $id = $_.id
    $v = (@($cells | Where-Object { $_.Approach -eq $id -and $_.WinappVersion } | ForEach-Object WinappVersion | Sort-Object -Unique) -join ', ')
    "**$id** $($_.title)$(if ($v) { " (winapp $v)" })"
})) -join '; ') + '.')
$md.Add('')
$md.Add('Cell: live/restart/error saves · UI checks passed/total · parity differences · state kept/checked · notes winapp printed. `-` = not run.')
$md.Add('')
$md.Add('| Journey | ' + ($ids -join ' | ') + ' |')
$md.Add('|---|' + (($ids | ForEach-Object { '---' }) -join '|') + '|')
foreach ($j in $journeys) {
    $line = "| $j |"
    foreach ($id in $ids) {
        $c = $cells | Where-Object { $_.Approach -eq $id -and $_.Journey -eq $j }
        $line += if (-not $c -or $c.Status -eq 'not run') { ' - |' }
            elseif ($c.Status -eq 'failed') { ' runner failed |' }
            elseif ($c.Status -eq 'running') { ' running |' }
            else {
                $parityText = if ($c.ParityChecks) { "parity $($c.ParityDiffs)" } else { 'parity off' }
                $stateText = if ($c.State) { " · state $($c.StateOk)/$($c.State)" } else { '' }
                $proc = if ($c.SameProcess) { '' } else { ' · new process' }
                $notesText = if (@($c.Notes).Count) { " · notes $(@($c.Notes).Count)" } else { '' }
                " $($c.Live)/$($c.Restart)/$($c.Error)$(if ($c.Timeout) { " +$($c.Timeout) timeout" }) · $($c.ChecksOk)/$($c.Checks) · $parityText$stateText$notesText$proc |"
            }
    }
    $md.Add($line)
}
$md.Add('')
$md.Add('## Totals')
$md.Add('')
$md.Add('| Approach | Journeys | Saves | Live | Restart | Error | Timeout | UI checks | Parity diffs (checks ok) | State kept | Notes | Same process |')
$md.Add('|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($id in $ids) {
    $ran = @($cells | Where-Object { $_.Approach -eq $id -and $_.Status -eq 'ran' })
    if (-not $ran) { $md.Add("| $id | 0 | | | | | | | | | | |"); continue }
    $sum = { param($p) [int](($ran | ForEach-Object { $_.$p }) | Measure-Object -Sum).Sum }
    $md.Add("| $id | $($ran.Count) | $(& $sum Saves) | $(& $sum Live) | $(& $sum Restart) | $(& $sum Error) | $(& $sum Timeout) | $(& $sum ChecksOk)/$(& $sum Checks) | $(& $sum ParityDiffs) ($(& $sum ParityOk)/$(& $sum ParityChecks)) | $(& $sum StateOk)/$(& $sum State) | $(@($ran | ForEach-Object { @($_.Notes) }).Count) | $(@($ran | Where-Object SameProcess).Count)/$($ran.Count) journeys |")
}
$md.Add('')
$md.Add('## Latency per save kind (seconds from save until the app shows it; a restart counts until the app is back)')
$md.Add('')
$kinds = @('xaml', 'cs', 'both', 'new type', 'package', 'project', 'other')
$present = @($kinds | Where-Object { $k = $_; @($cells | ForEach-Object { $_.Saves_ } | Where-Object { $_.Kind -eq $k }).Count })
$md.Add('| Approach | ' + (($present | ForEach-Object { "$_ p50 / p90 / max (n)" }) -join ' | ') + ' |')
$md.Add('|---|' + (($present | ForEach-Object { '---' }) -join '|') + '|')
foreach ($id in $ids) {
    $saves = @($cells | Where-Object Approach -eq $id | ForEach-Object { $_.Saves_ } | Where-Object { $_ -and $null -ne $_.WallMs })
    $line = "| $id |"
    foreach ($k in $present) {
        $values = [double[]]@($saves | Where-Object Kind -eq $k | ForEach-Object { [double]$_.WallMs })
        $line += if (-not $values) { ' - |' } else { " $(Format-Seconds (Get-Percentile $values 0.5)) / $(Format-Seconds (Get-Percentile $values 0.9)) / $(Format-Seconds ($values | Measure-Object -Maximum).Maximum) ($($values.Count)) |" }
    }
    $md.Add($line)
}
$md.Add('')
$md.Add('## Restarts, errors, notes and lost state')
foreach ($id in $ids) {
    $md.Add('')
    $md.Add("### $id")
    $any = $false
    foreach ($c in @($cells | Where-Object { $_.Approach -eq $id -and $_.Status -eq 'ran' })) {
        foreach ($r in @($c.Reasons)) { $md.Add("- $($c.Journey) / $($r.Action): **$($r.Outcome)** $($r.Reason)"); $any = $true }
        foreach ($n in @($c.Notes)) { $md.Add("- $($c.Journey) / $($n.Action): note: $($n.Note)"); $any = $true }
        if ($c.StateLost) { $md.Add("- $($c.Journey): state lost: $(@($c.StateLost) -join ', ')"); $any = $true }
        if ($c.ParityDiffs) { $md.Add("- $($c.Journey): $($c.ParityDiffs) parity difference(s), see $($c.Run)\*\parity\*\parity.json"); $any = $true }
    }
    foreach ($c in @($cells | Where-Object { $_.Approach -eq $id -and $_.Status -eq 'failed' })) { $md.Add("- $($c.Journey): the runner failed, see $($c.Log)"); $any = $true }
    if (-not $any) { $md.Add('- none') }
}
$md.Add('')
$md.Add('## Runs')
$md.Add('')
foreach ($c in @($cells | Where-Object Status -eq 'ran')) { $md.Add("- $($c.Approach) $($c.Journey): $($c.Run) ($($c.Seconds) s)") }

$text = $md -join "`n"
Set-Content (Join-Path $OutputRoot 'scoreboard.md') $text
$cells | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputRoot 'scoreboard.json')
Write-Host $text
Write-Host "`nScoreboard: $(Join-Path $OutputRoot 'scoreboard.md')"
