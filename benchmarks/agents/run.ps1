#Requires -Version 7.4
<#
.SYNOPSIS
    Local agent-plugin benchmark: measures which skills Copilot CLI loads, and how many tokens it
    uses, for realistic prompts under different plugin configurations.

.EXAMPLE
    ./run.ps1 -Plan

.EXAMPLE
    ./run.ps1 -Scenario electron-notifications -Configuration winapp -Model claude-sonnet-5.5 -Iterations 1

.EXAMPLE
    ./run.ps1 -Rescore results\20261002-160446
#>
[CmdletBinding()]
param(
    [string[]]$Scenario,
    [string[]]$Configuration,
    [string[]]$Model,
    [ValidateRange(1, 100)]
    [int]$Iterations,
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes,
    [string]$WinAppPlugin,
    [string]$WinUIPlugin,
    [string]$CopilotVersion,
    [string]$OutDir,
    [switch]$Plan,
    [switch]$KeepArtifacts,
    [string]$Rescore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Benchmark.psm1') -Force

# `pwsh -File run.ps1 -Scenario a,b` passes the single string "a,b"; accept both forms.
$Scenario = Split-ListArgument $Scenario
$Configuration = Split-ListArgument $Configuration
$Model = Split-ListArgument $Model
$badConfigs = @($Configuration | Where-Object { $_ -notin 'none', 'winapp', 'winui', 'both' })
if ($badConfigs) { throw "Unknown configuration(s): $($badConfigs -join ', '). Use none, winapp, winui, or both." }

$config = Get-Content -Raw (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json -AsHashtable
$models = if ($Model) { $Model } else { @($config.models) }
$iterationCount = if ($PSBoundParameters.ContainsKey('Iterations')) { $Iterations } else { [int]$config.iterations }

$allScenarios = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot 'scenarios')

if ($Rescore) {
    $r = Invoke-Rescore -ResultsDir (Resolve-Path $Rescore).Path -Scenarios $allScenarios
    Write-Host "Rescored $($r.Runs) runs against the current scenarios; $($r.Changed) changed status."
    Write-Host "Runs:    $($r.RunsPath)"
    Write-Host "Summary: $($r.SummaryPath)"
    return
}

$scenarios = $allScenarios
if ($Scenario) {
    $unknown = @($Scenario | Where-Object { $_ -notin $allScenarios.Id })
    if ($unknown) { throw "Unknown scenario id(s): $($unknown -join ', '). Known: $($allScenarios.Id -join ', ')" }
    $scenarios = @($allScenarios | Where-Object { $_.Id -in $Scenario })
}

$runList = [System.Collections.Generic.List[object]]::new()
foreach ($s in $scenarios) {
    $configs = @($s.Configurations | Where-Object { -not $Configuration -or $_ -in $Configuration })
    $timeout = if ($PSBoundParameters.ContainsKey('TimeoutMinutes')) { $TimeoutMinutes }
    elseif ($s.TimeoutMinutes) { $s.TimeoutMinutes }
    else { [int]$config.timeoutMinutes }
    foreach ($c in $configs) {
        foreach ($m in $models) {
            foreach ($i in 1..$iterationCount) {
                $runList.Add([pscustomobject]@{ Scenario = $s; Configuration = $c; Model = $m; Iteration = $i; TimeoutMinutes = $timeout })
            }
        }
    }
}

if ($Plan) {
    Write-Host "Models:      $($models -join ', ')"
    Write-Host "Iterations:  $iterationCount"
    Write-Host "WinApp:      $(if ($WinAppPlugin) { $WinAppPlugin } else { $config.plugins.winapp.path })"
    $pub = $config.plugins.winui.published
    Write-Host "WinUI:       $(if ($WinUIPlugin -eq 'published') { "$($pub.repository)@$($pub.ref):$($pub.path)" } elseif ($WinUIPlugin) { $WinUIPlugin } else { $config.plugins.winui.path })"
    Write-Host ''
    $runList | Group-Object { $_.Scenario.Id } | ForEach-Object {
        $configs = ($_.Group.Configuration | Select-Object -Unique) -join ', '
        [pscustomobject]@{ Scenario = $_.Name; Configurations = $configs; Sessions = $_.Count }
    } | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "Total agent sessions: $($runList.Count)"
    return
}

if ($runList.Count -eq 0) { throw 'Nothing to run: the filters matched no scenario/configuration combinations.' }

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$GitArgs)
    $out = & git @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $out" }
    return $out
}

function Get-PluginInfo {
    param([string]$Name, [string]$Path, [string]$Source)
    $version = $null
    $manifest = Join-Path $Path 'plugin.json'
    if (Test-Path -LiteralPath $manifest) { $version = (Get-Content -Raw $manifest | ConvertFrom-Json).version }
    $sha = $null
    $dirty = $false
    $top = & git -C $Path rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $top) {
        $sha = (& git -C $Path rev-parse HEAD).Trim()
        $dirty = [bool](& git -C $Path status --porcelain -- . 2>$null)
    }
    [pscustomobject]@{
        Name    = $Name
        Path    = $Path
        Source  = $Source
        Version = $version
        Sha     = $sha
        Dirty   = $dirty
        Skills  = @(Get-PluginSkillNames -PluginPath $Path)
    }
}

function Resolve-PublishedWinUIPlugin {
    $w = $config.plugins.winui.published
    $safeRef = $w.ref -replace '[^\w.-]', '_'
    $cache = Join-Path $PSScriptRoot "results\.cache\win-dev-skills-$safeRef"
    if (-not (Test-Path -LiteralPath (Join-Path $cache '.git'))) {
        Write-Host "Fetching $($w.repository) at $($w.ref)..."
        if (Test-Path -LiteralPath $cache) { Remove-Item -Recurse -Force -LiteralPath $cache }
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        Invoke-Git -C $cache init --quiet | Out-Null
        Invoke-Git -C $cache fetch --quiet --depth 1 $w.repository $w.ref | Out-Null
        Invoke-Git -C $cache -c advice.detachedHead=false checkout --quiet FETCH_HEAD | Out-Null
    }
    $path = Join-Path $cache $w.path
    if (-not (Test-Path -LiteralPath $path)) { throw "WinUI plugin path not found in $($w.repository)@$($w.ref): $($w.path)" }
    return $path
}

# --- Resolve plugins needed by this run -------------------------------------------------
$neededPlugins = @($runList.Configuration | Select-Object -Unique | ForEach-Object { Get-ConfigurationPlugins $_ } | Select-Object -Unique)
$plugins = @{}
if ('winapp' -in $neededPlugins) {
    $path = if ($WinAppPlugin) { (Resolve-Path $WinAppPlugin).Path } else { (Resolve-Path (Join-Path $PSScriptRoot $config.plugins.winapp.path)).Path }
    $plugins.winapp = Get-PluginInfo -Name 'winapp' -Path $path -Source $(if ($WinAppPlugin) { 'local override' } else { 'this repo' })
}
if ('winui' -in $neededPlugins) {
    if ($WinUIPlugin -eq 'published') {
        $pub = $config.plugins.winui.published
        $plugins.winui = Get-PluginInfo -Name 'winui' -Path (Resolve-PublishedWinUIPlugin) -Source "$($pub.repository)@$($pub.ref)"
    }
    elseif ($WinUIPlugin) {
        $plugins.winui = Get-PluginInfo -Name 'winui' -Path (Resolve-Path $WinUIPlugin).Path -Source 'local override'
    }
    else {
        $plugins.winui = Get-PluginInfo -Name 'winui' -Path (Resolve-Path (Join-Path $PSScriptRoot $config.plugins.winui.path)).Path -Source 'this repo'
    }
}
foreach ($p in $plugins.Values) {
    if ($p.Skills.Count -eq 0) { throw "Plugin '$($p.Name)' at $($p.Path) has no skills." }
}

$knownSkills = @($plugins.Values.Skills)
foreach ($s in $scenarios) {
    foreach ($name in @($s.Expect.SkillsAny) + @($s.Expect.SkillsAll) + @($s.Expect.SkillsForbid)) {
        if ($name -notmatch '[*?]' -and $name -notin $knownSkills -and $plugins.Count -eq 2) {
            Write-Warning "Scenario '$($s.Id)' expects skill '$name', which neither plugin ships."
        }
    }
}

# --- Resolve one Copilot CLI build and pin it for every invocation -----------------------
$copilotExe = (Get-Command copilot -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-agent-bench\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

function Get-CopilotVersion {
    param([string[]]$ExtraArgs = @(), [switch]$AllowAutoUpdate)
    $probeHome = Join-Path $tempRoot "version-probe-$(Get-Random)"
    New-Item -ItemType Directory -Force -Path $probeHome | Out-Null
    try {
        Set-Content -Path (Join-Path $probeHome 'config.json') -Value '{"autoUpdate": false}'
        $envMap = New-ChildEnvironment -CopilotHome $probeHome
        # Without the env opt-out the launcher picks the newest already-downloaded build; the
        # config file above still prevents it from downloading anything.
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

# --- Output ------------------------------------------------------------------------------
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "results\$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$runsPath = Join-Path $OutDir 'runs.jsonl'

$pluginText = ($plugins.Values | Sort-Object Name | ForEach-Object {
        $sha = if ($_.Sha) { " @ $($_.Sha.Substring(0, 12))$(if ($_.Dirty) { ' (dirty)' })" } else { '' }
        "$($_.Name) v$($_.Version) from $($_.Source)$sha"
    }) -join '; '
$header = [ordered]@{
    'Started'        = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    'Copilot CLI'    = "$pinnedVersion ($copilotExe)"
    'Models'         = $models -join ', '
    'Iterations'     = $iterationCount
    'Scenarios'      = "$($scenarios.Count) ($($runList.Count) agent sessions)"
    'Configurations' = ($runList.Configuration | Select-Object -Unique) -join ', '
    'Plugins'        = $pluginText
}
[ordered]@{
    copilotVersion = $pinnedVersion
    copilotPath    = $copilotExe
    models         = $models
    iterations     = $iterationCount
    plugins        = @($plugins.Values | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; path = $_.Path; source = $_.Source; version = $_.Version; sha = $_.Sha; dirty = $_.Dirty; skills = $_.Skills } })
    scenarios      = @($scenarios.Id)
} | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $OutDir 'run-info.json') -Encoding utf8NoBOM

# --- Run ---------------------------------------------------------------------------------
function Invoke-BenchmarkRun {
    param($Run, [int]$Index)

    $s = $Run.Scenario
    $runId = '{0:D3}-{1}-{2}-{3}-{4}' -f $Index, $s.Id, $Run.Configuration, ($Run.Model -replace '[^\w.-]', '_'), $Run.Iteration
    $root = Join-Path $tempRoot $runId
    $copilotHome = Join-Path $root 'home'
    $ws = Join-Path $root 'workspace'
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force -Path $copilotHome, $ws, $logs | Out-Null
    Set-Content -Path (Join-Path $copilotHome 'config.json') -Value '{"autoUpdate": false}'
    if ($s.FixturePath) { Copy-Item -Path (Join-Path $s.FixturePath '*') -Destination $ws -Recurse -Force }
    $envMap = New-ChildEnvironment -CopilotHome $copilotHome

    $record = [ordered]@{
        runId                  = $runId
        scenario               = $s.Id
        configuration          = $Run.Configuration
        model                  = $Run.Model
        iteration              = $Run.Iteration
        status                 = $null
        reason                 = $null
        expectationNotes       = @()
        skillsLoaded           = @()
        skillsInvoked          = @()
        skillsContextDelivered = @()
        skillContextChars        = $null
        skillContextTokensApprox = $null
        skillContextReason       = $null
        tokens                 = $null
        tokensReason           = $null
        aiCredits              = $null
        premiumRequests        = $null
        modelTurns             = $null
        toolCalls              = $null
        toolCallsByName        = $null
        deniedToolCalls        = $null
        workspaceChanges       = $null
        durationMs             = $null
        exitCode               = $null
        copilotVersion         = $null
        reasoningEffort        = $null
        preflight              = $null
        artifacts              = $null
        runnerMemoryMB         = $null
    }

    $invoke = {
        param([string]$Name, [string[]]$CopilotArgs, [int]$TimeoutSeconds = 120)
        Invoke-LoggedProcess -FilePath $copilotExe -Arguments (@($copilotPrefix) + $CopilotArgs) -WorkingDirectory $ws `
            -Environment $envMap -StdoutPath (Join-Path $logs "$Name.out") -StderrPath (Join-Path $logs "$Name.err") -TimeoutSeconds $TimeoutSeconds
    }

    try {
        & git -C $ws rev-parse --is-inside-work-tree *> $null
        if ($LASTEXITCODE -eq 0) { throw "Workspace $ws is inside a git repository." }

        # 1. Install plugins into the isolated home.
        $installed = [System.Collections.Generic.List[string]]::new()
        foreach ($name in Get-ConfigurationPlugins $Run.Configuration) {
            $p = $plugins[$name]
            $r = & $invoke "install-$name" @('plugin', 'install', $p.Path)
            if ($r.ExitCode -ne 0) {
                $record.status = 'preflight_failed'
                $record.reason = "plugin install '$name' exited $($r.ExitCode): $(Get-FileTail (Join-Path $logs "install-$name.err") 300)"
                return $record
            }
            foreach ($skill in $p.Skills) { $installed.Add($skill) }
        }

        # 2. Preflight: plugin skills present must match the configuration exactly.
        $r = & $invoke 'skill-list' @('skill', 'list', '--json')
        if ($r.ExitCode -ne 0) {
            $record.status = 'preflight_failed'
            $record.reason = "skill list exited $($r.ExitCode): $(Get-FileTail (Join-Path $logs 'skill-list.err') 300)"
            return $record
        }
        $skillList = @(Get-Content -Raw (Join-Path $logs 'skill-list.out') | ConvertFrom-Json)
        $nonBuiltin = @($skillList | Where-Object { $_.source -ne 'builtin' })
        $unexpected = @($nonBuiltin | Where-Object { $_.name -notin $installed -or $_.source -ne 'plugin' -or -not $_.path.StartsWith($copilotHome, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { "$($_.name) ($($_.source))" })
        $missing = @($installed | Where-Object { $_ -notin @($nonBuiltin | Where-Object enabled | ForEach-Object name) })
        $record.preflight = [ordered]@{ expectedSkills = @($installed); builtinSkills = @($skillList | Where-Object source -eq 'builtin' | ForEach-Object name); unexpected = $unexpected; missing = $missing }
        if ($unexpected -or $missing) {
            $record.status = 'preflight_failed'
            $record.reason = "skill set mismatch; unexpected: [$($unexpected -join ', ')], missing: [$($missing -join ', ')]"
            return $record
        }

        # 3. The agent run: may read files and load skills; shell, file writes, and URLs are denied.
        $agentArgs = @(
            '-C', $ws,
            '-p', $s.Prompt,
            '--model', $Run.Model,
            '--output-format', 'json',
            '--stream', 'off',
            '--allow-all-tools',
            '--deny-tool=shell',
            '--deny-tool=write',
            '--deny-tool=url',
            '--disable-builtin-mcps',
            '--no-custom-instructions',
            '--no-ask-user'
        )
        $wsBefore = Get-DirectorySnapshot -Path $ws
        $r = & $invoke 'agent' $agentArgs ($Run.TimeoutMinutes * 60)
        $record.durationMs = $r.DurationMs
        $record.exitCode = $r.ExitCode
        $record.workspaceChanges = @(Compare-DirectorySnapshot -Before $wsBefore -After (Get-DirectorySnapshot -Path $ws))

        # 4. Parse the persisted session log line by line.
        $eventLogs = @(Get-ChildItem -Path (Join-Path $copilotHome 'session-state') -Filter events.jsonl -Recurse -File -ErrorAction SilentlyContinue)
        $eventLog = $eventLogs | Sort-Object Length -Descending | Select-Object -First 1
        $parsed = Read-SessionEvents -Path ($eventLog ? $eventLog.FullName : '')
        $record.copilotVersion = $parsed.copilotVersion
        $record.skillsInvoked = @($parsed.skillsInvoked)
        $record.skillsLoaded = @($parsed.skillsInvoked | ForEach-Object { $_.name } | Select-Object -Unique)
        $record.skillsContextDelivered = @($parsed.skillsContextDelivered)
        $record.skillContextChars = $parsed.skillContextChars
        $record.skillContextTokensApprox = $parsed.skillContextTokensApprox
        $record.skillContextReason = $parsed.skillContextReason
        $record.tokens = $parsed.tokens
        $record.tokensReason = $parsed.tokensReason
        $record.aiCredits = $parsed.aiCredits
        $record.premiumRequests = $parsed.premiumRequests
        $record.reasoningEffort = $parsed.reasoningEffort
        $record.modelTurns = $parsed.modelTurns
        $record.toolCalls = $parsed.toolCalls
        $record.toolCallsByName = $parsed.toolCallsByName
        $record.deniedToolCalls = $parsed.deniedToolCalls

        if ($record.workspaceChanges) {
            # The run was supposed to be read-only; its result is not comparable.
            $record.status = 'harness_error'
            $record.reason = "agent modified the workspace despite denied write/shell tools: $($record.workspaceChanges -join ', ')"
        }
        elseif ($r.TimedOut) {
            $record.status = 'timeout'
            $record.reason = "exceeded $($Run.TimeoutMinutes) min"
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
            $eval = Test-Expectations -Expect $s.Expect -LoadedSkills $record.skillsLoaded -InstalledSkills @($installed)
            $record.status = $eval.Passed ? 'pass' : 'fail'
            $record.reason = $eval.Failures -join '; '
            $record.expectationNotes = $eval.Notes
        }
        if ($eventLogs.Count -gt 1) { $record.expectationNotes += "found $($eventLogs.Count) events.jsonl files; parsed the largest" }
    }
    catch {
        $record.status = 'harness_error'
        $record.reason = $_.Exception.Message
    }
    finally {
        if ($KeepArtifacts) {
            $record.artifacts = $root
        }
        else {
            $removed = $false
            foreach ($attempt in 1..5) {
                try { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction Stop; $removed = $true; break }
                catch { Start-Sleep -Seconds 2 }
            }
            if (-not $removed) {
                $record.reason = "[$($record.status)] $($record.reason) | could not delete $root"
                $record.status = 'cleanup_failed'
            }
        }
        $record.runnerMemoryMB = [Math]::Round([System.Diagnostics.Process]::GetCurrentProcess().WorkingSet64 / 1MB, 1)
    }
    return $record
}

Write-Host "Copilot CLI $pinnedVersion | $($runList.Count) agent sessions | results: $OutDir"
$index = 0
foreach ($run in $runList) {
    $index++
    $label = "[$index/$($runList.Count)] $($run.Scenario.Id) | $($run.Configuration) | $($run.Model) | #$($run.Iteration)"
    Write-Host "$label ..." -NoNewline
    $rec = Invoke-BenchmarkRun -Run $run -Index $index
    $rec | ConvertTo-Json -Depth 8 -Compress | Add-Content -Path $runsPath -Encoding utf8NoBOM
    $skills = if ($rec.skillsLoaded) { $rec.skillsLoaded -join ', ' } else { '(none)' }
    $tok = if ($rec.tokens) { "in $($rec.tokens.input) / out $($rec.tokens.output)" } else { 'tokens n/a' }
    if ($null -ne $rec.skillContextTokensApprox) { $tok += " / skill ctx ~$($rec.skillContextTokensApprox)" }
    $dur = if ($null -ne $rec.durationMs) { '{0:N0}s' -f ($rec.durationMs / 1000) } else { '' }
    Write-Host " $($rec.status) | skills: $skills | $tok | $dur"
    if ($rec.status -notin 'pass', 'fail' -and $rec.reason) { Write-Host "    $($rec.reason)" }
}

$header['Finished'] = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
Write-BenchmarkSummary -RunsPath $runsPath -SummaryPath (Join-Path $OutDir 'summary.md') -Header $header -ScenarioOrder @($scenarios.Id)
if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    $tempParent = Split-Path $tempRoot
    if (-not (Get-ChildItem -LiteralPath $tempParent -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $tempParent -ErrorAction SilentlyContinue }
}
Write-Host "Summary: $(Join-Path $OutDir 'summary.md')"
