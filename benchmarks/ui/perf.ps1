#Requires -Version 7.4
<#
.SYNOPSIS
    Scripted UI perf benchmark, with no agent. It times single UI automation commands against real
    Calculator and WinUI 3 Gallery windows, and runs fixed command sequences that do a scenario's task.
    The scenario's own checks score each sequence.

.EXAMPLE
    ./perf.ps1 -Plan

.EXAMPLE
    ./perf.ps1 -Repeat 5 -SequenceRepeat 1

.EXAMPLE
    ./perf.ps1 -Adapter winapp,C:\bench\my-tool.adapter.json -OutDir results\perf-my-tool

.EXAMPLE
    ./perf.ps1 -Compare results\perf-before,results\perf-after
#>
[CmdletBinding()]
param(
    # Checked-in adapter names (winapp) or paths to local adapter files.
    [string[]]$Adapter,
    [ValidateRange(1, 1000)]
    [int]$Repeat,
    [ValidateRange(0, 100)]
    [int]$Warmup,
    [ValidateRange(1, 100)]
    [int]$SequenceRepeat,
    # Operation ids from perf.json to time (default: all).
    [string[]]$Operation,
    # Sequence ids from perf.json to run (default: all).
    [string[]]$Sequence,
    [switch]$SkipOperations,
    [switch]$SkipSequences,
    [string]$OutDir,
    [switch]$Plan,
    # Run even when a target app is already open outside the benchmark (its windows are never closed).
    [switch]$Force,
    [switch]$KeepArtifacts,
    [string[]]$Compare
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'benchmarks\agents\lib\Benchmark.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\UiBenchmark.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\Perf.psm1') -Force

$Adapter = Split-ListArgument $Adapter
$Operation = Split-ListArgument $Operation
$Sequence = Split-ListArgument $Sequence
$Compare = Split-ListArgument $Compare

if ($Compare) {
    if ($Compare.Count -lt 2) { throw 'Use -Compare with two or more perf result folders, e.g. -Compare results\perf-a,results\perf-b.' }
    $report = Get-PerfComparison -ResultDirs @($Compare | ForEach-Object { (Resolve-Path $_).Path })
    if ($OutDir) {
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $path = Join-Path $OutDir 'perf-comparison.md'
        Set-Content -LiteralPath $path -Value $report -Encoding utf8NoBOM
        Write-Host "Comparison: $path"
    }
    else { $report }
    return
}

$config = Get-Content -Raw (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json -AsHashtable
$allScenarios = @(Get-UiScenarios -ScenariosRoot (Join-Path $PSScriptRoot 'scenarios'))
$perf = Read-PerfConfig -Path (Join-Path $PSScriptRoot 'perf.json') -Scenarios $allScenarios
if ($PSBoundParameters.ContainsKey('Repeat')) { $perf.Repeat = $Repeat }
if ($PSBoundParameters.ContainsKey('Warmup')) { $perf.Warmup = $Warmup }
if ($PSBoundParameters.ContainsKey('SequenceRepeat')) { $perf.SequenceRepeat = $SequenceRepeat }

foreach ($pair in @(@{ Ids = $Operation; Known = @($perf.Operations.Id); Kind = 'operation' }, @{ Ids = $Sequence; Known = @($perf.Sequences.Id); Kind = 'sequence' })) {
    $unknown = @($pair.Ids | Where-Object { $_ -notin $pair.Known })
    if ($unknown) { throw "Unknown $($pair.Kind) id(s): $($unknown -join ', '). Known: $($pair.Known -join ', ')" }
}
$operations = @(if (-not $SkipOperations) { $perf.Operations | Where-Object { -not $Operation -or $_.Id -in $Operation } })
$sequences = @(if (-not $SkipSequences) { $perf.Sequences | Where-Object { -not $Sequence -or $_.Id -in $Sequence } })
if (-not $operations -and -not $sequences) { throw 'Nothing to run: no operations or sequences selected.' }

$adapterNames = if ($Adapter) { $Adapter } else { @('winapp') }
$adapters = @($adapterNames | ForEach-Object { Read-PerfAdapter -NameOrPath $_ -AdaptersRoot (Join-Path $PSScriptRoot 'adapters') })
$dupes = @($adapters | Group-Object Name | Where-Object Count -gt 1)
if ($dupes) { throw "Two adapters are named '$($dupes[0].Name)'; adapter names must be unique within a run." }

$scenarioById = @{}
foreach ($s in $allScenarios) { $scenarioById[$s.Id] = $s }
$neededApps = @(@($operations | Where-Object Scenario | ForEach-Object { $scenarioById[$_.Scenario].App }) + @($sequences | ForEach-Object { $scenarioById[$_.Scenario].App }) | Select-Object -Unique)
$runPlan = Get-PerfPlan -Config $perf -Adapters $adapters -Operations $operations -Sequences $sequences

if ($Plan) {
    Write-Host "Adapters:   $(@($adapters | ForEach-Object { if ($_.CheckedIn) { $_.Name } else { "$($_.Name) ($($_.SourcePath))" } }) -join ', ')"
    Write-Host "Operations: $(@($operations.Id) -join ', ')"
    Write-Host "            $($perf.Warmup) warm-up + $($perf.Repeat) timed calls each = $($runPlan.OperationCalls) commands"
    Write-Host "Sequences:  $(@($sequences.Id) -join ', ')"
    Write-Host "            $($perf.SequenceRepeat) run(s) each = $($runPlan.SequenceRuns) runs, $($runPlan.SequenceCalls) commands"
    Write-Host "Estimate:   ~$($runPlan.Minutes) min, no AI credits"
    foreach ($s in $runPlan.Skipped) { Write-Host "Skipped:    $s (the adapter does not implement it)" }
    if ('gallery' -in $neededApps -and -not (Get-AppxPackage -Name $config.gallery.packageName -ErrorAction SilentlyContinue)) {
        Write-Host 'Setup:      the WinUI 3 Gallery is not installed yet; run .\setup-gallery.ps1 first.'
    }
    return
}

Import-Module (Join-Path $PSScriptRoot 'lib\AppState.psm1') -Force

# --- Adapter commands and environments ------------------------------------------------------
$adapterEnv = @{}
foreach ($a in $adapters) {
    $envMap = [ordered]@{}
    foreach ($e in [Environment]::GetEnvironmentVariables().GetEnumerator()) { $envMap[[string]$e.Key] = [string]$e.Value }
    $expanded = Expand-MethodEnv -Env $a.Env
    if ($expanded.Missing) { throw "Adapter '$($a.Name)' needs these environment variables: $($expanded.Missing -join ', ')." }
    foreach ($k in $expanded.Values.Keys) { $envMap[$k] = $expanded.Values[$k] }
    $pathValue = Set-EnvironmentPath -Environment $envMap -Prepend @($a.Path)
    $pathExt = [string](Get-Field $envMap 'PATHEXT')
    $commandPath = if ($a.CommandIsPath) { $a.Command } else { Resolve-CommandOnPath -Name $a.Command -PathValue $pathValue -PathExt ($pathExt ? $pathExt : '.COM;.EXE;.BAT;.CMD') }
    if (-not $commandPath -or -not (Test-Path -LiteralPath $commandPath)) { throw "Adapter '$($a.Name)': command '$($a.Command)' was not found." }
    $adapterEnv[$a.Name] = [pscustomobject]@{ Env = $envMap; CommandPath = $commandPath; Version = $null }
}

# --- Apps, and the guard against touching windows the benchmark does not own ---------------
$apps = @{}
foreach ($name in $neededApps) { $apps[$name] = Get-AppInfo -App $name -Config $config }

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "results\perf-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$resultsPath = Join-Path $OutDir 'perf-results.json'
$summaryPath = Join-Path $OutDir 'summary.md'
$ledgerPath = Join-Path $OutDir 'owned.json'
if ($apps.Count) { Clear-OwnedLedger -Path $ledgerPath -Apps $apps -Config $config }

function Get-ForeignReport {
    @(foreach ($name in $apps.Keys) {
            foreach ($p in Get-ForeignInstances -AppInfo $apps[$name]) { "$($apps[$name].DisplayName) (pid $($p.pid))" }
        })
}
$foreignNow = @(Get-ForeignReport)
if ($foreignNow -and -not $Force) {
    throw "These apps are already open outside the benchmark: $($foreignNow -join ', '). Close them, or pass -Force to run anyway (the benchmark never closes them)."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-ui-perf\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$callIndex = 0

function Invoke-AdapterCall {
    # One timed command: the adapter's operation with step values; returns ms, exit code, and an error.
    param($A, [string]$Op, [System.Collections.IDictionary]$Values)
    $script:callIndex++
    $v = [ordered]@{}
    foreach ($k in $Values.Keys) { $v[$k] = $Values[$k] }
    $v['output'] = Join-Path $tempRoot ("out-{0:D5}.png" -f $script:callIndex)
    $inv = Get-AdapterInvocation -Adapter $A -Operation $Op -Values $v
    if (-not $inv) { return [pscustomobject]@{ ms = $null; exitCode = $null; error = "adapter does not implement '$Op'" } }
    $out = Join-Path $tempRoot ("call-{0:D5}.out" -f $script:callIndex)
    $err = Join-Path $tempRoot ("call-{0:D5}.err" -f $script:callIndex)
    $r = Invoke-LoggedProcess -FilePath $adapterEnv[$A.Name].CommandPath -Arguments $inv.Arguments -WorkingDirectory $tempRoot `
        -Environment $adapterEnv[$A.Name].Env -StdoutPath $out -StderrPath $err -TimeoutSeconds $perf.TimeoutSeconds
    $problem = if ($r.TimedOut) { "timed out after $($perf.TimeoutSeconds) s" }
    elseif ($r.ExitCode -ne 0) {
        $tail = (Get-FileTail $err 300).Trim()
        if (-not $tail) { $tail = (Get-FileTail $out 300).Trim() }
        "exit $($r.ExitCode): $tail"
    }
    $result = [pscustomobject]@{ ms = [double]$r.DurationMs; exitCode = $r.ExitCode; error = $problem; stdout = $out }
    if (-not $KeepArtifacts) { Remove-Item -LiteralPath $err, $v['output'] -ErrorAction SilentlyContinue }
    return $result
}

foreach ($a in $adapters) {
    if ($a.Operations.Contains('version')) {
        $r = Invoke-AdapterCall -A $a -Op 'version' -Values @{}
        if (-not $r.error) { $adapterEnv[$a.Name].Version = (Get-Content -Raw -LiteralPath $r.stdout).Trim() }
    }
}

$results = [ordered]@{
    started      = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    finished     = $null
    stoppedEarly = $null
    environment  = Get-EnvironmentInfo -RepoRoot $repoRoot
    adapters     = @($adapters | ForEach-Object { [ordered]@{ name = $_.Name; displayName = $_.DisplayName; version = $adapterEnv[$_.Name].Version; commandPath = $adapterEnv[$_.Name].CommandPath; hash = $_.Hash; checkedIn = $_.CheckedIn } })
    apps         = @($apps.Values | ForEach-Object { [ordered]@{ app = $_.App; package = $_.PackageName; version = $_.Version; architecture = $_.Architecture } })
    settings     = [ordered]@{ repeat = $perf.Repeat; warmup = $perf.Warmup; sequenceRepeat = $perf.SequenceRepeat; timeoutSeconds = $perf.TimeoutSeconds }
    operations   = [System.Collections.Generic.List[object]]::new()
    sequences    = [System.Collections.Generic.List[object]]::new()
}
function Save-Results {
    $json = $results | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath $resultsPath -Value $json -Encoding utf8NoBOM
    Write-PerfSummary -Results ($json | ConvertFrom-Json) -Path $summaryPath
}

function Get-TargetInstance {
    # The setup instance a step acts on: the named role, or the scenario's target.
    param([object[]]$Setup, [string]$Role)
    $hit = @(if ($Role) { $Setup | Where-Object role -eq $Role } else { $Setup | Where-Object target })
    if (-not $hit) { $hit = @($Setup) }
    return $hit[0]
}

function Close-Owned {
    # Closes what this phase launched, by pid; returns an error message when something stayed open.
    param([System.Collections.Generic.List[object]]$Owned)
    $closed = @(Stop-OwnedEntries -Owned @($Owned) -Apps $apps -Config $config)
    $stuck = @($closed | Where-Object result -eq 'still-running')
    $Owned.Clear()
    if ($stuck) { return "could not close pid(s) $(@($stuck.pid) -join ', ')" }
    Save-OwnedLedger -Path $ledgerPath
    return $null
}

Write-Host ("Perf: {0} | {1} timed commands, {2} sequence runs (~{3} min) | results: {4}" -f (@($adapters.Name) -join ', '), $runPlan.OperationCalls, $runPlan.SequenceRuns, $runPlan.Minutes, $OutDir)
$abort = $null
try {
    # --- Operations: one window per scenario, every operation timed against it -----------------
    $byScenario = @($operations | Group-Object { $_.Scenario })
    foreach ($group in $byScenario) {
        if ($abort) { break }
        $owned = [System.Collections.Generic.List[object]]::new()
        $setup = @()
        try {
            if ($group.Name) {
                $s = $scenarioById[$group.Name]
                $info = $apps[$s.App]
                if (@(Get-ForeignInstances -AppInfo $info) -and -not $Force) { throw "$($info.DisplayName) was opened outside the benchmark; stopping." }
                Write-Host "Setting up $($s.Id) ..."
                $setup = @(Start-ScenarioInstances -AppInfo $info -Scenario $s -Owned $owned -LedgerPath $ledgerPath)
            }
            foreach ($o in $group.Group) {
                $values = [ordered]@{}
                foreach ($k in $o.Params.Keys) { $values[$k] = $o.Params[$k] }
                if ($setup) {
                    $t = Get-TargetInstance -Setup $setup -Role $o.Instance
                    $values['pid'] = [string]$t.pid
                    $values['hwnd'] = [string][int64]$t.hwnd
                }
                foreach ($a in $adapters) {
                    if (-not $a.Operations.Contains($o.Op)) { continue }
                    $samples = [System.Collections.Generic.List[double]]::new()
                    $errors = 0; $firstError = $null
                    foreach ($n in 1..($perf.Warmup + $perf.Repeat)) {
                        $r = Invoke-AdapterCall -A $a -Op $o.Op -Values $values
                        if (-not $KeepArtifacts) { Remove-Item -LiteralPath $r.stdout -ErrorAction SilentlyContinue }
                        if ($n -le $perf.Warmup) { continue }
                        if ($r.error) { $errors++; if (-not $firstError) { $firstError = $r.error }; continue }
                        $samples.Add($r.ms)
                    }
                    $stats = Get-PerfStats -Values $samples.ToArray()
                    $results.operations.Add([ordered]@{ id = $o.Id; adapter = $a.Name; op = $o.Op; scenario = $o.Scenario; samples = @($samples); errors = $errors; firstError = $firstError; stats = $stats })
                    Write-Host ("  {0,-20} {1,-10} median {2,9}  p95 {3,9}  errors {4}" -f $o.Id, $a.Name, (Format-PerfMs $stats.median), (Format-PerfMs $stats.p95), $errors)
                }
            }
        }
        catch {
            $abort = "operations on $($group.Name): $($_.Exception.Message)"
        }
        finally {
            $stuck = Close-Owned $owned
            if ($stuck) { $abort = "$stuck; stopping so the next step does not start on a dirty desktop." }
            Save-Results
        }
    }

    # --- Sequences: set up, run the steps, score with the scenario's checks, tear down ---------
    :outer foreach ($iteration in 1..$perf.SequenceRepeat) {
        foreach ($q in $sequences) {
            foreach ($a in $adapters) {
                if ($abort) { break outer }
                $missing = @($q.Steps | Where-Object { -not $a.Operations.Contains($_.Op) })
                if ($missing) { continue }
                $s = $scenarioById[$q.Scenario]
                $info = $apps[$s.App]
                $rec = [ordered]@{ id = $q.Id; adapter = $a.Name; iteration = $iteration; status = $null; reason = $null; commandMs = $null; steps = @(); failures = @(); collateral = @() }
                $owned = [System.Collections.Generic.List[object]]::new()
                Write-Host "[$iteration/$($perf.SequenceRepeat)] $($q.Id) | $($a.Name) ..." -NoNewline
                try {
                    $foreign = @(Get-ForeignInstances -AppInfo $info)
                    if ($foreign -and -not $Force) { $abort = "$($info.DisplayName) was opened outside the benchmark; stopping."; throw $abort }
                    $setup = @(Start-ScenarioInstances -AppInfo $info -Scenario $s -Owned $owned -LedgerPath $ledgerPath)
                    $before = Get-SetupSnapshot -AppInfo $info -Setup $setup -Foreign $foreign
                    $steps = [System.Collections.Generic.List[object]]::new()
                    $total = 0.0
                    $stepError = $null
                    foreach ($st in $q.Steps) {
                        $t = Get-TargetInstance -Setup $setup -Role $st.Instance
                        $values = [ordered]@{ pid = [string]$t.pid; hwnd = [string][int64]$t.hwnd }
                        foreach ($k in $st.Params.Keys) { $values[$k] = $st.Params[$k] }
                        $label = if ($st.Params.Contains('selector')) { "$($st.Op) $($st.Params.selector)" } else { $st.Op }
                        $r = Invoke-AdapterCall -A $a -Op $st.Op -Values $values
                        if (-not $KeepArtifacts) { Remove-Item -LiteralPath $r.stdout -ErrorAction SilentlyContinue }
                        $steps.Add([ordered]@{ label = $label; op = $st.Op; ms = $r.ms; exitCode = $r.exitCode })
                        $total += $r.ms
                        if ($r.error) { $stepError = "step $($steps.Count) ($label): $($r.error)"; break }
                    }
                    $rec.steps = @($steps)
                    $rec.commandMs = [Math]::Round($total, 0)
                    $after = Get-AppSnapshot -AppInfo $info -Known $setup -Foreign $foreign -RestoreOwnedMinimized
                    Add-StartedProcesses -AppInfo $info -Before $before -After $after -Owned $owned -LedgerPath $ledgerPath
                    $eval = Test-ScenarioState -Scenario $s -Before $before -After $after
                    $rec.failures = @($eval.Failures)
                    $rec.collateral = @($eval.Collateral)
                    if ($stepError) { $rec.status = 'fail'; $rec.reason = (@($stepError) + @($eval.Failures)) -join '; ' }
                    else {
                        $rec.status = $eval.Status
                        $rec.reason = (@($eval.Failures) + @($eval.Collateral | ForEach-Object { "$($_.type): $($_.detail)" })) -join '; '
                    }
                }
                catch {
                    $rec.status = 'harness_error'
                    $rec.reason = $_.Exception.Message
                }
                finally {
                    $stuck = Close-Owned $owned
                    if ($stuck) {
                        $rec.reason = "[$($rec.status)] $($rec.reason) | $stuck"
                        $rec.status = 'cleanup_failed'
                        $abort = "$stuck; stopping so the next run does not start on a dirty desktop."
                    }
                    $results.sequences.Add($rec)
                    Save-Results
                }
                Write-Host " $($rec.status) | $(Format-PerfMs $rec.commandMs) | $(@($rec.steps).Count) commands"
                if ($rec.status -ne 'pass' -and $rec.reason) { Write-Host "    $($rec.reason)" }
            }
        }
    }
}
finally {
    if ($abort) {
        Write-Warning "Stopping: $abort"
        $results.stoppedEarly = $abort
    }
    $results.finished = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    Save-Results
    if (-not $KeepArtifacts) { Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue }
    Write-Host "Summary: $summaryPath"
}
