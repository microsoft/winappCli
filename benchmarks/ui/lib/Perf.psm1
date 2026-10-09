Set-StrictMode -Version Latest

# Pure logic of the scripted UI perf benchmark: adapter and perf.json definitions, argument templates,
# statistics, and reports. Nothing here touches the desktop; perf.ps1 and lib\AppState.psm1 do.

Import-Module (Join-Path $PSScriptRoot '..\..\agents\lib\Benchmark.psm1')
Import-Module (Join-Path $PSScriptRoot 'UiBenchmark.psm1')

# Operations an adapter can implement, and the step fields each one requires.
$script:Operations = [ordered]@{
    'version'      = @()
    'list-windows' = @()
    'search'       = @('selector')
    'inspect'      = @()
    'get-property' = @('selector')
    'get-value'    = @('selector')
    'invoke'       = @('selector')
    'set-value'    = @('selector', 'value')
    'wait-for'     = @('selector')
    'focus'        = @('selector')
    'screenshot'   = @()
}
# Placeholders an argument template may use. pid and hwnd come from the target window; output is a
# fresh file path per call; adapterDir is the folder of the adapter file.
$script:Placeholders = @('pid', 'hwnd', 'selector', 'type', 'value', 'property', 'action', 'output', 'timeout', 'adapterDir')
$script:StepParams = @('selector', 'type', 'value', 'property', 'action', 'timeout')
$script:AdapterKeys = @('$schema', 'name', 'displayName', 'description', 'command', 'prefixArgs', 'env', 'path', 'operations')
$script:ConfigKeys = @('$schema', 'repeat', 'warmup', 'sequenceRepeat', 'timeoutSeconds', 'operations', 'sequences')
$script:OperationKeys = @('id', 'op', 'scenario', 'instance', 'note') + $script:StepParams
$script:SequenceKeys = @('id', 'scenario', 'description', 'steps')
$script:PlaceholderPattern = '\{([A-Za-z]+)\}'

#region Adapters

function Get-TemplatePlaceholders {
    # Placeholder names in one template entry (a string, or an optional group of strings).
    param($Entry)
    @(foreach ($s in @($Entry)) { foreach ($m in [regex]::Matches([string]$s, $script:PlaceholderPattern)) { $m.Groups[1].Value } })
}

function Test-PerfAdapterDefinition {
    # Schema errors of one adapter file (as a hashtable); empty when valid.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition)
    $e = [System.Collections.Generic.List[string]]::new()
    $d = $Definition
    foreach ($k in $d.Keys) { if ($k -notin $script:AdapterKeys) { $e.Add("unknown field '$k'") } }
    foreach ($k in 'name', 'command', 'operations') {
        if (-not $d.Contains($k) -or $null -eq $d[$k] -or ($d[$k] -is [string] -and -not $d[$k].Trim())) { $e.Add("missing '$k'") }
    }
    if ($e.Count) { return @($e) }
    if ([string]$d.name -notmatch '^[a-z0-9][a-z0-9-]*$') { $e.Add("name '$($d.name)' must be lowercase letters, digits, and dashes") }
    if ($d.command -isnot [string]) { $e.Add('command must be a string') }
    foreach ($k in 'prefixArgs', 'path') {
        if ($d.Contains($k) -and @($d[$k] | Where-Object { $_ -isnot [string] }).Count) { $e.Add("$k must be a list of strings") }
    }
    if ($d.Contains('env') -and $d.env -isnot [System.Collections.IDictionary]) { $e.Add('env must be an object of name/value strings') }
    if ($d.operations -isnot [System.Collections.IDictionary]) { $e.Add('operations must be an object mapping operation names to argument lists'); return @($e) }
    if (-not $d.operations.Count) { $e.Add('operations must not be empty') }
    foreach ($op in $d.operations.Keys) {
        if (-not $script:Operations.Contains($op)) { $e.Add("unknown operation '$op' (known: $(@($script:Operations.Keys) -join ', '))"); continue }
        $template = @($d.operations[$op])
        foreach ($entry in $template) {
            $isGroup = $entry -is [System.Collections.IList]
            if (-not $isGroup -and $entry -isnot [string]) { $e.Add("operation '$op': arguments must be strings or lists of strings"); continue }
            if ($isGroup -and @($entry | Where-Object { $_ -isnot [string] }).Count) { $e.Add("operation '$op': optional groups must contain only strings") }
            foreach ($p in Get-TemplatePlaceholders $entry) {
                if ($p -notin $script:Placeholders) { $e.Add("operation '$op': unknown placeholder '{$p}' (known: $($script:Placeholders -join ', '))") }
            }
        }
    }
    return @($e)
}

function Read-PerfAdapter {
    # Loads a checked-in adapter (adapters\<name>.json) or a local adapter file by path.
    param([Parameter(Mandatory)][string]$NameOrPath, [Parameter(Mandatory)][string]$AdaptersRoot)
    $checkedIn = $NameOrPath -match '^[a-z0-9][a-z0-9-]*$' -and -not (Test-Path -LiteralPath $NameOrPath -PathType Leaf)
    $file = if ($checkedIn) { Join-Path $AdaptersRoot "$NameOrPath.json" } else { $NameOrPath }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $known = @(Get-ChildItem -LiteralPath $AdaptersRoot -Filter *.json -File -ErrorAction SilentlyContinue | ForEach-Object BaseName)
        throw "Adapter '$NameOrPath' not found. Checked-in adapters: $($known -join ', '); or pass a path to an adapter file."
    }
    $file = (Resolve-Path -LiteralPath $file).Path
    $raw = Get-Content -Raw -LiteralPath $file
    try { $d = $raw | ConvertFrom-Json -AsHashtable }
    catch { throw "Adapter '$file' is not valid JSON: $($_.Exception.Message)" }
    $bad = @(Test-PerfAdapterDefinition -Definition $d)
    if ($bad) { throw "Invalid adapter '$file':`n  $($bad -join "`n  ")" }
    $dir = Split-Path -Parent $file
    $command = [string]$d.command
    $commandIsPath = $command -match '[\\/]'
    [pscustomobject]@{
        Name        = [string]$d.name
        DisplayName = if ($d.Contains('displayName') -and $d.displayName) { [string]$d.displayName } else { [string]$d.name }
        Description = [string](Get-Field $d 'description')
        Command     = if ($commandIsPath) { Resolve-MethodPath -Path $command -BaseDirectory $dir } else { $command }
        CommandIsPath = $commandIsPath
        PrefixArgs  = @(Get-Field $d 'prefixArgs' | Where-Object { $null -ne $_ })
        Env         = if ($d.Contains('env') -and $d.env) { $d.env } else { [ordered]@{} }
        Path        = @(Get-Field $d 'path' | Where-Object { $_ } | ForEach-Object { Resolve-MethodPath -Path $_ -BaseDirectory $dir })
        Operations  = $d.operations
        Directory   = $dir
        CheckedIn   = [bool]$checkedIn
        SourcePath  = $file
        Hash        = Get-ShortHash ($raw -replace '\s', '')
    }
}

function Expand-AdapterArgs {
    # Fills an operation's argument template. Plain entries need every placeholder they use; a nested
    # list is an optional group, dropped when any of its placeholders has no value.
    param([AllowEmptyCollection()][object[]]$Template = @(), [System.Collections.IDictionary]$Values = @{})
    $has = { param($n) $Values.Contains($n) -and $null -ne $Values[$n] -and [string]$Values[$n] -ne '' }
    $fill = { param([string]$s) [regex]::Replace($s, $script:PlaceholderPattern, { param($m) [string]$Values[$m.Groups[1].Value] }) }
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $Template) {
        if ($entry -is [System.Collections.IList]) {
            if (@(Get-TemplatePlaceholders $entry | Where-Object { -not (& $has $_) }).Count) { continue }
            foreach ($s in $entry) { $out.Add((& $fill $s)) }
            continue
        }
        $missing = @(Get-TemplatePlaceholders $entry | Where-Object { -not (& $has $_) })
        if ($missing) { throw "Argument '$entry' needs a value for {$($missing -join '}, {')}." }
        $out.Add((& $fill $entry))
    }
    return , $out.ToArray()
}

function Get-AdapterInvocation {
    # The command line for one operation: command, prefix arguments, then the filled template.
    param([Parameter(Mandatory)]$Adapter, [Parameter(Mandatory)][string]$Operation, [System.Collections.IDictionary]$Values = @{})
    if (-not $Adapter.Operations.Contains($Operation)) { return $null }
    $v = [ordered]@{ adapterDir = $Adapter.Directory }
    foreach ($k in $Values.Keys) { $v[$k] = $Values[$k] }
    $prefix = Expand-AdapterArgs -Template @($Adapter.PrefixArgs) -Values $v
    $filled = Expand-AdapterArgs -Template @($Adapter.Operations[$Operation]) -Values $v
    [pscustomobject]@{ Arguments = [string[]](@($prefix) + @($filled)) }
}

#endregion

#region perf.json

function Test-StepDefinition {
    param([System.Collections.IDictionary]$Step, [string]$Where, [string[]]$AllowedKeys)
    $e = [System.Collections.Generic.List[string]]::new()
    foreach ($k in $Step.Keys) { if ($k -notin $AllowedKeys) { $e.Add("$Where`: unknown field '$k'") } }
    $op = [string](Get-Field $Step 'op')
    if (-not $script:Operations.Contains($op)) { $e.Add("$Where`: unknown op '$op' (known: $(@($script:Operations.Keys) -join ', '))"); return @($e) }
    foreach ($k in $script:Operations[$op]) { if (-not (Get-Field $Step $k)) { $e.Add("$Where`: op '$op' needs '$k'") } }
    if ($Step.Contains('timeout') -and -not (($Step.timeout -is [int] -or $Step.timeout -is [long]) -and $Step.timeout -gt 0)) { $e.Add("$Where`: timeout must be a positive number of milliseconds") }
    return @($e)
}

function Test-PerfConfigDefinition {
    # Schema errors of perf.json (as a hashtable) against the loaded scenarios; empty when valid.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition, [AllowEmptyCollection()][object[]]$Scenarios = @())
    $e = [System.Collections.Generic.List[string]]::new()
    $d = $Definition
    foreach ($k in $d.Keys) { if ($k -notin $script:ConfigKeys) { $e.Add("unknown field '$k'") } }
    foreach ($k in 'repeat', 'sequenceRepeat', 'timeoutSeconds') {
        if (-not $d.Contains($k) -or -not (($d[$k] -is [int] -or $d[$k] -is [long]) -and $d[$k] -ge 1)) { $e.Add("$k must be a positive integer") }
    }
    if ($d.Contains('warmup') -and -not (($d.warmup -is [int] -or $d.warmup -is [long]) -and $d.warmup -ge 0)) { $e.Add('warmup must be a non-negative integer') }
    $byId = @{}
    foreach ($s in $Scenarios) { $byId[$s.Id] = $s }
    $checkScenario = {
        param($Where, $Id, $Role)
        if (-not $Id) { return }
        $s = $byId[[string]$Id]
        if (-not $s) { $e.Add("$Where`: unknown scenario '$Id'"); return }
        if (-not @($s.Instances).Count) { $e.Add("$Where`: scenario '$Id' starts no app, so it has no window to target") ; return }
        if ($Role -and -not @($s.Instances | Where-Object { [string](Get-Field $_ 'role') -eq $Role })) { $e.Add("$Where`: scenario '$Id' has no instance '$Role'") }
    }
    $ids = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($o in @(Get-Field $d 'operations')) {
        if ($o -isnot [System.Collections.IDictionary]) { $e.Add('operations entries must be objects'); continue }
        $id = [string](Get-Field $o 'id')
        if ($id -notmatch '^[a-z0-9][a-z0-9-]*$') { $e.Add("operation id '$id' must be lowercase letters, digits, and dashes"); continue }
        if (-not $ids.Add("op:$id")) { $e.Add("duplicate operation id '$id'") }
        foreach ($x in Test-StepDefinition -Step $o -Where "operation '$id'" -AllowedKeys $script:OperationKeys) { $e.Add($x) }
        $sc = Get-Field $o 'scenario'
        if ([string](Get-Field $o 'op') -ne 'version' -and -not $sc) { $e.Add("operation '$id' needs a scenario whose setup provides the target window") }
        & $checkScenario "operation '$id'" $sc (Get-Field $o 'instance')
    }
    foreach ($q in @(Get-Field $d 'sequences')) {
        if ($q -isnot [System.Collections.IDictionary]) { $e.Add('sequences entries must be objects'); continue }
        $id = [string](Get-Field $q 'id')
        if ($id -notmatch '^[a-z0-9][a-z0-9-]*$') { $e.Add("sequence id '$id' must be lowercase letters, digits, and dashes"); continue }
        if (-not $ids.Add("seq:$id")) { $e.Add("duplicate sequence id '$id'") }
        foreach ($k in $q.Keys) { if ($k -notin $script:SequenceKeys) { $e.Add("sequence '$id': unknown field '$k'") } }
        if (-not (Get-Field $q 'scenario')) { $e.Add("sequence '$id' needs a scenario (its setup and checks)") }
        else { & $checkScenario "sequence '$id'" (Get-Field $q 'scenario') $null }
        $steps = @(Get-Field $q 'steps')
        if (-not $steps.Count) { $e.Add("sequence '$id' has no steps") }
        $n = 0
        foreach ($st in $steps) {
            $n++
            if ($st -isnot [System.Collections.IDictionary]) { $e.Add("sequence '$id' step $n must be an object"); continue }
            foreach ($x in Test-StepDefinition -Step $st -Where "sequence '$id' step $n" -AllowedKeys (@('op', 'instance', 'note') + $script:StepParams)) { $e.Add($x) }
            if ((Get-Field $st 'instance') -and (Get-Field $q 'scenario')) { & $checkScenario "sequence '$id' step $n" (Get-Field $q 'scenario') (Get-Field $st 'instance') }
        }
    }
    if (-not @(Get-Field $d 'operations').Count -and -not @(Get-Field $d 'sequences').Count) { $e.Add('perf.json defines no operations or sequences') }
    return @($e)
}

function ConvertTo-PerfStep {
    param([System.Collections.IDictionary]$Step)
    $params = [ordered]@{}
    foreach ($k in $script:StepParams) { if ($Step.Contains($k) -and $null -ne $Step[$k]) { $params[$k] = [string]$Step[$k] } }
    [pscustomobject]@{ Op = [string]$Step.op; Instance = [string](Get-Field $Step 'instance'); Params = $params; Note = [string](Get-Field $Step 'note') }
}

function Read-PerfConfig {
    # Loads and validates perf.json; throws listing every problem.
    param([Parameter(Mandatory)][string]$Path, [AllowEmptyCollection()][object[]]$Scenarios = @())
    try { $d = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable }
    catch { throw "'$Path' is not valid JSON: $($_.Exception.Message)" }
    $bad = @(Test-PerfConfigDefinition -Definition $d -Scenarios $Scenarios)
    if ($bad) { throw "Invalid perf config '$Path':`n  $($bad -join "`n  ")" }
    [pscustomobject]@{
        Repeat         = [int]$d.repeat
        Warmup         = if ($d.Contains('warmup')) { [int]$d.warmup } else { 1 }
        SequenceRepeat = [int]$d.sequenceRepeat
        TimeoutSeconds = [int]$d.timeoutSeconds
        Operations     = @(foreach ($o in @(Get-Field $d 'operations')) {
                $step = ConvertTo-PerfStep $o
                [pscustomobject]@{ Id = [string]$o.id; Op = $step.Op; Scenario = [string](Get-Field $o 'scenario'); Instance = $step.Instance; Params = $step.Params; Note = $step.Note }
            })
        Sequences      = @(foreach ($q in @(Get-Field $d 'sequences')) {
                [pscustomobject]@{ Id = [string]$q.id; Scenario = [string]$q.scenario; Description = [string](Get-Field $q 'description'); Steps = @(foreach ($st in @($q.steps)) { ConvertTo-PerfStep $st }) }
            })
    }
}

function Get-PerfPlan {
    # Process count and a rough duration for a perf run. Operations an adapter does not implement are skipped.
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][object[]]$Adapters,
        [AllowEmptyCollection()][object[]]$Operations = @(),
        [AllowEmptyCollection()][object[]]$Sequences = @(),
        [double]$SecondsPerCall = 0.6,
        [double]$SecondsPerSetup = 8
    )
    $calls = 0; $skipped = [System.Collections.Generic.List[string]]::new()
    foreach ($o in $Operations) {
        foreach ($a in $Adapters) {
            if ($a.Operations.Contains($o.Op)) { $calls += $Config.Warmup + $Config.Repeat }
            else { $skipped.Add("$($a.Name): $($o.Id)") }
        }
    }
    $targets = @($Operations | Where-Object Scenario | ForEach-Object Scenario | Select-Object -Unique).Count
    $sequenceRuns = 0; $sequenceCalls = 0
    foreach ($q in $Sequences) {
        foreach ($a in $Adapters) {
            $missing = @($q.Steps | Where-Object { -not $a.Operations.Contains($_.Op) } | ForEach-Object Op | Select-Object -Unique)
            if ($missing) { $skipped.Add("$($a.Name): sequence $($q.Id) (no $($missing -join ', '))"); continue }
            $sequenceRuns += $Config.SequenceRepeat
            $sequenceCalls += $Config.SequenceRepeat * @($q.Steps).Count
        }
    }
    $seconds = ($calls + $sequenceCalls) * $SecondsPerCall + ($targets + $sequenceRuns) * $SecondsPerSetup
    [pscustomobject]@{
        OperationCalls = $calls
        SequenceRuns   = $sequenceRuns
        SequenceCalls  = $sequenceCalls
        Minutes        = [Math]::Ceiling($seconds / 60)
        Skipped        = @($skipped)
    }
}

#endregion

#region Statistics and reports

function Get-PerfStats {
    # Median, p95 (nearest rank), min, max, and mean of timing samples in milliseconds.
    param([AllowEmptyCollection()][double[]]$Values = @())
    $v = @($Values | Sort-Object)
    if (-not $v.Count) { return [pscustomobject]@{ count = 0; median = $null; p95 = $null; min = $null; max = $null; mean = $null } }
    $rank = [Math]::Max(1, [int][Math]::Ceiling(0.95 * $v.Count))
    [pscustomobject]@{
        count  = $v.Count
        median = Get-Median $v
        p95    = $v[$rank - 1]
        min    = $v[0]
        max    = $v[-1]
        mean   = [Math]::Round(($v | Measure-Object -Average).Average, 1)
    }
}

function Format-PerfMs {
    param($Value)
    if ($null -eq $Value) { return 'n/a' }
    return ('{0:N0} ms' -f [double]$Value)
}

function Get-SequenceStats {
    # Pass count and timing of one sequence's runs for one adapter.
    param([AllowEmptyCollection()][object[]]$Runs = @())
    $totals = @($Runs | ForEach-Object { Get-Field $_ 'commandMs' } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    [pscustomobject]@{
        Runs   = $Runs.Count
        Passed = @($Runs | Where-Object { $_.status -eq 'pass' }).Count
        Total  = Get-PerfStats $totals
        Calls  = Get-Median @($Runs | ForEach-Object { @(Get-Field $_ 'steps').Count } | ForEach-Object { [double]$_ })
    }
}

function Write-PerfSummary {
    # summary.md for one perf results object (as written to perf-results.json).
    param([Parameter(Mandatory)]$Results, [Parameter(Mandatory)][string]$Path)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# UI automation perf benchmark')
    [void]$sb.AppendLine()
    $machine = $Results.environment
    [void]$sb.AppendLine("- **Started**: $($Results.started)")
    if (Get-Field $Results 'finished') { [void]$sb.AppendLine("- **Finished**: $($Results.finished)") }
    [void]$sb.AppendLine("- **Machine**: $(Get-Field $machine 'os'), $(Get-Field $machine 'architecture'), $(Get-Field $machine 'cpuCount') CPUs ($(Get-Field $machine 'cpu'))")
    [void]$sb.AppendLine("- **Repo**: $(Get-Field $machine 'repoCommit')$(if (Get-Field $machine 'repoDirty') { ' (dirty)' })")
    foreach ($a in @($Results.adapters)) {
        [void]$sb.AppendLine("- **Adapter $($a.name)**: $($a.displayName)$(if (Get-Field $a 'version') { ", version $($a.version)" }) ($($a.commandPath))")
    }
    [void]$sb.AppendLine("- **Samples**: $($Results.settings.repeat) per operation after $($Results.settings.warmup) discarded warm-up; $($Results.settings.sequenceRepeat) runs per sequence")
    if (Get-Field $Results 'stoppedEarly') { [void]$sb.AppendLine("- **Stopped early**: $($Results.stoppedEarly)") }
    [void]$sb.AppendLine()

    $ops = @(Get-Field $Results 'operations')
    if ($ops) {
        [void]$sb.AppendLine('## Operations')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('Wall time of one command, measured from outside the process (process start to exit).')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Operation | Adapter | Samples | Errors | Median | p95 | Max |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
        foreach ($o in $ops) {
            $st = $o.stats
            [void]$sb.AppendLine("| $($o.id) | $($o.adapter) | $($st.count) | $($o.errors) | $(Format-PerfMs $st.median) | $(Format-PerfMs $st.p95) | $(Format-PerfMs $st.max) |")
        }
        [void]$sb.AppendLine()
    }

    $seqs = @(Get-Field $Results 'sequences')
    if ($seqs) {
        [void]$sb.AppendLine('## Sequences')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('Fixed command sequences that perform a scenario''s task, scored with the scenario''s own checks. Time is the sum of the commands, without app setup.')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Sequence | Adapter | Pass | Commands | Median | p95 | Max |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
        foreach ($g in $seqs | Group-Object id, adapter) {
            $s = Get-SequenceStats $g.Group
            [void]$sb.AppendLine("| $($g.Group[0].id) | $($g.Group[0].adapter) | $($s.Passed)/$($s.Runs) | $(Format-Count $s.Calls) | $(Format-PerfMs $s.Total.median) | $(Format-PerfMs $s.Total.p95) | $(Format-PerfMs $s.Total.max) |")
        }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('### Median per step')
        [void]$sb.AppendLine()
        foreach ($g in $seqs | Group-Object id, adapter) {
            $steps = @($g.Group[0].steps)
            [void]$sb.AppendLine("**$($g.Group[0].id)** · $($g.Group[0].adapter): " + (@(for ($i = 0; $i -lt $steps.Count; $i++) {
                            $ms = @($g.Group | ForEach-Object { $x = @($_.steps); if ($i -lt $x.Count) { [double]$x[$i].ms } })
                            "$($steps[$i].label) $(Format-PerfMs (Get-Median $ms))"
                        }) -join ' → '))
            [void]$sb.AppendLine()
        }
    }

    $problems = @(
        foreach ($o in $ops | Where-Object { $_.errors }) { "- operation **$($o.id)** · $($o.adapter): $($o.errors) failed call(s); first: $($o.firstError)" }
        foreach ($q in $seqs | Where-Object { $_.status -ne 'pass' }) { "- sequence **$($q.id)** · $($q.adapter) · #$($q.iteration) · $($q.status): $($q.reason)" }
    )
    [void]$sb.AppendLine('## Failures')
    [void]$sb.AppendLine()
    if ($problems) { foreach ($p in $problems) { [void]$sb.AppendLine($p) } } else { [void]$sb.AppendLine('None.') }
    Set-Content -LiteralPath $Path -Value $sb.ToString() -Encoding utf8NoBOM
}

function Read-PerfResults {
    param([Parameter(Mandatory)][string]$Directory)
    $file = Join-Path $Directory 'perf-results.json'
    if (-not (Test-Path -LiteralPath $file)) { throw "No perf-results.json in '$Directory'." }
    return (Get-Content -Raw -LiteralPath $file | ConvertFrom-Json)
}

function Get-PerfComparison {
    # Markdown comparing medians of two or more perf result folders; the change is relative to the first.
    param([Parameter(Mandatory)][string[]]$ResultDirs)
    $sets = @(foreach ($d in $ResultDirs) { [pscustomobject]@{ Label = Split-Path $d -Leaf; Results = Read-PerfResults $d } })
    $delta = {
        param($base, $value)
        if ($null -eq $base -or $null -eq $value -or [double]$base -eq 0) { return '' }
        $pct = ([double]$value - [double]$base) / [double]$base * 100
        return ('{0:+0;-0;0}%' -f $pct)
    }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# UI perf comparison')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("Results: $(@($sets | ForEach-Object { '`' + $_.Label + '`' }) -join ' vs '). Change is relative to ``$($sets[0].Label)``.")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Operations (median)')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| Operation | Adapter | $(@($sets.Label) -join ' | ') | Change |")
    [void]$sb.AppendLine("|---|---|$(@($sets | ForEach-Object { '---|' }) -join '')---|")
    $keys = @($sets | ForEach-Object { @(Get-Field $_.Results 'operations') } | ForEach-Object { "$($_.id)|$($_.adapter)" } | Select-Object -Unique)
    foreach ($k in $keys) {
        $id, $adapter = $k -split '\|', 2
        $vals = @(foreach ($s in $sets) { $o = @(@(Get-Field $s.Results 'operations') | Where-Object { $_.id -eq $id -and $_.adapter -eq $adapter }); if ($o) { $o[0].stats.median } else { $null } })
        [void]$sb.AppendLine("| $id | $adapter | $(@($vals | ForEach-Object { Format-PerfMs $_ }) -join ' | ') | $(& $delta $vals[0] $vals[-1]) |")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Sequences (pass, median total)')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| Sequence | Adapter | $(@($sets.Label) -join ' | ') | Change |")
    [void]$sb.AppendLine("|---|---|$(@($sets | ForEach-Object { '---|' }) -join '')---|")
    $keys = @($sets | ForEach-Object { @(Get-Field $_.Results 'sequences') } | ForEach-Object { "$($_.id)|$($_.adapter)" } | Select-Object -Unique)
    foreach ($k in $keys) {
        $id, $adapter = $k -split '\|', 2
        $stats = @(foreach ($s in $sets) { $runs = @(@(Get-Field $s.Results 'sequences') | Where-Object { $_.id -eq $id -and $_.adapter -eq $adapter }); if ($runs) { Get-SequenceStats $runs } else { $null } })
        $cells = @($stats | ForEach-Object { if ($_) { "$($_.Passed)/$($_.Runs), $(Format-PerfMs $_.Total.median)" } else { 'n/a' } })
        $first = if ($stats[0]) { $stats[0].Total.median } else { $null }
        $last = if ($stats[-1]) { $stats[-1].Total.median } else { $null }
        [void]$sb.AppendLine("| $id | $adapter | $($cells -join ' | ') | $(& $delta $first $last) |")
    }
    return $sb.ToString()
}

#endregion

Export-ModuleMember -Function Test-PerfAdapterDefinition, Read-PerfAdapter, Expand-AdapterArgs, Get-AdapterInvocation, Test-PerfConfigDefinition,
Read-PerfConfig, Get-PerfPlan, Get-PerfStats, Format-PerfMs, Get-SequenceStats, Write-PerfSummary, Read-PerfResults, Get-PerfComparison
