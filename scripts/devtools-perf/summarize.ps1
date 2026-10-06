# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    Turn devtools-perf results.json into a markdown summary with budget checks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Results,
    [string]$Budgets = (Join-Path $PSScriptRoot 'budgets.json'),
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
# Several result files (e.g. one per app plus a soak run) merge into one report; the first file's
# environment and parameters head the report, and later files add apps or stress sections.
$r = Get-Content -Raw $Results[0] | ConvertFrom-Json
foreach ($more in $Results | Select-Object -Skip 1) {
    $x = Get-Content -Raw $more | ConvertFrom-Json
    foreach ($a in $x.apps.PSObject.Properties) {
        $existing = $r.apps.PSObject.Properties[$a.Name]
        if (-not $existing) { $r.apps | Add-Member -NotePropertyName $a.Name -NotePropertyValue $a.Value; continue }
        $hasRuns = @($a.Value.runs.PSObject.Properties | Where-Object { @($_.Value).Count -gt 0 }).Count -gt 0
        if ($hasRuns) {
            # A second measured run of the same app (e.g. -StartupOnly) gets its own section, named after its folder.
            $r.apps | Add-Member -NotePropertyName "$($a.Name) ($(Split-Path -Leaf (Split-Path -Parent (Resolve-Path $more))))" -NotePropertyValue $a.Value
            continue
        }
        foreach ($s in $a.Value.stress.PSObject.Properties) { $existing.Value.stress | Add-Member -NotePropertyName $s.Name -NotePropertyValue $s.Value -Force }
    }
    if ($x.environment.commit -ne $r.environment.commit) { Write-Warning "$more was measured on a different commit ($($x.environment.commit))." }
}
$b = Get-Content -Raw $Budgets | ConvertFrom-Json

function Median([double[]]$v) {
    $s = @($v | Where-Object { $null -ne $_ } | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { return $s[[int](($s.Count - 1) / 2)] }
    ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}
function P95([double[]]$v) {
    $s = @($v | Where-Object { $null -ne $_ } | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    $s[[Math]::Min($s.Count - 1, [int][Math]::Ceiling(0.95 * $s.Count) - 1)]
}
function Fmt($v, [int]$Digits = 1) { if ($null -eq $v) { '–' } else { [Math]::Round([double]$v, $Digits).ToString("N$Digits") } }
function Signed($v, [int]$Digits = 1) { if ($null -eq $v) { '–' } elseif ($v -ge 0) { '+' + (Fmt $v $Digits) } else { Fmt $v $Digits } }
function Get-Values($Runs, [scriptblock]$Pick) {
    @(foreach ($run in @($Runs)) { try { $x = & $Pick $run; if ($null -ne $x) { [double]$x } } catch { } })
}

# Per-run metrics. Each picks one number from one launch; the table shows the median across
# launches (p95 across launches in parentheses when there are 3 or more).
$metrics = @(
    @{ Group = 'Startup'; Name = '`winapp run` (no build), ms'; Pick = { $args[0].startup.runMs } }
    @{ Group = 'Startup'; Name = 'Launch → first window, ms'; Pick = { $args[0].startup.launchToWindowMs } }
    @{ Group = 'Startup'; Name = 'Process start → first window, ms'; Pick = { $args[0].startup.processToWindowMs } }
    @{ Group = 'Startup'; Name = 'Process start → first frame (in app), ms'; Pick = { $args[0].startup.processToFirstFrameMs } }
    @{ Group = 'Startup'; Name = 'Launch → UI settled, ms'; Pick = { $args[0].startup.launchToSettledMs } }
    @{ Group = 'Startup'; Name = 'First window → UI settled, ms'; Pick = { $args[0].startup.windowToSettledMs } }
    @{ Group = 'Startup'; Name = 'Longest UI stall in first 10 s, ms'; Pick = { $args[0].startup.startupMaxPingMs } }
    @{ Group = 'Idle'; Name = 'Idle CPU, % of one core'; Pick = { $args[0].idle.cpuPercentOfOneCore }; Digits = 2 }
    @{ Group = 'Idle'; Name = 'Steady private bytes, MB'; Pick = { $args[0].idle.privateMB } }
    @{ Group = 'Idle'; Name = 'Steady working set, MB'; Pick = { $args[0].idle.workingSetMB } }
    @{ Group = 'Scroll 10k ListView'; Name = 'Missed frames'; Pick = { $args[0].scroll.missedFrames } }
    @{ Group = 'Scroll 10k ListView'; Name = 'Frame gap p95, ms'; Pick = { $args[0].scroll.frameGapMs.p95 } }
    @{ Group = 'Scroll 10k ListView'; Name = 'UI dispatch lag p95, ms'; Pick = { $args[0].scroll.uiLagMs.p95 }; Digits = 2 }
    @{ Group = 'Element churn (collapsed)'; Name = 'Create+remove time, ms'; Pick = { $args[0].churn.ms } }
    @{ Group = 'Element churn (collapsed)'; Name = 'Retained bytes / element'; Pick = { $args[0].churn.retainedBytesPerElement }; Digits = 0 }
    @{ Group = 'Element churn (visible)'; Name = 'Create+render+remove time, ms'; Pick = { $args[0].churnVisible.ms } }
    @{ Group = 'Element churn (visible)'; Name = 'Retained bytes / element'; Pick = { $args[0].churnVisible.retainedBytesPerElement }; Digits = 0 }
    @{ Group = 'Navigation loop'; Name = 'Heavy page nav p50, ms'; Pick = { $args[0].nav.navMs.p50 } }
    @{ Group = 'Navigation loop'; Name = 'Heavy page nav p95, ms'; Pick = { $args[0].nav.navMs.p95 } }
    @{ Group = 'Navigation loop'; Name = 'UI dispatch lag p95, ms'; Pick = { $args[0].nav.frames.uiLagMs.p95 } }
    @{ Group = 'Navigation loop'; Name = 'Private growth / cycle, MB'; Pick = { ($args[0].nav.after.privateMB - $args[0].nav.before.privateMB) / $args[0].nav.cycles }; Digits = 2 }
    @{ Group = 'Large tree'; Name = 'Build 4k elements, ms'; Pick = { if ($args[0].bigtree) { @($args[0].bigtree)[0].navMs } } }
    @{ Group = 'Large tree'; Name = 'Build 20k elements, ms'; Pick = { if ($args[0].bigtree) { @($args[0].bigtree)[1].navMs } } }
    @{ Group = 'Large tree'; Name = '20k elements loaded, private MB added'; Pick = { if ($args[0].bigtree) { $t = @($args[0].bigtree)[1]; $t.loaded.privateMB - $t.before.privateMB } } }
    @{ Group = 'Navigation walk'; Name = 'Pages visited'; Pick = { $args[0].navWalk.pagesVisited }; Digits = 0 }
    @{ Group = 'Navigation walk'; Name = 'Private growth over walk, MB'; Pick = { $args[0].navWalk.walkGrowthMB } }
    @{ Group = 'Navigation walk'; Name = 'Private MB after walk'; Pick = { $args[0].navWalk.afterWalk.privateMB } }
    @{ Group = 'Navigation walk'; Name = 'UI ping p95 during walk, ms'; Pick = { $args[0].navWalk.walkPingMs.p95 } }
    @{ Group = 'Navigation walk'; Name = 'UI pings > 50 ms during walk'; Pick = { $args[0].navWalk.walkPingsOver50ms }; Digits = 0 }
    @{ Group = 'Navigation walk'; Name = 'Away-and-back growth / cycle, MB'; Pick = { $args[0].navWalk.loopGrowthMBPerCycle }; Digits = 2 }
)

$sb = [Text.StringBuilder]::new()
function L([string]$s = '') { [void]$sb.AppendLine($s) }
$env = $r.environment
L '# WinUI DevTools performance'
L
L "Commit ``$($env.commit.Substring(0, 12))`` · winapp $($env.winapp) · $($env.os) · $($env.cpu), $($env.logicalCores) logical cores, $($env.memoryGB) GB$(if ($env.ci) { ' · CI' })"
L
$p = $r.parameters
L "Warm starts, window 1280x800, $($p.repetitions) launches per app and mode (modes rotated), idle window $($p.idleSeconds) s. ``off`` = ``winapp run``; ``devtools`` = ``--devtools``; ``engine`` = ``--devtools --no-overlay``; ``alias`` = ``--with-alias`` (DevTools' launch path without DevTools); ``sourceinfo`` = ``alias`` + ``ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1``. Δ is the difference of medians against ``off``."
L

$checks = [Collections.Generic.List[object]]::new()
function Check([string]$App, [string]$Mode, [string]$Id, $Value) {
    $budget = $b.$Id
    if (-not $budget -or $null -eq $Value) { return }
    $checks.Add([pscustomobject]@{ App = $App; Mode = $Mode; Id = $Id; Value = [double]$Value; Max = [double]$budget.max; Description = $budget.description; Pass = ([double]$Value -le [double]$budget.max) })
}

foreach ($appName in $r.apps.PSObject.Properties.Name) {
    $app = $r.apps.$appName
    $modes = @($app.runs.PSObject.Properties.Name)
    $others = @($modes | Where-Object { $_ -ne 'off' })
    L "## $appName"
    L
    $header = '| Metric | ' + (($modes | ForEach-Object { if ($_ -eq 'off') { 'off' } else { "$_ | Δ $_" } }) -join ' | ') + ' |'
    L $header
    L ('|---|' + (($modes | ForEach-Object { if ($_ -eq 'off') { '---:' } else { '---:|---:' } }) -join '|') + '|')
    $build = @{}
    foreach ($m in $modes) { if ($app.build.$m) { $build[$m] = $app.build.$m.runWithBuildMs.median } }
    if ($build.Count) {
        $cells = foreach ($m in $modes) {
            if ($m -eq 'off') { Fmt $build[$m] 0 } else { (Fmt $build[$m] 0); if ($null -ne $build[$m] -and $null -ne $build['off']) { Signed ($build[$m] - $build['off']) 0 } else { '–' } }
        }
        L "| **Build** ``winapp run`` with warm incremental build, ms | $($cells -join ' | ') |"
    }
    $group = ''
    foreach ($metric in $metrics) {
        $vals = @{}
        foreach ($m in $modes) { $vals[$m] = Get-Values $app.runs.$m $metric.Pick }
        if (-not ($vals.Values | Where-Object { $_.Count -gt 0 })) { continue }
        $d = if ($metric.Digits -ne $null) { $metric.Digits } else { 1 }
        $label = if ($metric.Group -ne $group) { "**$($metric.Group)** $($metric.Name)" } else { $metric.Name }
        $group = $metric.Group
        $cells = foreach ($m in $modes) {
            $med = Median $vals[$m]
            $cell = Fmt $med $d
            if ($vals[$m].Count -ge 3) { $cell += " ($(Fmt (P95 $vals[$m]) $d))" }
            if ($m -eq 'off') { $cell } else { $cell; $off = Median $vals['off']; if ($null -ne $med -and $null -ne $off) { Signed ($med - $off) $d } else { '–' } }
        }
        L "| $label | $($cells -join ' | ') |"
    }
    L
    if (@($app.errors).Count) {
        $failures = (@($app.errors) | ForEach-Object { '`' + $_ + '`' }) -join '; '
        L "Launches that failed and were skipped: $failures"
        L
    }

    # Budget checks per DevTools mode
    foreach ($m in @($others | Where-Object { $_ -in 'devtools', 'engine' })) {
        $runs = $app.runs.$m; $offRuns = $app.runs.off
        $delta = { param($pick) $a = Median (Get-Values $runs $pick); $o = Median (Get-Values $offRuns $pick); if ($null -ne $a -and $null -ne $o) { $a - $o } }
        $pct = { param($pick) $a = Median (Get-Values $runs $pick); $o = Median (Get-Values $offRuns $pick); if ($null -ne $a -and $o) { ($a - $o) / $o * 100 } }
        Check $appName $m 'startup.runDeltaMs' (& $delta { $args[0].startup.runMs })
        Check $appName $m 'startup.firstWindowDeltaMs' (& $delta { $args[0].startup.processToWindowMs })
        Check $appName $m 'startup.settledDeltaMs' (& $delta { $args[0].startup.launchToSettledMs })
        if ($app.build.$m -and $app.build.off) { Check $appName $m 'build.runWithBuildDeltaMs' ($app.build.$m.runWithBuildMs.median - $app.build.off.runWithBuildMs.median) }
        Check $appName $m 'idle.cpuDeltaPercent' (& $delta { $args[0].idle.cpuPercentOfOneCore })
        $memMB = & $delta { $args[0].idle.privateMB }
        $memPct = & $pct { $args[0].idle.privateMB }
        $mb = $b.'idle.privateDelta'
        if ($mb -and $null -ne $memMB) {
            $checks.Add([pscustomobject]@{ App = $appName; Mode = $m; Id = 'idle.privateDelta'; Value = $memMB; Max = [double]$mb.maxMB
                Description = "$($mb.description) — $(Fmt $memPct 1)% of off, limit $($mb.maxPercent)%"
                Pass = ($memMB -le $mb.maxMB -or $memPct -le $mb.maxPercent) })
        }
        Check $appName $m 'scroll.missedFramesDelta' (& $delta { $args[0].scroll.missedFrames })
        Check $appName $m 'scroll.uiLagP95DeltaMs' (& $delta { $args[0].scroll.uiLagMs.p95 })
        Check $appName $m 'nav.navP50DeltaPercent' (& $pct { $args[0].nav.navMs.p50 })
        Check $appName $m 'churn.createDeltaPercent' (& $pct { $args[0].churn.ms })
        Check $appName $m 'churn.retainedBytesPerElement' (Median (Get-Values $runs { $args[0].churn.retainedBytesPerElement }))
        Check $appName $m 'walk.pingP95DeltaMs' (& $delta { $args[0].navWalk.walkPingMs.p95 })
        Check $appName $m 'walk.growthDeltaMB' (& $delta { $args[0].navWalk.walkGrowthMB })
    }

    # Interaction cost
    $ix = $app.'interaction-devtools'
    if ($ix -and $ix.interaction -and -not $ix.interaction.error) {
        $i = $ix.interaction
        L "### $appName · interaction cost (``--devtools``)"
        L
        L '| Operation | Protocol round trip median (p95), ms | Engine UI-thread time per call, mean (max) ms | UI stall in window, ms |'
        L '|---|---:|---:|---:|'
        $row = { param($label, $rt, $sites, $site, $jank)
            $s = if ($sites -and $sites.$site) { "$(Fmt $sites.$site.meanMs 2) ($(Fmt $sites.$site.maxMs 1))" } else { '–' }
            $rtCell = if ($rt) { "$(Fmt $rt.median 1) ($(Fmt $rt.p95 1))" } else { '–' }
            L "| $label | $rtCell | $s | $(Fmt ($jank.stalledUs / 1000) 0) |"
        }
        & $row 'Hover highlight' $i.highlight.roundTripMs $i.highlight.ui.sites 'overlay.highlight' $i.highlight.ui.jank
        if (-not $i.pick.error) { & $row 'Pick (hit test)' $i.pick.roundTripMs $i.pick.ui.sites 'overlay.pick' $i.pick.ui.jank }
        $insp = $i.inspector
        $inspUi = ($insp.ui.sites.PSObject.Properties | Where-Object { $_.Name -like 'win.*' } | Measure-Object -Property { $_.Value.totalMs } -Sum).Sum
        L "| Open inspector + select | $(Fmt $insp.openRoundTripMs 0) + $(Fmt $insp.selectRoundTripMs 0) | $(Fmt $inspUi 0) total in ``win.*`` | $(Fmt ($insp.ui.jank.stalledUs / 1000) 0) |"
        & $row 'Live property edit (inspector open)' $i.propertyEdit.roundTripMs $i.propertyEdit.ui.sites 'win.propWrite' $i.propertyEdit.ui.jank
        if ($ix.commentSave) {
            $c = $ix.commentSave
            L "| Comment save (``winapp devtools comments add``) | $(Fmt $c.cliMs.median 0) ($(Fmt $c.cliMs.p95 0)) | $(if ($c.ui.sites.'overlay.setComments') { Fmt $c.ui.sites.'overlay.setComments'.meanMs 2 } else { '–' }) (pins refresh) | $(Fmt ($c.ui.jank.stalledUs / 1000) 0) |"
        }
        L
        L "Inspector open: private bytes $(Signed $insp.privateDeltaMB) MB, working set $(Signed $insp.workingSetDeltaMB) MB; idle CPU with the inspector open $(Fmt $i.inspectorIdle.cpuPercentOfOneCore 2)% of one core."
        L
        Check $appName 'devtools' 'interaction.highlightMeanMs' $i.highlight.ui.sites.'overlay.highlight'.meanMs
        if (-not $i.pick.error) { Check $appName 'devtools' 'interaction.pickMeanMs' $i.pick.ui.sites.'overlay.pick'.meanMs }
        Check $appName 'devtools' 'interaction.inspectorStallMs' ($insp.ui.jank.stalledUs / 1000)
        Check $appName 'devtools' 'interaction.propertyEditP95Ms' $i.propertyEdit.roundTripMs.p95
        if ($ix.commentSave) { Check $appName 'devtools' 'interaction.commentSaveP95Ms' $ix.commentSave.cliMs.p95 }
    }

    # Attribution
    $attr = @()
    foreach ($m in 'devtools', 'engine') { if ($app."interaction-$m".attribution) { $attr += [pscustomobject]@{ Mode = $m; A = $app."interaction-$m".attribution } } }
    if ($attr.Count) {
        L "### $appName · where the time goes"
        L
        L 'Engine UI-thread time (the engine''s own instrumented sites) during each scenario, against the scenario''s wall time. Time the scenario got slower that is not in these sites is WinUI''s XAML diagnostics work on the engine''s behalf (visual-tree change notifications, source info).'
        L
        L '| Mode | Scenario | Wall time, ms | Engine sites total, ms | Top sites (total ms) |'
        L '|---|---|---:|---:|---|'
        foreach ($a in $attr) {
            foreach ($s in $a.A.PSObject.Properties) {
                $sites = $s.Value.engine.sites
                $total = ($sites.PSObject.Properties | ForEach-Object { $_.Value.totalMs } | Measure-Object -Sum).Sum
                $top = ($sites.PSObject.Properties | Select-Object -First 4 | ForEach-Object { "``$($_.Name)`` $(Fmt $_.Value.totalMs 1)" }) -join ', '
                $sc = $s.Value.scenario
                $wall = if ($s.Value.wallMs) { $s.Value.wallMs } elseif ($sc.ms) { $sc.ms } elseif ($sc.seconds) { $sc.seconds * 1000 } elseif ($sc.frames.seconds) { $sc.frames.seconds * 1000 } else { $null }
                L "| $($a.Mode) | $($s.Name) | $(Fmt $wall 0) | $(Fmt $total 1) | $top |"
            }
        }
        L
    }

    # Stress
    if ($app.stress.comments) {
        $s = $app.stress.comments
        $runs = @($s.runs)
        $base = Median (Get-Values $app.runs.devtools { $args[0].startup.launchToSettledMs })
        $with = Median (Get-Values $runs { $args[0].startup.launchToSettledMs })
        $first = $runs[0]
        $set = $first.commentSave.ui.sites.'overlay.setComments'
        L "### $appName · $($s.count) comments in the store"
        L
        L "| Metric | Value |"
        L "|---|---:|"
        L "| Launch → UI settled, ms (Δ vs ``--devtools`` with an empty store) | $(Fmt $with 0) ($(Signed ($with - $base) 0)) |"
        L "| Steady private bytes, MB (Δ) | $(Fmt (Median (Get-Values $runs { $args[0].idle.privateMB }))) ($(Signed ((Median (Get-Values $runs { $args[0].idle.privateMB })) - (Median (Get-Values $app.runs.devtools { $args[0].idle.privateMB }))))) |"
        L "| Idle CPU, % of one core | $(Fmt (Median (Get-Values $runs { $args[0].idle.cpuPercentOfOneCore })) 2) |"
        if ($set) { L "| Pins refresh on save (``overlay.setComments``), mean / max ms | $(Fmt $set.meanMs 2) / $(Fmt $set.maxMs 1) |" }
        L "| Comment save p95, ms | $(Fmt $first.commentSave.cliMs.p95 0) |"
        L "| ``winapp devtools comments list``, ms | $(Fmt $first.listMs 0) |"
        L "| Heavy page nav p50, ms (Δ vs empty store) | $(Fmt $first.nav.navMs.p50 0) ($(Signed ($first.nav.navMs.p50 - (Median (Get-Values $app.runs.devtools { $args[0].nav.navMs.p50 }))) 0)) |"
        L
        if ($set) { Check $appName 'devtools' 'stress.commentsSetMeanMs' $set.meanMs }
    }
    $soak = @($app.stress.PSObject.Properties | Where-Object { $_.Name -like 'soak-*' })
    if ($soak.Count) {
        L "### $appName · soak"
        L
        L '| Mode | Minutes | Private MB first → last (min over last 5 samples) | Growth, MB/hour | CPU seconds |'
        L '|---|---:|---|---:|---:|'
        $slopes = @{}
        foreach ($s in $soak) {
            $samples = @($s.Value.samples)
            if ($samples.Count -lt 4) { continue }
            # Least-squares slope over sample index, converted to per hour using the soak length.
            $n = $samples.Count; $xs = 0..($n - 1); $ys = $samples | ForEach-Object { $_.privateMB }
            $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
            $num = 0; $den = 0; for ($k = 0; $k -lt $n; $k++) { $num += ($xs[$k] - $mx) * ($ys[$k] - $my); $den += ($xs[$k] - $mx) * ($xs[$k] - $mx) }
            $perHour = ($num / $den) * ($n / $s.Value.minutes) * 60
            $mode = $s.Name.Substring(5)
            $slopes[$mode] = $perHour
            $tailMin = ($ys | Select-Object -Last 5 | Measure-Object -Minimum).Minimum
            L "| $mode | $($s.Value.minutes) | $(Fmt $ys[0]) → $(Fmt $ys[-1]) ($(Fmt $tailMin)) | $(Fmt $perHour 1) | $(Fmt $s.Value.cpuSeconds 0) |"
        }
        L
        if ($slopes.ContainsKey('devtools') -and $slopes.ContainsKey('off')) { Check $appName 'devtools' 'soak.growthDeltaMBPerHour' ($slopes['devtools'] - $slopes['off']) }
    }
}

if ($checks.Count) {
    L '## Budgets'
    L
    L '| App | Mode | Budget | Measured | Limit | Result |'
    L '|---|---|---|---:|---:|---|'
    foreach ($c in $checks) {
        L "| $($c.App) | $($c.Mode) | $($c.Description) | $(Fmt $c.Value 2) | $(Fmt $c.Max 2) | $(if ($c.Pass) { 'pass' } else { '**FAIL**' }) |"
    }
    L
    $failed = @($checks | Where-Object { -not $_.Pass }).Count
    L "$($checks.Count - $failed) of $($checks.Count) budget checks pass. Budgets live in ``scripts/devtools-perf/budgets.json``."
}

Set-Content -Path $Out -Value $sb.ToString()
