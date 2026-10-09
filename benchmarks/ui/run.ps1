#Requires -Version 7.4
<#
.SYNOPSIS
    UI automation agent benchmark: Copilot CLI drives real Windows apps (Calculator, WinUI 3 Gallery)
    through a method (the winapp plugin and CLI, nothing, or a local method file), and the harness
    scores the resulting app state itself.

.EXAMPLE
    ./run.ps1 -Plan

.EXAMPLE
    ./run.ps1 -Scenario calc-running -Method winapp -Iterations 1

.EXAMPLE
    ./run.ps1 -Method winapp,none -OutDir results\overnight

.EXAMPLE
    ./run.ps1 -Method C:\bench\my-tool.method.json -OutDir results\my-tool

.EXAMPLE
    ./run.ps1 -Compare results\before,results\after -OutDir results\compare
#>
[CmdletBinding()]
param(
    [string[]]$Scenario,
    # Checked-in method names (winapp, none) or paths to local method.json files.
    [string[]]$Method,
    [string[]]$Model,
    [ValidateRange(1, 100)]
    [int]$Iterations,
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes,
    [ValidateRange(0.01, 1000000)]
    [double]$MaxCredits,
    [string]$CopilotVersion,
    # Results folder. Rerunning with the same folder skips finished runs.
    [string]$OutDir,
    [switch]$Plan,
    [switch]$Lint,
    # Run even when a target app is already open outside the benchmark (its windows are never closed).
    [switch]$Force,
    [switch]$KeepArtifacts,
    [string[]]$Compare
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'lib\UiBenchmark.psm1') -Force
Import-Module (Join-Path $repoRoot 'benchmarks\agents\lib\Benchmark.psm1') -Force

$Scenario = Split-ListArgument $Scenario
$Method = Split-ListArgument $Method
$Model = Split-ListArgument $Model
$Compare = Split-ListArgument $Compare

$config = Get-Content -Raw (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json -AsHashtable
$allScenarios = @(Get-UiScenarios -ScenariosRoot (Join-Path $PSScriptRoot 'scenarios'))

if ($Compare) {
    if ($Compare.Count -lt 2) { throw 'Use -Compare with two or more result folders, e.g. -Compare results\a,results\b.' }
    $report = Get-UiComparison -ResultDirs @($Compare | ForEach-Object { (Resolve-Path $_).Path }) -ScenarioOrder @($allScenarios.Id)
    if ($OutDir) {
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $path = Join-Path $OutDir 'comparison.md'
        Set-Content -LiteralPath $path -Value $report -Encoding utf8NoBOM
        Write-Host "Comparison: $path"
    }
    else { $report }
    return
}

if ($Lint) {
    # Schema and tool-name leaks already fail scenario loading; this adds word overlap with skill descriptions.
    Import-Module (Join-Path $repoRoot 'benchmarks\agents\lib\ScenarioLint.psm1') -Force
    $corpus = Get-LintCorpus -PluginPaths @(Join-Path $repoRoot 'plugins\winapp')
    $warnings = 0
    foreach ($s in $allScenarios) {
        $leak = Get-ScenarioLeakage -Text $s.Prompt -Corpus $corpus
        $bigrams = @($leak.SharedBigrams | ForEach-Object { "'$($_.Bigram)' ($($_.Documents -join ', '))" })
        if ($bigrams -or $leak.MaxJaccard -gt 0.10) {
            $warnings++
            Write-Host "warning $($s.Id): jaccard $([Math]::Round($leak.MaxJaccard, 2)) with $($leak.MaxJaccardDocument)$(if ($bigrams) { "; shares $($bigrams -join ', ')" })"
        }
    }
    Write-Host "Linted $($allScenarios.Count) prompts: 0 errors, $warnings warnings."
    return
}

if ($Scenario) {
    $unknown = @($Scenario | Where-Object { $_ -notin $allScenarios.Id })
    if ($unknown) { throw "Unknown scenario id(s): $($unknown -join ', '). Known: $($allScenarios.Id -join ', ')" }
}
$scenarios = @($allScenarios | Where-Object { -not $Scenario -or $_.Id -in $Scenario })
$methods = @((& { if ($Method) { $Method } else { @($config.methods) } }) | ForEach-Object { Get-UiMethod -NameOrPath $_ -MethodsRoot (Join-Path $PSScriptRoot 'methods') })
$dupes = @($methods | Group-Object Name | Where-Object Count -gt 1)
if ($dupes) { throw "Two methods are named '$($dupes[0].Name)'; method names must be unique within a run." }
$models = if ($Model) { $Model } else { @($config.model) }
$iterationCount = if ($PSBoundParameters.ContainsKey('Iterations')) { $Iterations } else { [int]$config.iterations }

# Iterations outermost, so a run stopped early still covers every method and scenario evenly.
$runList = @(foreach ($i in 1..$iterationCount) {
        foreach ($s in $scenarios) {
            foreach ($m in $methods) {
                foreach ($mod in $models) {
                    $t = if ($PSBoundParameters.ContainsKey('TimeoutMinutes')) { $TimeoutMinutes } elseif ($s.TimeoutMinutes) { $s.TimeoutMinutes } else { [int]$config.timeoutMinutes }
                    [pscustomobject]@{ Scenario = $s; Method = $m; Model = $mod; Iteration = $i; TimeoutMinutes = $t }
                }
            }
        }
    })

$resuming = $OutDir -and (Test-Path -LiteralPath (Join-Path $OutDir 'runs.jsonl'))
$previous = @(if ($resuming) { Read-RunRecords (Join-Path $OutDir 'runs.jsonl') })
$runPlan = Get-RunPlan -Runs $runList -Records $previous -CreditEstimate ([double]$config.creditEstimatePerRun) -MinutesEstimate ([double]$config.minutesEstimatePerRun)

if ($Plan) {
    Write-Host "Scenarios:  $($scenarios.Id -join ', ')"
    Write-Host "Methods:    $(@($methods | ForEach-Object { if ($_.CheckedIn) { $_.Name } else { "$($_.Name) ($($_.SourcePath))" } }) -join ', ')"
    Write-Host "Models:     $($models -join ', ')"
    Write-Host "Iterations: $iterationCount"
    Write-Host "Sessions:   $($runPlan.Total) total, $($runPlan.Done) already finished, $($runPlan.Pending.Count) to run"
    Write-Host ('Estimate:   ~{0:N0} AI credits (~{1:N1} per session), ~{2:N0} min (~{3:N1} per session)' -f $runPlan.Credits, $runPlan.CreditsPerRun, $runPlan.Minutes, $runPlan.MinutesPerRun)
    if ($MaxCredits) { Write-Host "Budget:     stops after $MaxCredits AI credits" }
    $galleryNeeded = [bool]@($scenarios | Where-Object App -eq 'gallery')
    if ($galleryNeeded -and -not (Get-AppxPackage -Name $config.gallery.packageName -ErrorAction SilentlyContinue)) {
        Write-Host "Setup:      the WinUI 3 Gallery is not installed yet; run .\setup-gallery.ps1 first."
    }
    return
}

Import-Module (Join-Path $PSScriptRoot 'lib\AppState.psm1') -Force

# --- Apps, and the guard against touching windows the benchmark does not own ---------------
$apps = @{}
foreach ($name in @($scenarios.App | Select-Object -Unique)) {
    try { $apps[$name] = Get-AppInfo -App $name -Config $config }
    catch { throw "$($_.Exception.Message)" }
}

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "results\$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$runsPath = Join-Path $OutDir 'runs.jsonl'
$ledgerPath = Join-Path $OutDir 'owned.json'
function Save-Ledger([object[]]$Entries) { Save-OwnedLedger -Path $ledgerPath -Entries @($Entries) }
Clear-OwnedLedger -Path $ledgerPath -Apps $apps -Config $config

function Get-ForeignReport {
    @(foreach ($name in $apps.Keys) {
            foreach ($p in Get-ForeignInstances -AppInfo $apps[$name]) { "$($apps[$name].DisplayName) (pid $($p.pid))" }
        })
}
$foreignNow = @(Get-ForeignReport)
if ($foreignNow -and -not $Force) {
    throw "These apps are already open outside the benchmark: $($foreignNow -join ', '). Close them, or pass -Force to run anyway (the benchmark never closes them, but agents may act on them)."
}

if (-not $runPlan.Pending.Count) {
    Write-Host "All $($runPlan.Total) sessions in $OutDir are already finished."
    Write-UiSummary -RunsPath $runsPath -SummaryPath (Join-Path $OutDir 'summary.md') -ScenarioOrder @($allScenarios.Id)
    Write-Host "Summary: $(Join-Path $OutDir 'summary.md')"
    return
}

# --- Resolve one Copilot CLI build and pin it for every invocation ---------------------------
$copilotExe = (Get-Command copilot -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-ui-bench\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

function Get-CopilotVersion {
    param([string[]]$ExtraArgs = @(), [switch]$AllowAutoUpdate)
    $probeHome = Join-Path $tempRoot "version-probe-$(Get-Random)"
    New-Item -ItemType Directory -Force -Path $probeHome | Out-Null
    try {
        Set-Content -Path (Join-Path $probeHome 'config.json') -Value '{"autoUpdate": false}'
        $envMap = New-ChildEnvironment -CopilotHome $probeHome
        if ($AllowAutoUpdate) { $envMap.Remove('COPILOT_AUTO_UPDATE') }
        $outFile = Join-Path $probeHome 'version.out'
        $r = Invoke-LoggedProcess -FilePath $copilotExe -Arguments (@($ExtraArgs) + '--version') -WorkingDirectory $probeHome `
            -Environment $envMap -StdoutPath $outFile -StderrPath (Join-Path $probeHome 'version.err') -TimeoutSeconds 60
        $text = Get-Content -Raw $outFile
        if ($r.ExitCode -ne 0 -or $text -notmatch '(\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?)') {
            throw "Could not determine Copilot CLI version (exit $($r.ExitCode)): $text"
        }
        return $Matches[1]
    }
    finally { Remove-Item -Recurse -Force -LiteralPath $probeHome -ErrorAction SilentlyContinue }
}

$pinnedVersion = if ($CopilotVersion) { $CopilotVersion } elseif ($config.copilotVersion) { $config.copilotVersion } else { Get-CopilotVersion -AllowAutoUpdate }
$verified = Get-CopilotVersion -ExtraArgs @('--prefer-version', $pinnedVersion)
if ($verified -ne $pinnedVersion) { throw "Copilot CLI ran version $verified instead of the pinned $pinnedVersion." }
$copilotPrefix = @('--prefer-version', $pinnedVersion)

# --- Per-method environment ----------------------------------------------------------------
function Get-MethodEnvironment {
    # Environment map, PATH, and resolved commands for one method, without the Copilot home.
    param($M, [string]$ShimRoot)
    $baseEnv = New-ChildEnvironment -CopilotHome '(set per run)'
    $expanded = Expand-MethodEnv -Env $M.Env
    if ($expanded.Missing) { throw "Method '$($M.Name)' needs these environment variables: $($expanded.Missing -join ', ')." }
    foreach ($k in $expanded.Values.Keys) { $baseEnv[$k] = $expanded.Values[$k] }
    $prepend = [System.Collections.Generic.List[string]]::new()
    if ($M.HideCommands) {
        $shim = Join-Path $ShimRoot "hide-$($M.Name)"
        New-HiddenCommandShim -Directory $shim -Names $M.HideCommands
        $prepend.Add($shim)
    }
    foreach ($p in $M.Path) { $prepend.Add($p) }
    $pathValue = Set-EnvironmentPath -Environment $baseEnv -Prepend @($prepend)
    $pathExt = [string](Get-Field $baseEnv 'PATHEXT')
    $problems = @(
        foreach ($c in $M.Requires) {
            $found = Resolve-CommandOnPath -Name $c -PathValue $pathValue -PathExt ($pathExt ? $pathExt : '.COM;.EXE;.BAT;.CMD')
            if (-not $found -or ($M.HideCommands -and $found.StartsWith($ShimRoot, [StringComparison]::OrdinalIgnoreCase))) { "requires '$c', which is not on PATH" }
        }
        foreach ($c in $M.HideCommands) {
            $found = Resolve-CommandOnPath -Name $c -PathValue $pathValue -PathExt ($pathExt ? $pathExt : '.COM;.EXE;.BAT;.CMD')
            if ($found -and -not $found.StartsWith($ShimRoot, [StringComparison]::OrdinalIgnoreCase)) { "could not hide '$c' (resolves to $found)" }
        }
    )
    if ($problems) { throw "Method '$($M.Name)': $($problems -join '; ')." }
    $winapp = Resolve-CommandOnPath -Name 'winapp' -PathValue $pathValue -PathExt ($pathExt ? $pathExt : '.COM;.EXE;.BAT;.CMD')
    $winappVersion = $null
    if ($winapp -and -not $winapp.StartsWith($ShimRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $winappVersion = (& $winapp --version 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { $winappVersion = $null }
    }
    $mcpPath = $null
    if ($M.McpServers.Count) {
        $json = ConvertTo-Json -InputObject ([ordered]@{ mcpServers = $M.McpServers }) -Depth 10
        foreach ($m in [regex]::Matches($json, '\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}')) {
            $v = [Environment]::GetEnvironmentVariable($m.Groups[1].Value)
            if (-not $v) { throw "Method '$($M.Name)' MCP config needs the environment variable $($m.Groups[1].Value)." }
            $json = $json.Replace($m.Value, (ConvertTo-Json $v).Trim('"'))
        }
        $mcpPath = Join-Path $ShimRoot "mcp-$($M.Name).json"
        Set-Content -LiteralPath $mcpPath -Value $json -Encoding utf8NoBOM
    }
    [pscustomobject]@{ Env = $baseEnv; WinappPath = $winapp; WinappVersion = $winappVersion; McpPath = $mcpPath }
}

$methodEnv = @{}
foreach ($m in $methods) { $methodEnv[$m.Name] = Get-MethodEnvironment -M $m -ShimRoot $tempRoot }

# --- Output --------------------------------------------------------------------------------
$environment = Get-EnvironmentInfo -RepoRoot $repoRoot
$header = [ordered]@{
    'Started'     = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    'Copilot CLI' = $pinnedVersion
    'Models'      = $models -join ', '
    'Iterations'  = $iterationCount
    'Methods'     = @($methods | ForEach-Object {
            $v = $methodEnv[$_.Name].WinappVersion
            "$($_.DisplayName)$(if ($v) { " (winapp $v)" })"
        }) -join '; '
    'Machine'     = "$($environment.os), $($environment.architecture), $($environment.cpuCount) CPUs"
    'Repo'        = "$($environment.repoCommit)$(if ($environment.repoDirty) { ' (dirty)' })"
}
[ordered]@{
    copilotVersion = $pinnedVersion
    models         = $models
    iterations     = $iterationCount
    scenarios      = @($scenarios | ForEach-Object { [ordered]@{ id = $_.Id; hash = $_.Hash } })
    methods        = @($methods | ForEach-Object {
            [ordered]@{
                name = $_.Name; displayName = $_.DisplayName; hash = $_.Hash; source = $(if ($_.CheckedIn) { "methods\$($_.Name).json" } else { $_.SourcePath })
                skills = $_.ExpectedSkills; winappVersion = $methodEnv[$_.Name].WinappVersion; winappPath = $methodEnv[$_.Name].WinappPath
            }
        })
    apps           = @($apps.Values | ForEach-Object { [ordered]@{ app = $_.App; package = $_.PackageName; version = $_.Version; architecture = $_.Architecture } })
    environment    = $environment
} | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'run-info.json') -Encoding utf8NoBOM

# --- One run -------------------------------------------------------------------------------
function Invoke-UiRun {
    param($Run, [int]$Index)
    $s = $Run.Scenario
    $m = $Run.Method
    $info = $apps[$s.App]
    $runId = '{0:D3}-{1}-{2}-{3}-{4}' -f $Index, $s.Id, $m.Name, ($Run.Model -replace '[^\w.-]', '_'), $Run.Iteration
    $root = Join-Path $tempRoot $runId
    $copilotHome = Join-Path $root 'home'
    $ws = Join-Path $root 'workspace'
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force -Path $copilotHome, $ws, $logs | Out-Null
    Set-Content -Path (Join-Path $copilotHome 'config.json') -Value '{"autoUpdate": false}'
    $envMap = [ordered]@{}
    foreach ($k in $methodEnv[$m.Name].Env.Keys) { $envMap[$k] = $methodEnv[$m.Name].Env[$k] }
    $envMap['COPILOT_HOME'] = $copilotHome
    $started = [System.Diagnostics.Stopwatch]::StartNew()

    $record = [ordered]@{
        runId          = $runId
        scenario       = $s.Id
        app            = $s.App
        method         = $m.Name
        model          = $Run.Model
        iteration      = $Run.Iteration
        status         = $null
        reason         = $null
        failures       = @()
        collateral     = @()
        notes          = @()
        scenarioHash   = $s.Hash
        methodHash     = $m.Hash
        promptHash     = Get-ShortHash $s.Prompt
        winappVersion  = $methodEnv[$m.Name].WinappVersion
        totalMs        = $null
        setupMs        = $null
        durationMs     = $null
        toolTimeMs     = $null
        apiDurationMs  = $null
        tokens         = $null
        aiCredits      = $null
        premiumRequests = $null
        modelTurns     = $null
        toolCalls      = $null
        toolCallsByName = $null
        winappCommands = $null
        skillsLoaded   = @()
        finalResponse  = $null
        exitCode       = $null
        copilotVersion = $null
        before         = $null
        after          = $null
        teardown       = @()
        preflight      = $null
        artifacts      = $null
        finishedAt     = $null
    }

    $invoke = {
        param([string]$Name, [string[]]$CopilotArgs, [int]$TimeoutSeconds = 120)
        Invoke-LoggedProcess -FilePath $copilotExe -Arguments (@($copilotPrefix) + $CopilotArgs) -WorkingDirectory $ws `
            -Environment $envMap -StdoutPath (Join-Path $logs "$Name.out") -StderrPath (Join-Path $logs "$Name.err") -TimeoutSeconds $TimeoutSeconds
    }

    $owned = [System.Collections.Generic.List[object]]::new()
    $setup = @()
    $foreign = @()
    $abort = $null
    try {
        & git -C $ws rev-parse --is-inside-work-tree *> $null
        if ($LASTEXITCODE -eq 0) { throw "Workspace $ws is inside a git repository." }

        # 1. Agent preflight first, so a broken method never costs a desktop setup.
        foreach ($p in $m.Plugins) {
            $r = & $invoke "install-$(Split-Path $p -Leaf)" @('plugin', 'install', $p)
            if ($r.ExitCode -ne 0) {
                $record.status = 'preflight_failed'
                $record.reason = "plugin install '$p' exited $($r.ExitCode): $(Get-FileTail (Join-Path $logs "install-$(Split-Path $p -Leaf).err") 300)"
                return $record
            }
        }
        foreach ($sk in $m.Skills) { Copy-Item -LiteralPath $sk -Destination (Join-Path $copilotHome "skills\$(Split-Path $sk -Leaf)") -Recurse -Force }
        $r = & $invoke 'skill-list' @('skill', 'list', '--json')
        if ($r.ExitCode -ne 0) {
            $record.status = 'preflight_failed'
            $record.reason = "skill list exited $($r.ExitCode): $(Get-FileTail (Join-Path $logs 'skill-list.err') 300)"
            return $record
        }
        $listed = @(Get-Content -Raw (Join-Path $logs 'skill-list.out') | ConvertFrom-Json)
        $check = Test-MethodSkillList -Expected $m.ExpectedSkills -Listed $listed -CopilotHome $copilotHome
        $record.preflight = [ordered]@{ expectedSkills = $m.ExpectedSkills; unexpected = $check.Unexpected; missing = $check.Missing }
        if ($check.Unexpected -or $check.Missing) {
            $record.status = 'preflight_failed'
            $record.reason = "skill set mismatch; unexpected: [$($check.Unexpected -join ', ')], missing: [$($check.Missing -join ', ')]"
            return $record
        }

        # 2. App setup. Every launched process is recorded before anything else can fail.
        $foreign = @(Get-ForeignInstances -AppInfo $info)
        if ($foreign -and -not $Force) {
            $abort = "$($info.DisplayName) was opened outside the benchmark (pid $(@($foreign.pid) -join ', ')); stopping. Close it and rerun to resume."
            $record.status = 'harness_error'
            $record.reason = $abort
            return $record
        }
        $setupWatch = [System.Diagnostics.Stopwatch]::StartNew()
        try { $setup = @(Start-ScenarioInstances -AppInfo $info -Scenario $s -Owned $owned -LedgerPath $ledgerPath) }
        catch {
            $record.status = 'setup_failed'
            $record.reason = $_.Exception.Message
            return $record
        }
        $record.setupMs = $setupWatch.ElapsedMilliseconds
        $beforeSnap = Get-SetupSnapshot -AppInfo $info -Setup $setup -Foreign $foreign
        $record.before = $beforeSnap

        # 3. The agent run: shell allowed, so it really drives the desktop. URLs stay denied.
        $agentArgs = @(
            '-C', $ws,
            '-p', $s.Prompt,
            '--model', $Run.Model,
            '--output-format', 'json',
            '--stream', 'off',
            '--allow-all-tools',
            '--deny-tool=url',
            '--disable-builtin-mcps',
            '--no-custom-instructions',
            '--no-ask-user'
        )
        foreach ($t in $m.AllowTools) { $agentArgs += "--allow-tool=$t" }
        foreach ($t in $m.DenyTools) { $agentArgs += "--deny-tool=$t" }
        if ($methodEnv[$m.Name].McpPath) { $agentArgs += @('--additional-mcp-config', "@$($methodEnv[$m.Name].McpPath)") }
        $r = & $invoke 'agent' $agentArgs ($Run.TimeoutMinutes * 60)
        $record.durationMs = $r.DurationMs
        $record.exitCode = $r.ExitCode

        # 4. Parse the persisted session log.
        $eventLogs = @(Get-ChildItem -Path (Join-Path $copilotHome 'session-state') -Filter events.jsonl -Recurse -File -ErrorAction SilentlyContinue)
        $eventLog = $eventLogs | Sort-Object Length -Descending | Select-Object -First 1
        $parsed = Read-SessionEvents -Path ($eventLog ? $eventLog.FullName : '')
        $record.copilotVersion = $parsed.copilotVersion
        $record.skillsLoaded = @($parsed.skillsInvoked | ForEach-Object { Get-BareSkillName $_.name } | Select-Object -Unique)
        $record.tokens = $parsed.tokens
        $record.aiCredits = $parsed.aiCredits
        $record.premiumRequests = $parsed.premiumRequests
        $record.modelTurns = $parsed.modelTurns
        $record.toolCalls = $parsed.toolCalls
        $record.toolCallsByName = $parsed.toolCallsByName
        $record.toolTimeMs = $parsed.toolTimeMs
        $record.apiDurationMs = $parsed.apiDurationMs
        $record.winappCommands = @($parsed.winappCommands)
        $record.finalResponse = $parsed.finalResponse

        # 5. Score from the harness's own reads; the agent's answer is never consulted.
        $afterSnap = Get-AppSnapshot -AppInfo $info -Known $setup -Foreign $foreign -RestoreOwnedMinimized
        $record.after = $afterSnap
        Add-StartedProcesses -AppInfo $info -Before $beforeSnap -After $afterSnap -Owned $owned -LedgerPath $ledgerPath

        $eval = Test-ScenarioState -Scenario $s -Before $beforeSnap -After $afterSnap
        $record.failures = $eval.Failures
        $record.collateral = $eval.Collateral
        $record.notes = $eval.Notes
        if ($eventLogs.Count -gt 1) { $record.notes += "found $($eventLogs.Count) events.jsonl files; parsed the largest" }
        if ($r.TimedOut) {
            $record.status = 'fail'
            $record.reason = (@("timed out after $($Run.TimeoutMinutes) min") + $eval.Failures) -join '; '
        }
        elseif ($r.ExitCode -ne 0) {
            $record.status = 'harness_error'
            $record.reason = "copilot exited $($r.ExitCode): $(Get-FileTail (Join-Path $logs 'agent.err') 300)"
        }
        elseif (-not $eventLog) {
            $record.status = 'harness_error'
            $record.reason = 'no persisted events.jsonl found in the isolated COPILOT_HOME'
        }
        else {
            $record.status = $eval.Status
            $record.reason = (@($eval.Failures) + @($eval.Collateral | ForEach-Object { "$($_.type): $($_.detail)" })) -join '; '
        }
    }
    catch {
        $record.status = 'harness_error'
        $record.reason = $_.Exception.Message
    }
    finally {
        # 6. Teardown: close what the harness and the run started, by pid; never foreign windows.
        $results = Stop-OwnedEntries -Owned @($owned) -Apps $apps -Config $config
        $record.teardown = @($results)
        $stuck = @($results | Where-Object result -eq 'still-running')
        if ($stuck) {
            $record.reason = "[$($record.status)] $($record.reason) | could not close pid(s) $(@($stuck.pid) -join ', ')"
            $record.status = 'cleanup_failed'
            $abort = "could not close pid(s) $(@($stuck.pid) -join ', '); stopping so the next run does not start on a dirty desktop."
        }
        else { Save-Ledger @() }

        if ($KeepArtifacts) { $record.artifacts = $root }
        else {
            foreach ($attempt in 1..5) {
                try { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction Stop; break }
                catch { Start-Sleep -Seconds 2 }
            }
        }
        $record.totalMs = $started.ElapsedMilliseconds
        $record.finishedAt = (Get-Date).ToString('o')
    }
    if ($abort) { $record['abort'] = $abort }
    return $record
}

# --- Loop ----------------------------------------------------------------------------------
$pending = @($runPlan.Pending)
Write-Host ("Copilot CLI {0} | {1} of {2} sessions to run (~{3:N0} AI credits, ~{4:N0} min) | results: {5}" -f $pinnedVersion, $pending.Count, $runPlan.Total, $runPlan.Credits, $runPlan.Minutes, $OutDir)
$finished = [System.Collections.Generic.List[object]]::new()
$spend = Get-CreditSpend @() -DefaultEstimate ([double]$config.creditEstimatePerRun)
$index = 0
foreach ($run in $pending) {
    if ($MaxCredits -and $spend.Spent -ge $MaxCredits) {
        Write-Warning "Stopping: spent $([Math]::Round($spend.Spent, 1)) AI credits (-MaxCredits $MaxCredits); $($pending.Count - $index) sessions not run. Rerun with the same -OutDir to continue."
        $header['Stopped early'] = "credit limit $MaxCredits reached after $index of $($pending.Count) sessions"
        break
    }
    $index++
    Write-Host "[$index/$($pending.Count)] $($run.Scenario.Id) | $($run.Method.Name) | $($run.Model) | #$($run.Iteration) ..." -NoNewline
    $rec = Invoke-UiRun -Run $run -Index ($runPlan.Done + $index)
    $abort = if ($rec.Contains('abort')) { $rec['abort'] } else { $null }
    $rec.Remove('abort')
    $rec | ConvertTo-Json -Depth 12 -Compress | Add-Content -Path $runsPath -Encoding utf8NoBOM
    $finished.Add([pscustomobject]$rec)
    $spend = Get-CreditSpend $finished -DefaultEstimate ([double]$config.creditEstimatePerRun)
    $dur = if ($null -ne $rec.durationMs) { '{0:N0}s' -f ($rec.durationMs / 1000) } else { '-' }
    $cred = if ($null -ne $rec.aiCredits) { '{0:N1} credits' -f $rec.aiCredits } else { 'credits n/a' }
    Write-Host " $($rec.status) | $dur | turns $($rec.modelTurns ?? '-') | tools $($rec.toolCalls ?? '-') | $cred"
    if ($rec.status -ne 'pass' -and $rec.reason) { Write-Host "    $($rec.reason)" }
    Write-UiSummary -RunsPath $runsPath -SummaryPath (Join-Path $OutDir 'summary.md') -Header $header -ScenarioOrder @($allScenarios.Id)
    if ($abort) {
        Write-Warning "Stopping: $abort"
        $header['Stopped early'] = $abort
        break
    }
}

$header['Finished'] = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
Write-UiSummary -RunsPath $runsPath -SummaryPath (Join-Path $OutDir 'summary.md') -Header $header -ScenarioOrder @($allScenarios.Id)
if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    $tempParent = Split-Path $tempRoot
    if (-not (Get-ChildItem -LiteralPath $tempParent -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $tempParent -ErrorAction SilentlyContinue }
}
Write-Host "Summary: $(Join-Path $OutDir 'summary.md')"
