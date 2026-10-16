# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Tables for the real-app comparison: for each journey (one app), every save x approach with its outcome, the seconds
  until the change was on screen, and what the user had built up that the save lost; totals; parity per checkpoint;
  first build and launch times.
.DESCRIPTION
  Reads the latest run of each approach under <RunsRoot>\<approach>\<journey>\<timestamp>\default (results.json,
  startup.json, meta.json from C:\hrrg\run-cell.ps1 or the same shape) and writes Markdown.
.EXAMPLE
  .\Report-RealAppRuns.ps1 -RunsRoot C:\hrrg\runs -Journey rg-winui-gallery, rg-ai-dev-gallery -Out C:\hrrg\report.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunsRoot,
    [Parameter(Mandatory)][string[]]$Journey,
    [string[]]$Approach = @('A', 'B', 'C', 'D'),
    [string]$Out
)
$ErrorActionPreference = 'Stop'

function Get-LatestRun([string]$A, [string]$J) {
    $dir = Join-Path $RunsRoot "$A\$J"
    if (-not (Test-Path $dir)) { return $null }
    $run = Get-ChildItem $dir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'default\results.json') } | Sort-Object Name | Select-Object -Last 1
    if (-not $run) { return $null }
    $default = Join-Path $run.FullName 'default'
    return [pscustomobject]@{
        Path = $run.FullName
        Rows = @(Get-Content (Join-Path $default 'results.json') -Raw | ConvertFrom-Json)
        Startup = $(if (Test-Path (Join-Path $default 'startup.json')) { Get-Content (Join-Path $default 'startup.json') -Raw | ConvertFrom-Json })
        Meta = $(if (Test-Path (Join-Path $run.FullName 'meta.json')) { Get-Content (Join-Path $run.FullName 'meta.json') -Raw | ConvertFrom-Json })
    }
}

function Format-Sec($Ms) { if ($null -eq $Ms) { return '-' }; return ('{0:0.0} s' -f ($Ms / 1000)) }
function Get-Median([double[]]$Values) {
    if (-not $Values) { return $null }
    $sorted = @($Values | Sort-Object); $n = $sorted.Count
    if ($n % 2) { return $sorted[[int](($n - 1) / 2)] } else { return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
}
function Get-Percentile([double[]]$Values, [double]$P) {
    if (-not $Values) { return $null }
    $sorted = @($Values | Sort-Object)
    return $sorted[[Math]::Min($sorted.Count - 1, [int][Math]::Ceiling($P * $sorted.Count) - 1)]
}

$md = [System.Collections.Generic.List[string]]::new()
# A parity check as the journey's current ignore list sees it: the stored differences (parity.json) filtered again, so a
# pattern added for data a page randomizes on every launch applies to runs made before it was added.
function Get-ParityVerdict($Run, $Check, [string[]]$Ignore) {
    $label = ([string]$Check.Check) -replace '^parity ', ''
    $file = Join-Path $Run.Path "default\parity\$label\parity.json"
    $first = (@(([string]$Check.Output) -split "`n")[0]) -replace '^parity: ', ''
    $verdict = [pscustomobject]@{ Ok = [bool]$Check.Ok; Silent = [int]$Check.Diffs; Reported = [int]$Check.Reported; Incomplete = [bool]$Check.Incomplete; CleanBuildFailed = [bool]$Check.CleanBuildFailed; Skipped = [bool]$Check.Skipped; Summary = $first; Lines = @(); Ignored = 0 }
    if ($verdict.Incomplete -or $verdict.CleanBuildFailed -or $verdict.Skipped -or -not (Test-Path $file)) { return $verdict }
    $data = Get-Content $file -Raw | ConvertFrom-Json
    $kept = @($data.Diffs | Where-Object { $d = $_; -not @($Ignore | Where-Object { $d.Path -like $_ -or "$($d.Path).$($d.Property)" -like $_ }) } |
        Where-Object { ([string]$_.HotReload -replace '\.hr[0-9a-f]{8}(?=_)', '.hr*') -cne ([string]$_.Clean -replace '\.hr[0-9a-f]{8}(?=_)', '.hr*') })
    $verdict.Ignored = @($data.Diffs).Count - $kept.Count
    $verdict.Silent = $kept.Count
    $verdict.Ok = ($kept.Count -eq 0)
    $verdict.Lines = @($kept | Select-Object -First 6 | ForEach-Object {
        # The element's own name, or the nearest named ancestor: "ToggleSwitch#SemanticSearchToggleSwitch", "...ListView#Control2/Grid[3]".
        $segments = @(([string]$_.Element) -split '/')
        $named = [Array]::FindLastIndex([string[]]$segments, [Predicate[string]] { param($s) $s.Contains('#') })
        $short = if ($named -lt 0) { $segments[-1] } elseif ($named -eq $segments.Count - 1) { $segments[-1] } else { "$($segments[$named])/.../$($segments[-1])" }
        "$short / $($_.Property): hot reload '$($_.HotReload)' vs clean build '$($_.Clean)'"
    })
    if ($verdict.Ok -and $verdict.Ignored) { $verdict.Summary = "hot reload matches a clean build once $($verdict.Ignored) difference(s) are left out: data the page randomizes at launch, or the copy's own package path" }
    return $verdict
}
foreach ($j in $Journey) {
    $journeyDef = Get-Content (Join-Path $PSScriptRoot "journeys\$j.json") -Raw | ConvertFrom-Json
    $states = @($journeyDef.stateProbes | ForEach-Object { [string]$_.state })
    $restorable = @($journeyDef.restore | ForEach-Object target)
    $runs = [ordered]@{}
    foreach ($a in $Approach) { $r = Get-LatestRun $a $j; if ($r) { $runs[$a] = $r } }
    if (-not $runs.Count) { continue }
    $md.Add("## $($journeyDef.title)")
    $md.Add('')
    $md.Add('Stack: ' + (@($runs.Keys | ForEach-Object { $m = $runs[$_].Meta; "$_ = $(if ($m) { "$($m.Pipeline)$(if ($m.PageUpdate) { "/$($m.PageUpdate)" })" } else { '?' })" }) -join ', ') +
        $(if ($runs[0].Meta) { "; CLI $($runs[0].Meta.CliCommit); package $($runs[0].Meta.Package)" }))
    $md.Add('')

    # Save x approach.
    $md.Add('Each cell: outcome; for a live save, the time winapp reported, and in brackets when a check first saw the change on screen (that includes about a second of the check''s own overhead); for a restart, until the app was back up; then the state the save lost. "kept all" means every probe survived: ' + ($states -join ', ') + '. "already lost" is state an earlier save lost that could not be put back.')
    $md.Add('')
    $md.Add('| # | Save | Kind | ' + (@($runs.Keys) -join ' | ') + ' |')
    $md.Add('|---|---|---|' + (@($runs.Keys | ForEach-Object { '---' }) -join '|') + '|')
    $saveIds = @($journeyDef.segments.actions | ForEach-Object id)
    $lostBefore = @{}
    foreach ($a in $runs.Keys) { $lostBefore[$a] = [System.Collections.Generic.HashSet[string]]::new() }
    for ($i = 0; $i -lt $saveIds.Count; $i++) {
        $id = $saveIds[$i]
        $kind = ''
        $cells = foreach ($a in $runs.Keys) {
            $row = @($runs[$a].Rows | Where-Object Action -eq $id | Select-Object -First 1)
            if (-not $row) { '(not run)'; continue }
            $row = $row[0]
            $kind = $row.Kind
            $outcome = switch ($row.Outcome) { 'applied' { 'live' } 'unchanged' { 'live (unchanged)' } default { $_ } }
            if ($row.Outcome -eq 'restart') { $outcome = "**restart**" }
            if ($row.Outcome -eq 'error') { $outcome = $(if ($id -match 'broken') { 'error (expected)' } else { '**error**' }) }
            $visible = if ($null -eq $row.Applied) { '' } elseif ($row.Applied) { '' } else { ' **change not shown**' }
            $seconds = if ($null -ne $row.Ms -and $row.Outcome -eq 'applied') { "$(Format-Sec $row.Ms)$(if ($row.VisibleMs) { " (seen $(Format-Sec $row.VisibleMs))" })" } elseif ($row.VisibleMs) { Format-Sec $row.VisibleMs } else { Format-Sec $row.WallMs }
            $lost = @()
            $already = @()
            foreach ($check in @($row.Checks | Where-Object { $_.State })) {
                if ($check.Ok) { continue }
                $probe = @($journeyDef.stateProbes | Where-Object state -eq $check.State)[0]
                $restore = $probe.target -in $restorable
                if ($lostBefore[$a].Contains([string]$check.State)) { $already += $check.State; continue }
                $lost += $check.State
                # Lost for good: it can't be put back, or putting it back after this save failed.
                $restoreFailed = @($row.Checks | Where-Object { $_.Restore -and -not $_.Ok -and $_.Check -match "\b$([regex]::Escape([string]$probe.target))\b" }).Count -gt 0
                if (-not $restore -or $restoreFailed) { $null = $lostBefore[$a].Add([string]$check.State) }
            }
            if ($row.Outcome -eq 'restart') { $lostBefore[$a].Clear() }
            $stateText = if ($row.Outcome -eq 'error') { '' } elseif ($lost) { "lost: $($lost -join ', ')" } else { 'kept all' + $(if ($already) { " (already lost: $($already -join ', '))" }) }
            if ($lost -and $already) { $stateText += " (already lost: $($already -join ', '))" }
            "$outcome$visible, $seconds$(if ($stateText) { "; $stateText" })"
        }
        $md.Add("| $($i + 1) | $id | $kind | $($cells -join ' | ') |")
    }
    # The not-shown page's edit, checked by navigating there after the last save.
    $afterCells = foreach ($a in $runs.Keys) {
        $after = @($runs[$a].Rows | Where-Object Action -eq 'after' | Select-Object -First 1)
        if (-not $after) { '-'; continue }
        $verify = @($after[0].Checks | Where-Object Verifies | Select-Object -First 1)
        if (-not $verify) { '-' } elseif ($verify[0].Ok) { 'shown' } else { '**not shown**' }
    }
    $md.Add("| | (after the last save, open the page edited while not shown) | | $($afterCells -join ' | ') |")
    $md.Add('')

    # Totals.
    $md.Add('| Totals | ' + (@($runs.Keys) -join ' | ') + ' |')
    $md.Add('|---|' + (@($runs.Keys | ForEach-Object { '---' }) -join '|') + '|')
    $tot = [ordered]@{}
    foreach ($a in $runs.Keys) {
        $saves = @($runs[$a].Rows | Where-Object { $_.Kind -notin 'before', 'after' })
        $live = @($saves | Where-Object { $_.Outcome -in 'applied', 'unchanged', 'waiting' }).Count
        $restart = @($saves | Where-Object Outcome -eq 'restart').Count
        $errors = @($saves | Where-Object Outcome -eq 'error').Count
        $timeouts = @($saves | Where-Object Outcome -eq 'timeout').Count
        $checked = @($saves | Where-Object { $null -ne $_.Applied })
        $shown = @($checked | Where-Object Applied).Count
        $liveMs = @($saves | Where-Object { $_.Outcome -eq 'applied' -and $_.VisibleMs } | ForEach-Object { [double]$_.VisibleMs })
        $stateChecks = @($saves | Where-Object { $_.Outcome -ne 'error' } | ForEach-Object { @($_.Checks | Where-Object State) })
        $parity = @($saves | ForEach-Object { $s = $_; @($s.Checks | Where-Object { $_.Check -like 'parity *' }) | ForEach-Object { Get-ParityVerdict $runs[$a] $_ @($journeyDef.parity.ignore) } } | Where-Object { -not $_.Skipped })
        $tot[$a] = [ordered]@{
            'Saves live / restart / error' = "$live / $restart / $errors$(if ($timeouts) { " / $timeouts timeout" })"
            'Change on screen when checked' = "$shown of $($checked.Count)"
            'Seconds to see a live change, median (p90)' = $(if ($liveMs) { '{0:0.0} ({1:0.0})' -f ((Get-Median $liveMs) / 1000), ((Get-Percentile $liveMs 0.9) / 1000) } else { '-' })
            'State probes kept' = "$(@($stateChecks | Where-Object Ok).Count) of $($stateChecks.Count)"
            'Parity checks: match / reported / differ / incomplete / clean build failed' = "$(@($parity | Where-Object { $_.Ok -and -not $_.Reported }).Count) / $(@($parity | Where-Object { $_.Ok -and $_.Reported }).Count) / $(@($parity | Where-Object { -not $_.Ok -and -not $_.Incomplete -and -not $_.CleanBuildFailed }).Count) / $(@($parity | Where-Object Incomplete).Count) / $(@($parity | Where-Object CleanBuildFailed).Count)"
            'Silent parity differences (total)' = "$(($parity | Measure-Object Silent -Sum).Sum)"
            'First build (s)' = $(if ($runs[$a].Startup.BuildSeconds) { '{0:0}' -f $runs[$a].Startup.BuildSeconds } else { '-' })
            'Launch to ready (s)' = $(if ($runs[$a].Startup.ReadySeconds) { '{0:0}' -f $runs[$a].Startup.ReadySeconds } else { '-' })
        }
    }
    foreach ($key in $tot[0].Keys) { $md.Add("| $key | " + (@($runs.Keys | ForEach-Object { $tot[$_][$key] }) -join ' | ') + ' |') }
    $md.Add('')

    # Parity per checkpoint.
    $md.Add('Parity (hot-reloaded app vs a clean build of the same files):')
    $md.Add('')
    foreach ($a in $runs.Keys) {
        foreach ($row in @($runs[$a].Rows | Where-Object { $_.Checks | Where-Object { $_.Check -like 'parity *' } })) {
            foreach ($p in @($row.Checks | Where-Object { $_.Check -like 'parity *' })) {
                $v = Get-ParityVerdict $runs[$a] $p @($journeyDef.parity.ignore)
                $md.Add("- $a, after $($row.Action): $($v.Summary)")
                foreach ($l in $v.Lines) { $md.Add("  - ``$l``") }
                if ($v.Silent -gt $v.Lines.Count) { $md.Add("  - ... and $($v.Silent - $v.Lines.Count) more") }
            }
        }
    }
    $md.Add('')

    # Notes winapp printed.
    $md.Add('What winapp printed besides "applied" (notes, errors, restarts):')
    $md.Add('')
    foreach ($a in $runs.Keys) {
        foreach ($n in @($runs[$a].Startup.Notes)) { if ($n) { $md.Add("- $a, at launch: ``$n``") } }
        foreach ($row in @($runs[$a].Rows | Where-Object { $_.Kind -notin 'before', 'after' })) {
            $interesting = @(([string]$row.Detail) -split "`n" | Where-Object { $_ -match '^\s*note:|^\[watch\] (error|restart|waiting|the app exited|lost)' } | Select-Object -First 3)
            foreach ($l in $interesting) { $md.Add("- $a, $($row.Action): ``$(($l.Trim() -replace '\s+', ' ') -replace '^(.{0,240}).*$', '$1')``") }
        }
    }
    $md.Add('')
}
$text = $md -join "`n"
if ($Out) { Set-Content $Out $text -Encoding utf8 }
$text
