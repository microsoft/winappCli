Set-StrictMode -Version Latest

$script:ValidConfigurations = @('none', 'winapp', 'winui', 'both')
$script:ValidSets = @('dev', 'heldout')
# Implicit routing results leave out explicit-command scenarios (the prompt names the command).
$script:ValidCohorts = @('implicit', 'error', 'vague', 'followup', 'explicit-command', 'near-miss', 'trap')
$script:CapabilityMap = $null
$script:DefaultCapabilityPath = Join-Path $PSScriptRoot '..\capabilities.json'

# Environment variables that can change which skills, instructions, or CLI build a
# child Copilot process picks up. Auth variables are kept so the child can sign in.
$script:StrippedEnvPattern = '^(COPILOT_|GITHUB_COPILOT_|CLAUDE|AI_AGENT$|RUBBER_DUCK)'
$script:KeptEnvNames = @('COPILOT_GITHUB_TOKEN')

function Split-ListArgument {
    # Splits comma-separated items so list parameters work from PowerShell and from `pwsh -File`.
    param([AllowNull()][AllowEmptyCollection()][string[]]$Value)
    if (-not $Value) { return @() }
    return @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-ScenarioDefinitions {
    param([Parameter(Mandatory)][string]$ScenariosRoot)

    $scenarios = foreach ($file in Get-ChildItem -Path $ScenariosRoot -Filter scenario.json -Recurse -File | Sort-Object FullName) {
        $s = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json -AsHashtable
        $dir = $file.Directory.FullName
        foreach ($key in 'id', 'description', 'prompt', 'configurations', 'expect') {
            if (-not $s.ContainsKey($key) -or $null -eq $s[$key]) { throw "Scenario '$($file.FullName)' is missing '$key'." }
        }
        if ($s.id -ne $file.Directory.Name) { throw "Scenario id '$($s.id)' must match its folder name '$($file.Directory.Name)'." }
        $bad = @($s.configurations | Where-Object { $_ -notin $script:ValidConfigurations })
        if ($bad) { throw "Scenario '$($s.id)' has unknown configurations: $($bad -join ', ')." }
        # Users typically install both plugins, so every scenario must measure that setup.
        if ('both' -notin $s.configurations) { throw "Scenario '$($s.id)' must include the 'both' configuration." }
        $fixture = $null
        if ($s.ContainsKey('fixture') -and $s.fixture) {
            $fixture = [System.IO.Path]::GetFullPath((Join-Path $dir $s.fixture))
            if (-not (Test-Path -LiteralPath $fixture -PathType Container)) { throw "Scenario '$($s.id)' fixture folder not found: $fixture" }
        }
        $set = if ($s.ContainsKey('set') -and $s.set) { $s.set } else { 'dev' }
        if ($set -notin $script:ValidSets) { throw "Scenario '$($s.id)' has unknown set '$set'. Use $($script:ValidSets -join ' or ')." }
        if (-not $s.ContainsKey('cohort') -or $s.cohort -notin $script:ValidCohorts) {
            throw "Scenario '$($s.id)' needs a cohort: $($script:ValidCohorts -join ', ')."
        }
        $expect = $s.expect
        $caps = $null
        if ($expect.ContainsKey('capabilities') -and $expect.capabilities) {
            $c = $expect.capabilities
            # A primary entry is one capability, or an array of capabilities that must all load.
            $primary = @(if ($c.ContainsKey('primary')) { foreach ($p in $c.primary) { , @($p) } })
            $caps = [pscustomobject]@{
                Primary      = $primary
                Acceptable   = @(if ($c.ContainsKey('acceptable')) { $c.acceptable })
                Forbid       = @(if ($c.ContainsKey('forbid')) { $c.forbid })
                BudgetTokens = if ($c.ContainsKey('budgetTokens')) { $c.budgetTokens } else { $null }
            }
        }
        $expectObject = [pscustomobject]@{
            SkillsAny    = @(if ($expect.ContainsKey('skillsAny')) { $expect.skillsAny })
            SkillsAll    = @(if ($expect.ContainsKey('skillsAll')) { $expect.skillsAll })
            SkillsForbid = @(if ($expect.ContainsKey('skillsForbid')) { $expect.skillsForbid })
            MaxSkills    = if ($expect.ContainsKey('maxSkills')) { $expect.maxSkills } else { $null }
            Capabilities = $caps
            Commands     = @(if ($expect.ContainsKey('commands')) { $expect.commands })
        }
        # Paraphrases share the fixture and expectations and run as separate scenarios '<id>.<variant>'.
        $variants = [ordered]@{ '' = $s.prompt }
        if ($s.ContainsKey('paraphrases') -and $s.paraphrases) {
            foreach ($k in $s.paraphrases.Keys) {
                if ($k -notmatch '^[a-z][a-z0-9-]*$') { throw "Scenario '$($s.id)' paraphrase name '$k' must be lowercase kebab-case." }
                $variants[$k] = $s.paraphrases[$k]
            }
        }
        foreach ($v in $variants.Keys) {
            [pscustomobject]@{
                Id               = if ($v) { "$($s.id).$v" } else { $s.id }
                BaseId           = $s.id
                Variant          = if ($v) { $v } else { 'base' }
                Set              = $set
                Cohort           = $s.cohort
                Description      = $s.description
                Prompt           = $variants[$v]
                Configurations   = @($s.configurations)
                FixturePath      = $fixture
                RoutingSnapshot  = [bool]($s.ContainsKey('routingSnapshot') -and $s.routingSnapshot)
                LeakAllow        = @(if ($s.ContainsKey('leakAllow')) { $s.leakAllow })
                ScenarioFile     = $file.FullName
                TimeoutMinutes   = if ($s.ContainsKey('timeoutMinutes')) { $s.timeoutMinutes } else { $null }
                Expect           = $expectObject
            }
        }
    }
    $dupes = @($scenarios | Group-Object Id | Where-Object Count -gt 1)
    if ($dupes) { throw "Duplicate scenario ids: $($dupes.Name -join ', ')" }
    return @($scenarios)
}

function Get-PluginSkillNames {
    # Skill names a plugin folder ships: frontmatter `name:` of skills/<dir>/SKILL.md, falling back to the folder name.
    param([Parameter(Mandatory)][string]$PluginPath)

    $skillsDir = Join-Path $PluginPath 'skills'
    if (-not (Test-Path -LiteralPath $skillsDir)) { return @() }
    $names = foreach ($dir in Get-ChildItem -LiteralPath $skillsDir -Directory) {
        $md = Join-Path $dir.FullName 'SKILL.md'
        if (-not (Test-Path -LiteralPath $md)) { continue }
        $name = $dir.Name
        $inFrontmatter = $false
        foreach ($line in [System.IO.File]::ReadLines($md)) {
            if ($line.Trim() -eq '---') { if ($inFrontmatter) { break } else { $inFrontmatter = $true; continue } }
            if ($inFrontmatter -and $line -match '^name:\s*["'']?([^"'']+?)["'']?\s*$') { $name = $Matches[1]; break }
        }
        $name
    }
    return @($names | Sort-Object -Unique)
}

function Get-ConfigurationPlugins {
    param([Parameter(Mandatory)][string]$Configuration)
    switch ($Configuration) {
        'none' { @() }
        'winapp' { @('winapp') }
        'winui' { @('winui') }
        'both' { @('winapp', 'winui') }
        default { throw "Unknown configuration '$Configuration'." }
    }
}

function New-ChildEnvironment {
    # Copy of the current environment with skill/instruction/CLI-selection variables removed.
    param([Parameter(Mandatory)][string]$CopilotHome)

    $envMap = [ordered]@{}
    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $name = [string]$entry.Key
        if ($name -match $script:StrippedEnvPattern -and $name -notin $script:KeptEnvNames) { continue }
        $envMap[$name] = [string]$entry.Value
    }
    $envMap['COPILOT_HOME'] = $CopilotHome
    $envMap['COPILOT_AUTO_UPDATE'] = 'false'
    $envMap['NO_COLOR'] = '1'
    return $envMap
}

function Invoke-LoggedProcess {
    # Runs a process with stdout/stderr streamed straight to files (never buffered in memory).
    # On timeout, kills only the process tree it started.
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Environment,
        [Parameter(Mandatory)][string]$StdoutPath,
        [Parameter(Mandatory)][string]$StderrPath,
        [int]$TimeoutSeconds = 300
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment.Clear()
    foreach ($k in $Environment.Keys) { $psi.Environment[$k] = $Environment[$k] }

    $out = [System.IO.File]::Create($StdoutPath)
    $err = [System.IO.File]::Create($StderrPath)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.StandardInput.Close()
        $copyOut = $proc.StandardOutput.BaseStream.CopyToAsync($out)
        $copyErr = $proc.StandardError.BaseStream.CopyToAsync($err)
        $timedOut = -not $proc.WaitForExit([int]([Math]::Min([int]::MaxValue, $TimeoutSeconds * 1000L)))
        if ($timedOut) {
            try { $proc.Kill($true) } catch { Write-Verbose "Kill failed: $_" }
            [void]$proc.WaitForExit(30000)
        }
        else {
            $proc.WaitForExit()
        }
        [void][System.Threading.Tasks.Task]::WaitAll(@($copyOut, $copyErr), 30000)
        $sw.Stop()
        return [pscustomobject]@{
            ExitCode   = if ($proc.HasExited) { $proc.ExitCode } else { $null }
            TimedOut   = $timedOut
            DurationMs = [int64]$sw.ElapsedMilliseconds
        }
    }
    finally {
        $out.Dispose()
        $err.Dispose()
        if ($proc) { $proc.Dispose() }
    }
}

function Get-FileTail {
    param([string]$Path, [int]$MaxChars = 600)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -eq 0) { return $null }
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $take = [int][Math]::Min($len, $MaxChars * 4)
        [void]$fs.Seek(-$take, [System.IO.SeekOrigin]::End)
        $buf = [byte[]]::new($take)
        [void]$fs.Read($buf, 0, $take)
        $text = [System.Text.Encoding]::UTF8.GetString($buf).Trim()
        if ($text.Length -gt $MaxChars) { $text = $text.Substring($text.Length - $MaxChars) }
        return $text
    }
    finally { $fs.Dispose() }
}

function Get-BareSkillName {
    # Copilot lists a skill name shipped by more than one plugin as '<plugin>:<name>'.
    param([string]$Name)
    if ($Name -match '^[^:]+:(.+)$') { return $Matches[1] }
    return $Name
}

function Get-WinappCommands {
    # Unique 'winapp <command> [<subcommand>]' invocations named in shell commands or answer text.
    param([AllowEmptyCollection()][AllowNull()][string[]]$Text)
    $groups = 'cert', 'manifest', 'ui', 'find-api', 'target', 'store'
    # Top-level commands only, so prose like "winapp is" or "winapp CLI" is not counted.
    $known = 'az-sign', 'cert', 'create-debug-identity', 'create-external-catalog', 'embed-identity', 'find-api', 'find-ui',
    'get-winapp-path', 'init', 'manifest', 'new', 'package', 'pack', 'restore', 'run', 'sign', 'store', 'target', 'tool',
    'ui', 'unregister', 'update'
    $found = [System.Collections.Generic.List[string]]::new()
    foreach ($t in $Text) {
        if (-not $t) { continue }
        foreach ($m in [regex]::Matches($t, '(?<![\w./\\-])winapp(?:\.exe)?\s+([a-z][a-z-]*)(?:\s+([a-z][a-z-]*))?')) {
            $cmd = $m.Groups[1].Value
            if ($cmd -notin $known) { continue }
            if ($cmd -in $groups -and $m.Groups[2].Success) { $cmd += " $($m.Groups[2].Value)" }
            if (-not $found.Contains($cmd)) { $found.Add($cmd) }
        }
    }
    return $found.ToArray()
}

function Read-SessionEvents {
    # Streams a persisted Copilot CLI events.jsonl and returns a small summary.
    # Values that the log does not contain are $null with a reason, never 0.
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)

    $r = [ordered]@{
        copilotVersion         = $null
        model                  = $null
        reasoningEffort        = $null
        skillsInvoked          = [System.Collections.Generic.List[object]]::new()
        skillsContextDelivered = [System.Collections.Generic.List[string]]::new()
        # Characters of skill content delivered into the model context, and a chars/4 token estimate.
        skillContextChars        = $null
        skillContextTokensApprox = $null
        skillContextReason       = $null
        # Deliveries of a skill beyond its first in the session, and the approximate tokens they added.
        skillRepeatDeliveries          = $null
        skillRepeatContextTokensApprox = $null
        tokens                 = $null
        tokensReason           = $null
        aiCredits              = $null
        premiumRequests        = $null
        modelTurns             = $null
        toolCalls              = $null
        toolCallsByName        = [ordered]@{}
        deniedToolCalls        = [ordered]@{}
        # winapp CLI commands the agent tried to run (shell is denied) or named in its final answer.
        winappCommands         = [System.Collections.Generic.List[string]]::new()
        selectedAgent          = $null
        sessionShutdown        = $false
        eventCount             = 0
        unparsedLines          = 0
    }
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
        $r.tokensReason = 'no persisted events log'
        $r.skillContextReason = 'no persisted events log'
        return [pscustomobject]$r
    }

    $turns = 0
    $tools = 0
    $usage = $null
    $toolNames = @{}
    $skillContentLength = @{}
    $deliveries = [System.Collections.Generic.List[hashtable]]::new()
    $commandText = [System.Collections.Generic.List[string]]::new()
    $lastMessage = $null
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $ev = $line | ConvertFrom-Json -AsHashtable -Depth 64 }
        catch { $r.unparsedLines++; continue }
        $r.eventCount++
        $data = if ($ev.ContainsKey('data') -and $ev.data -is [System.Collections.IDictionary]) { $ev.data } else { @{} }
        switch ($ev.type) {
            'session.start' {
                if ($data.ContainsKey('copilotVersion')) { $r.copilotVersion = $data.copilotVersion }
                if ($data.ContainsKey('selectedModel')) { $r.model = $data.selectedModel }
                if ($data.ContainsKey('reasoningEffort')) { $r.reasoningEffort = $data.reasoningEffort }
            }
            'session.model_change' {
                if ($data.ContainsKey('newModel')) { $r.model = $data.newModel }
            }
            'skill.invoked' {
                if ($data.ContainsKey('content') -and $data.content -is [string]) { $skillContentLength[[string]$data.name] = $data.content.Length }
                $r.skillsInvoked.Add([ordered]@{
                        name    = $data.name
                        trigger = $data.ContainsKey('trigger') ? $data.trigger : $null
                        source  = $data.ContainsKey('source') ? $data.source : $null
                        plugin  = $data.ContainsKey('pluginName') ? $data.pluginName : $null
                    })
            }
            { $_ -like 'skill.context_delivered*' } {
                # e.g. {"source":"skill-winapp-setup","prefix":"<skill-context name=\"winapp-setup\">..."}
                $n = $null
                if ($data.ContainsKey('prefix') -and $data.prefix -match '<skill-context name="([^"]+)"') { $n = $Matches[1] }
                elseif ($data.ContainsKey('source') -and $data.source -like 'skill-*') { $n = $data.source.Substring(6) }
                if ($n -and -not $r.skillsContextDelivered.Contains($n)) { $r.skillsContextDelivered.Add($n) }
                # The _ref event carries only a content hash; the body length comes from skill.invoked.
                $wrapper = ([string]$data.prefix).Length + ([string]$data.suffix).Length
                $bodyLength = if ($data.ContainsKey('content') -and $data.content -is [string]) { $data.content.Length } else { $null }
                $deliveries.Add(@{ name = $n; wrapper = $wrapper; body = $bodyLength })
            }
            'assistant.turn_start' { $turns++ }
            'subagent.selected' {
                if (-not $r.selectedAgent -and $data.ContainsKey('agentName')) { $r.selectedAgent = $data.agentName }
            }
            'assistant.message' {
                if ($data.ContainsKey('content') -and $data.content -is [string] -and $data.content.Trim()) { $lastMessage = $data.content }
            }
            'tool.execution_start' {
                $tools++
                $tn = if ($data.ContainsKey('toolName')) { [string]$data.toolName } else { '(unknown)' }
                $r.toolCallsByName[$tn] = 1 + ($r.toolCallsByName.Contains($tn) ? $r.toolCallsByName[$tn] : 0)
                if ($data.ContainsKey('toolCallId')) { $toolNames[[string]$data.toolCallId] = $tn }
                if ($data.arguments -is [System.Collections.IDictionary] -and $data.arguments.ContainsKey('command') -and $data.arguments.command -is [string]) { $commandText.Add($data.arguments.command) }
            }
            'tool.execution_complete' {
                if ($data.ContainsKey('success') -and $data.success -eq $false -and $data.error -is [System.Collections.IDictionary] -and $data.error.code -eq 'denied') {
                    $id = [string]$data.toolCallId
                    $tn = $toolNames.ContainsKey($id) ? $toolNames[$id] : '(unknown)'
                    $r.deniedToolCalls[$tn] = 1 + ($r.deniedToolCalls.Contains($tn) ? $r.deniedToolCalls[$tn] : 0)
                }
            }
            'session.shutdown' {
                $r.sessionShutdown = $true
                if ($data.ContainsKey('totalNanoAiu') -and $null -ne $data.totalNanoAiu) { $r.aiCredits = [Math]::Round([double]$data.totalNanoAiu / 1e9, 3) }
                if ($data.ContainsKey('totalPremiumRequests')) { $r.premiumRequests = $data.totalPremiumRequests }
                if ($data.ContainsKey('modelMetrics') -and $data.modelMetrics -is [System.Collections.IDictionary] -and $data.modelMetrics.Count -gt 0) {
                    # inputTokens is the full prompt size; cacheRead/cacheWrite are the cached portions of it.
                    $usage = [ordered]@{ input = 0L; output = 0L; cacheRead = 0L; cacheWrite = 0L; reasoning = 0L }
                    foreach ($m in $data.modelMetrics.GetEnumerator()) {
                        $u = $m.Value.usage
                        if ($null -eq $u) { continue }
                        $usage.input += [int64]($u.inputTokens ?? 0)
                        $usage.output += [int64]($u.outputTokens ?? 0)
                        $usage.cacheRead += [int64]($u.cacheReadTokens ?? 0)
                        $usage.cacheWrite += [int64]($u.cacheWriteTokens ?? 0)
                        $usage.reasoning += [int64]($u.reasoningTokens ?? 0)
                    }
                }
            }
        }
    }

    if ($r.eventCount -gt 0) {
        $r.modelTurns = $turns
        $r.toolCalls = $tools
        foreach ($c in Get-WinappCommands -Text (@($commandText) + @($lastMessage))) { $r.winappCommands.Add($c) }
        $chars = 0L
        $repeatChars = 0L
        $repeats = 0
        $seen = [System.Collections.Generic.HashSet[string]]::new()
        $unknown = @()
        foreach ($d in $deliveries) {
            $isRepeat = $d.name -and -not $seen.Add($d.name)
            if ($isRepeat) { $repeats++ }
            $body = $d.body ?? ($d.name -and $skillContentLength.ContainsKey($d.name) ? $skillContentLength[$d.name] : $null)
            if ($null -eq $body) { $unknown += $d.name; continue }
            $chars += $d.wrapper + $body
            if ($isRepeat) { $repeatChars += $d.wrapper + $body }
        }
        $r.skillRepeatDeliveries = $repeats
        if ($unknown) {
            $r.skillContextReason = "body length unknown for: $(($unknown | Select-Object -Unique) -join ', ')"
        }
        else {
            $r.skillContextChars = $chars
            $r.skillContextTokensApprox = [int64][Math]::Round($chars / 4)
            $r.skillRepeatContextTokensApprox = [int64][Math]::Round($repeatChars / 4)
        }
    }
    else {
        $r.skillContextReason = 'events log is empty'
    }
    if ($usage) { $r.tokens = $usage }
    elseif (-not $r.sessionShutdown) { $r.tokensReason = 'session.shutdown event missing' }
    else { $r.tokensReason = 'session.shutdown has no modelMetrics usage' }
    return [pscustomobject]$r
}

function Get-DirectorySnapshot {
    # Relative path -> SHA256 for every file under a directory; used to prove a run changed nothing.
    param([Parameter(Mandatory)][string]$Path)
    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\') + '\'
    $map = [ordered]@{}
    foreach ($f in Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName) {
        $map[$f.FullName.Substring($root.Length)] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Compare-DirectorySnapshot {
    param([System.Collections.IDictionary]$Before, [System.Collections.IDictionary]$After)
    $changes = foreach ($k in @($Before.Keys) + @($After.Keys) | Select-Object -Unique) {
        if (-not $After.Contains($k)) { "deleted $k" }
        elseif (-not $Before.Contains($k)) { "added $k" }
        elseif ($Before[$k] -ne $After[$k]) { "modified $k" }
    }
    return @($changes)
}

function Test-SkillMatch {
    param([string]$Name, [string[]]$Patterns)
    foreach ($p in $Patterns) { if ($Name -like $p) { return $true } }
    return $false
}

function Test-Expectations {
    # Evaluates scenario expectations against loaded skills. Expected skills that are not
    # installed in this configuration are ignored, so one scenario can run across configurations.
    # Status is 'fail' on any failure, 'n/a' when nothing in the expectation applies to the
    # installed skills, else 'pass'.
    param(
        [Parameter(Mandatory)]$Expect,
        [AllowEmptyCollection()][string[]]$LoadedSkills = @(),
        [AllowEmptyCollection()][string[]]$InstalledSkills = @()
    )

    $loaded = @($LoadedSkills | Select-Object -Unique)
    $failures = [System.Collections.Generic.List[string]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()

    $any = @($Expect.SkillsAny | Where-Object { $p = $_; @($InstalledSkills | Where-Object { $_ -like $p }).Count -gt 0 })
    if ($Expect.SkillsAny.Count -gt 0 -and $any.Count -eq 0) {
        $notes.Add('skillsAny not applicable: none installed')
    }
    elseif ($any.Count -gt 0 -and -not @($loaded | Where-Object { Test-SkillMatch $_ $any })) {
        $failures.Add("none of skillsAny loaded ($($any -join ', '))")
    }

    foreach ($p in $Expect.SkillsAll) {
        if (-not @($InstalledSkills | Where-Object { $_ -like $p })) { $notes.Add("skillsAll '$p' not installed"); continue }
        if (-not @($loaded | Where-Object { $_ -like $p })) { $failures.Add("required skill not loaded: $p") }
    }

    $forbidden = @($loaded | Where-Object { Test-SkillMatch $_ $Expect.SkillsForbid })
    if ($forbidden) { $failures.Add("forbidden skill loaded: $($forbidden -join ', ')") }

    if ($null -ne $Expect.MaxSkills -and $loaded.Count -gt $Expect.MaxSkills) {
        $failures.Add("loaded $($loaded.Count) skills, max $($Expect.MaxSkills)")
    }

    # A run proves nothing when no installed skill is expected or forbidden and no skill count is
    # bounded: it passes whatever the agent does. Forbid-only checks (no overreach) still count.
    $patterns = @($Expect.SkillsAny) + @($Expect.SkillsAll) + @($Expect.SkillsForbid)
    $relevant = @($InstalledSkills | Where-Object { Test-SkillMatch $_ $patterns })
    $notApplicable = $relevant.Count -eq 0 -and $null -eq $Expect.MaxSkills
    $positive = @($Expect.SkillsAny) + @($Expect.SkillsAll)
    $expectedInstalled = @($InstalledSkills | Where-Object { Test-SkillMatch $_ $positive }).Count -gt 0

    $status = if ($failures.Count -gt 0) { 'fail' } elseif ($notApplicable) { 'n/a' } else { 'pass' }
    return [pscustomobject]@{
        Status            = $status
        Passed            = $failures.Count -eq 0
        NotApplicable     = $notApplicable
        ExpectedInstalled = $expectedInstalled
        Failures          = @($failures)
        Notes             = @($notes)
    }
}

function Get-ShortHash {
    param([AllowEmptyString()][string]$Text)
    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Text))
    return ([System.Convert]::ToHexString($bytes).ToLowerInvariant()).Substring(0, 12)
}

function Get-SkillSetHash {
    # Short, stable hash of a plugin's skill names; identifies which capability map describes it.
    param([AllowEmptyCollection()][string[]]$SkillNames = @())
    return Get-ShortHash (@($SkillNames | Sort-Object -Unique) -join "`n")
}

function Read-CapabilityMap {
    # Loads capabilities.json: capability definitions (with winapp command patterns) and, per plugin
    # version, the capabilities each skill provides.
    param([Parameter(Mandatory)][string]$Path)
    $j = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable
    $caps = [ordered]@{}
    foreach ($k in $j.capabilities.Keys) {
        $caps[$k] = [pscustomobject]@{ Description = $j.capabilities[$k].description; Commands = @($j.capabilities[$k].commands) }
    }
    $maps = foreach ($m in $j.maps) {
        foreach ($skill in $m.skills.Keys) {
            $unknown = @($m.skills[$skill] | Where-Object { -not $caps.Contains($_) })
            if ($unknown) { throw "Capability map '$($m.id)' maps '$skill' to unknown capabilities: $($unknown -join ', ')" }
        }
        [pscustomobject]@{ Id = $m.id; Plugin = $m.plugin; Source = $m.source; SkillSetHash = $m.skillSetHash; Skills = $m.skills }
    }
    return [pscustomobject]@{ Path = $Path; Capabilities = $caps; Maps = @($maps) }
}

function Get-CapabilityMap {
    if (-not $script:CapabilityMap) { $script:CapabilityMap = Read-CapabilityMap -Path $script:DefaultCapabilityPath }
    return $script:CapabilityMap
}

function Set-CapabilityMap {
    param([Parameter(Mandatory)][string]$Path)
    $script:CapabilityMap = Read-CapabilityMap -Path $Path
}

function Resolve-SkillCapabilities {
    # A plugin map applies when every skill it names is installed. When several maps of one plugin
    # apply, the one naming the most skills wins. Installed skills no applied map names are unmapped.
    param([AllowEmptyCollection()][string[]]$InstalledSkills = @(), $Map = (Get-CapabilityMap))
    $installed = @($InstalledSkills | Select-Object -Unique)
    $applied = foreach ($g in ($Map.Maps | Group-Object Plugin)) {
        $g.Group | Where-Object { $m = $_; -not @($m.Skills.Keys | Where-Object { $_ -notin $installed }) } |
            Sort-Object { $_.Skills.Count } -Descending -Stable | Select-Object -First 1
    }
    $skillCaps = @{}
    foreach ($m in @($applied)) { foreach ($s in $m.Skills.Keys) { $skillCaps[$s] = @($m.Skills[$s]) } }
    [pscustomobject]@{
        SkillCapabilities = $skillCaps
        Maps              = @($applied | ForEach-Object Id)
        Unmapped          = @($installed | Where-Object { -not $skillCaps.ContainsKey($_) })
    }
}

function Test-CapabilityExpectations {
    # Scores a run against capability expectations:
    #   pass    - a primary alternative (one capability, or a group that must all load) fully loaded
    #   partial - only part of a primary group, or only acceptable capabilities, loaded
    #   fail    - a forbidden capability loaded, maxSkills exceeded, or nothing expected loaded
    #   n/a     - no primary alternative and no forbidden capability is installed, and no maxSkills
    param(
        [Parameter(Mandatory)]$Expect,
        [AllowEmptyCollection()][string[]]$LoadedSkills = @(),
        [AllowEmptyCollection()][string[]]$InstalledSkills = @(),
        $Map = (Get-CapabilityMap)
    )
    $c = $Expect.Capabilities
    $resolved = Resolve-SkillCapabilities -InstalledSkills $InstalledSkills -Map $Map
    $skillCaps = $resolved.SkillCapabilities
    $loaded = @($LoadedSkills | Select-Object -Unique)
    $installedCaps = @($InstalledSkills | Where-Object { $skillCaps.ContainsKey($_) } | ForEach-Object { $skillCaps[$_] } | Select-Object -Unique)
    $loadedCaps = @($loaded | Where-Object { $skillCaps.ContainsKey($_) } | ForEach-Object { $skillCaps[$_] } | Select-Object -Unique)
    $failures = [System.Collections.Generic.List[string]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()
    if ($resolved.Unmapped) { $notes.Add("installed skills without a capability map: $($resolved.Unmapped -join ', ')") }

    $forbidden = @($loadedCaps | Where-Object { Test-SkillMatch $_ $c.Forbid })
    if ($forbidden) {
        $by = @($loaded | Where-Object { $skillCaps.ContainsKey($_) -and @($skillCaps[$_] | Where-Object { $_ -in $forbidden }) })
        $failures.Add("forbidden capability loaded: $($forbidden -join ', ') (via $($by -join ', '))")
    }
    if ($null -ne $Expect.MaxSkills -and $loaded.Count -gt $Expect.MaxSkills) {
        $failures.Add("loaded $($loaded.Count) skills, max $($Expect.MaxSkills)")
    }

    $primaryInstalled = @($c.Primary | Where-Object { $alt = @($_); -not @($alt | Where-Object { $_ -notin $installedCaps }) })
    $partial = $false
    if ($c.Primary.Count -gt 0 -and $primaryInstalled.Count -eq 0) { $notes.Add('primary capability not installed') }
    if ($primaryInstalled.Count -gt 0) {
        $met = @($primaryInstalled | Where-Object { $alt = @($_); -not @($alt | Where-Object { $_ -notin $loadedCaps }) })
        if (-not $met) {
            $some = @(@($primaryInstalled | ForEach-Object { $_ }) + @($c.Acceptable) | Where-Object { $_ -in $loadedCaps } | Select-Object -Unique)
            if ($some) { $partial = $true; $notes.Add("partial: loaded $($some -join ', ') but no complete primary alternative") }
            else { $failures.Add("no primary capability loaded ($(@($primaryInstalled | ForEach-Object { @($_) -join '+' }) -join ' | '))") }
        }
    }

    $forbidApplies = @($installedCaps | Where-Object { Test-SkillMatch $_ $c.Forbid }).Count -gt 0
    $notApplicable = $primaryInstalled.Count -eq 0 -and -not $forbidApplies -and $null -eq $Expect.MaxSkills
    $status = if ($failures.Count -gt 0) { 'fail' } elseif ($partial) { 'partial' } elseif ($notApplicable) { 'n/a' } else { 'pass' }
    [pscustomobject]@{
        Status             = $status
        Passed             = $status -eq 'pass'
        NotApplicable      = $status -eq 'n/a'
        ExpectedInstalled  = $primaryInstalled.Count -gt 0
        Failures           = @($failures)
        Notes              = @($notes)
        CapabilitiesLoaded = $loadedCaps
    }
}

function Test-ScenarioExpectations {
    # Scores a run with the scenario's capability expectations, or its skill-name expectations when it
    # has none, and adds the command and budget signals shared by both.
    param(
        [Parameter(Mandatory)]$Expect,
        [AllowEmptyCollection()][string[]]$LoadedSkills = @(),
        [AllowEmptyCollection()][string[]]$InstalledSkills = @(),
        [AllowNull()][AllowEmptyCollection()][string[]]$WinappCommands = $null,
        [AllowNull()]$SkillContextTokens = $null,
        $Map = (Get-CapabilityMap)
    )
    $caps = $Expect.PSObject.Properties['Capabilities'] ? $Expect.Capabilities : $null
    if ($caps) {
        $r = Test-CapabilityExpectations -Expect $Expect -LoadedSkills $LoadedSkills -InstalledSkills $InstalledSkills -Map $Map
    }
    else {
        $legacy = Test-Expectations -Expect $Expect -LoadedSkills $LoadedSkills -InstalledSkills $InstalledSkills
        $r = [pscustomobject]@{
            Status = $legacy.Status; Passed = $legacy.Passed; NotApplicable = $legacy.NotApplicable
            ExpectedInstalled = $legacy.ExpectedInstalled
            Failures = $legacy.Failures; Notes = $legacy.Notes; CapabilitiesLoaded = $null
        }
    }

    # The right winapp command named (tried in the shell or in the answer), from the scenario's
    # `commands` or the primary capabilities' command patterns.
    $patterns = @($Expect.PSObject.Properties['Commands'] ? $Expect.Commands : @())
    if (-not $patterns -and $caps) {
        $patterns = @($caps.Primary | ForEach-Object { $_ } | Select-Object -Unique |
                Where-Object { $Map.Capabilities.Contains($_) } | ForEach-Object { $Map.Capabilities[$_].Commands })
    }
    $commandHit = $null
    if ($patterns -and $null -ne $WinappCommands) {
        $commandHit = [bool]@($WinappCommands | Where-Object { $cmd = $_; @($patterns | Where-Object { $cmd -match $_ }) }).Count
    }
    $overBudget = $null
    if ($caps -and $null -ne $caps.BudgetTokens -and $null -ne $SkillContextTokens) { $overBudget = [int64]$SkillContextTokens -gt [int64]$caps.BudgetTokens }

    $r | Add-Member -NotePropertyName CommandHit -NotePropertyValue $commandHit
    # Named the right command, but no primary skill loaded: a routing miss the agent partly covered.
    $r | Add-Member -NotePropertyName CommandWithoutSkill -NotePropertyValue ($commandHit -eq $true -and $r.Status -in 'fail', 'partial')
    $r | Add-Member -NotePropertyName OverBudget -NotePropertyValue $overBudget
    if ($overBudget) { $r.Notes = @($r.Notes) + "skill context ~$SkillContextTokens tokens over budget $($caps.BudgetTokens)" }
    return $r
}

function Get-Median {
    param([AllowEmptyCollection()][double[]]$Values)
    $v = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    $mid = [int][Math]::Floor($v.Count / 2)
    if ($v.Count % 2) { return $v[$mid] }
    return ($v[$mid - 1] + $v[$mid]) / 2
}

function Format-Count {
    param($Value)
    if ($null -eq $Value) { return 'n/a' }
    if ($Value -ge 1000000) { return ('{0:N2}M' -f ($Value / 1000000)) }
    if ($Value -ge 1000) { return ('{0:N1}k' -f ($Value / 1000)) }
    return ('{0:N0}' -f $Value)
}

function Get-RecordValue {
    # Property value of a recorded run, or $null when an older run does not have the field.
    param($Record, [string]$Name)
    $p = $Record.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Get-RepeatStats {
    # Totals of repeated skill deliveries over runs that measured them (older runs did not).
    param([AllowEmptyCollection()][object[]]$Runs)
    $measured = @($Runs | Where-Object { $null -ne (Get-RecordValue $_ 'skillRepeatDeliveries') })
    [pscustomobject]@{
        Measured   = $measured.Count
        Runs       = @($measured | Where-Object { $_.skillRepeatDeliveries -gt 0 }).Count
        Deliveries = [int](($measured | ForEach-Object { $_.skillRepeatDeliveries } | Measure-Object -Sum).Sum)
        Tokens     = [int64](($measured | ForEach-Object { Get-RecordValue $_ 'skillRepeatContextTokensApprox' } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum)
    }
}

function Get-StatusStats {
    # Pass / partial / fail / n/a counts and the pass rate over scored (pass, partial, fail) runs.
    param([AllowEmptyCollection()][object[]]$Runs)
    $pass = @($Runs | Where-Object status -eq 'pass').Count
    $partial = @($Runs | Where-Object status -eq 'partial').Count
    $fail = @($Runs | Where-Object status -eq 'fail').Count
    $scored = $pass + $partial + $fail
    $rate = if ($scored) { '{0:N0}%' -f (100 * $pass / $scored) } else { 'n/a' }
    [pscustomobject]@{
        Pass = $pass; Partial = $partial; Fail = $fail; Scored = $scored; Rate = $rate
        NA = @($Runs | Where-Object status -eq 'n/a').Count
        Text = "$pass/$scored$(if ($scored) { " ($rate)" }), $partial partial"
    }
}

function Write-BenchmarkSummary {
    # Builds summary.md from runs.jsonl, reading one line at a time.
    param(
        [Parameter(Mandatory)][string]$RunsPath,
        [Parameter(Mandatory)][string]$SummaryPath,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Header,
        [string[]]$ScenarioOrder = @()
    )

    $rows = [System.Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $RunsPath) {
        foreach ($line in [System.IO.File]::ReadLines($RunsPath)) {
            if ($line.Trim()) { $rows.Add(($line | ConvertFrom-Json)) }
        }
    }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Agent plugin benchmark')
    [void]$sb.AppendLine()
    foreach ($k in $Header.Keys) { [void]$sb.AppendLine("- **$k**: $($Header[$k])") }
    [void]$sb.AppendLine()

    $statusCounts = $rows | Group-Object status | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Count)" }
    [void]$sb.AppendLine("## Totals")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- Runs: $($rows.Count) ($($statusCounts -join ', '))")
    # Routing results leave out the no-plugin control and prompts that name the command.
    $routing = @($rows | Where-Object { $_.configuration -ne 'none' -and (Get-RecordValue $_ 'cohort') -ne 'explicit-command' })
    $naRows = @($rows | Where-Object status -eq 'n/a')
    $st = Get-StatusStats $routing
    [void]$sb.AppendLine("- Pass rate: $($st.Text); $(@($routing | Where-Object status -eq 'n/a').Count) n/a runs excluded (nothing in the expectation applies to the installed skills). Leaves out the ``none`` control and explicit-command scenarios")
    $explicit = @($rows | Where-Object { $_.configuration -ne 'none' -and (Get-RecordValue $_ 'cohort') -eq 'explicit-command' })
    if ($explicit) { [void]$sb.AppendLine("- Explicit-command pass rate: $((Get-StatusStats $explicit).Text)") }
    $missed = @($rows | Where-Object { $_.configuration -ne 'none' -and $_.status -in 'fail', 'partial' })
    $cmdMiss = @($missed | Where-Object { (Get-RecordValue $_ 'commandWithoutSkill') -eq $true })
    if (@($rows | Where-Object { $null -ne (Get-RecordValue $_ 'commandHit') })) {
        [void]$sb.AppendLine("- Right winapp command named without loading a primary skill: $($cmdMiss.Count) of $($missed.Count) fail/partial runs")
    }
    $budget = @($rows | Where-Object { $null -ne (Get-RecordValue $_ 'overBudget') })
    if ($budget) { [void]$sb.AppendLine("- Over the scenario's skill-context budget: $(@($budget | Where-Object overBudget).Count) of $($budget.Count) runs with a budget") }
    $tokIn = ($rows | Where-Object { $_.tokens } | ForEach-Object { $_.tokens.input } | Measure-Object -Sum).Sum
    $tokOut = ($rows | Where-Object { $_.tokens } | ForEach-Object { $_.tokens.output } | Measure-Object -Sum).Sum
    $credits = ($rows | Where-Object { $null -ne $_.aiCredits } | ForEach-Object { $_.aiCredits } | Measure-Object -Sum).Sum
    [void]$sb.AppendLine("- Tokens: $(Format-Count $tokIn) input, $(Format-Count $tokOut) output; AI credits: $(if ($null -ne $credits) { '{0:N1}' -f $credits } else { 'n/a' })")
    $ctx = @($rows | Where-Object { $null -ne $_.skillContextTokensApprox })
    $ctxSum = ($ctx | ForEach-Object { $_.skillContextTokensApprox } | Measure-Object -Sum).Sum
    [void]$sb.AppendLine("- Skill context delivered: ~$(Format-Count $ctxSum) tokens (approximate, characters / 4; $($ctx.Count) of $($rows.Count) runs measured)")
    $rep = Get-RepeatStats $rows
    if ($rep.Measured) {
        [void]$sb.AppendLine("- Repeated skill deliveries: $($rep.Deliveries) in $($rep.Runs) of $($rep.Measured) measured runs (~$(Format-Count $rep.Tokens) extra skill-context tokens)")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Input tokens count the full prompt on every model turn, including cached tokens, so they are dominated by')
    [void]$sb.AppendLine("Copilot's own system prompt, tool definitions, and conversation. The skill context column shows what the")
    [void]$sb.AppendLine('loaded skills added; it is an estimate, not a tokenizer count.')
    [void]$sb.AppendLine()

    $grouped = @($rows | Where-Object { Get-RecordValue $_ 'cohort' })
    $plugged = @($grouped | Where-Object configuration -ne 'none')
    if ($plugged) {
        [void]$sb.AppendLine('## By set and cohort')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('Pass rate is pass / (pass + partial + fail). The `none` control is reported separately below.')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Set | Cohort | Model | Runs | Pass | Partial | Fail | n/a | Pass rate | Right command, no skill |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|')
        foreach ($g in ($plugged | Group-Object set, cohort, model | Sort-Object { $_.Group[0].set }, { $_.Group[0].cohort }, { $_.Group[0].model })) {
            $r0 = $g.Group[0]
            $s = Get-StatusStats $g.Group
            $cmd = @($g.Group | Where-Object { (Get-RecordValue $_ 'commandWithoutSkill') -eq $true }).Count
            [void]$sb.AppendLine("| $($r0.set) | $($r0.cohort) | $($r0.model) | $($g.Count) | $($s.Pass) | $($s.Partial) | $($s.Fail) | $($s.NA) | $($s.Rate) | $cmd |")
        }
        [void]$sb.AppendLine()
    }
    $control = @($grouped | Where-Object configuration -eq 'none')
    if ($control) {
        [void]$sb.AppendLine('## No-plugin control')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('What models do with no plugin installed. Not part of any pass rate.')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Set | Cohort | Model | Runs | Named any winapp command | Named the right command | Median AI credits |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
        foreach ($g in ($control | Group-Object set, cohort, model | Sort-Object { $_.Group[0].set }, { $_.Group[0].cohort }, { $_.Group[0].model })) {
            $r0 = $g.Group[0]
            $any = @($g.Group | Where-Object { @(Get-RecordValue $_ 'winappCommands').Count -gt 0 }).Count
            $withCheck = @($g.Group | Where-Object { $null -ne (Get-RecordValue $_ 'commandHit') })
            $right = if ($withCheck) { "$(@($withCheck | Where-Object commandHit).Count)/$($withCheck.Count)" } else { 'n/a' }
            $medCredits = Get-Median @($g.Group | Where-Object { $null -ne $_.aiCredits } | ForEach-Object { [double]$_.aiCredits })
            [void]$sb.AppendLine("| $($r0.set) | $($r0.cohort) | $($r0.model) | $($g.Count) | $any | $right | $(if ($null -ne $medCredits) { '{0:N1}' -f $medCredits } else { 'n/a' }) |")
        }
        [void]$sb.AppendLine()
    }

    if ($naRows) {
        [void]$sb.AppendLine('## Not applicable')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('Nothing in these scenarios'' expectations applies to the installed skills, so these runs are left out of pass rates.')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Scenario | Configuration | Runs |')
        [void]$sb.AppendLine('|---|---|---|')
        foreach ($g in ($naRows | Group-Object scenario, configuration)) {
            [void]$sb.AppendLine("| $($g.Group[0].scenario) | $($g.Group[0].configuration) | $($g.Count) |")
        }
        [void]$sb.AppendLine()
    }

    $order = @($ScenarioOrder) + @($rows.scenario | Select-Object -Unique | Where-Object { $_ -notin $ScenarioOrder })
    foreach ($scenario in $order) {
        $sr = @($rows | Where-Object scenario -eq $scenario)
        if (-not $sr) { continue }
        [void]$sb.AppendLine("## $scenario")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Configuration | Model | Pass | Skills loaded (most common, freq) | Median input (incl. cached) | Median skill context (~tokens, approx.) | Median output | Median cache read | Median duration | Repeated deliveries (~extra tokens) |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|')
        foreach ($g in ($sr | Group-Object configuration, model)) {
            $runs = @($g.Group)
            $first = $runs[0]
            $passed = @($runs | Where-Object status -eq 'pass').Count
            $na = @($runs | Where-Object status -eq 'n/a').Count
            $other = $runs | Where-Object { $_.status -notin 'pass', 'fail' } | Group-Object status | ForEach-Object { "$($_.Count) $($_.Name)" }
            $passText = if ($na -eq $runs.Count) { "n/a ($na)" } else {
                "$passed/$($runs.Count - $na)" + $(if ($other) { " ($($other -join ', '))" } else { '' })
            }
            $evaluated = @($runs | Where-Object { $_.status -in 'pass', 'fail', 'n/a' })
            $skillText = 'n/a'
            if ($evaluated) {
                $top = $evaluated | ForEach-Object { if ($_.skillsLoaded) { ($_.skillsLoaded -join ', ') } else { '(none)' } } |
                    Group-Object | Sort-Object Count -Descending | Select-Object -First 1
                $skillText = "$($top.Name) ($($top.Count)/$($evaluated.Count))"
            }
            $withTokens = @($runs | Where-Object { $_.tokens })
            $medIn = Get-Median @($withTokens | ForEach-Object { [double]$_.tokens.input })
            $medOut = Get-Median @($withTokens | ForEach-Object { [double]$_.tokens.output })
            $medCache = Get-Median @($withTokens | ForEach-Object { [double]$_.tokens.cacheRead })
            $medCtx = Get-Median @($runs | Where-Object { $null -ne $_.skillContextTokensApprox } | ForEach-Object { [double]$_.skillContextTokensApprox })
            $ctxText = if ($null -ne $medCtx) { "~$(Format-Count $medCtx)" } else { 'n/a' }
            $medDur = Get-Median @($runs | Where-Object { $null -ne $_.durationMs } | ForEach-Object { [double]$_.durationMs })
            $durText = if ($null -ne $medDur) { '{0:N0}s' -f ($medDur / 1000) } else { 'n/a' }
            $cellRep = Get-RepeatStats $runs
            $repText = if (-not $cellRep.Measured) { 'n/a' } elseif ($cellRep.Deliveries) { "$($cellRep.Deliveries) (~$(Format-Count $cellRep.Tokens))" } else { '0' }
            [void]$sb.AppendLine("| $($first.configuration) | $($first.model) | $passText | $skillText | $(Format-Count $medIn) | $ctxText | $(Format-Count $medOut) | $(Format-Count $medCache) | $durText | $repText |")
        }
        $failed = @($sr | Where-Object { $_.status -ne 'pass' -and $_.reason })
        if ($failed) {
            [void]$sb.AppendLine()
            foreach ($f in $failed) { [void]$sb.AppendLine("- $($f.configuration) / $($f.model) #$($f.iteration): **$($f.status)** - $($f.reason)") }
        }
        [void]$sb.AppendLine()
    }

    $text = $sb.ToString()
    $userHome = [Environment]::GetFolderPath('UserProfile')
    if ($userHome) { $text = $text.Replace($userHome, '~') }
    Set-Content -LiteralPath $SummaryPath -Value $text -Encoding utf8NoBOM
}

function Add-EvaluationFields {
    # Copies scenario grouping fields and, when given, evaluation signals onto a run record.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Record, [Parameter(Mandatory)]$Scenario, $Evaluation)
    $Record['set'] = Get-RecordValue $Scenario 'Set'
    $Record['cohort'] = Get-RecordValue $Scenario 'Cohort'
    $Record['baseScenario'] = Get-RecordValue $Scenario 'BaseId'
    $Record['variant'] = Get-RecordValue $Scenario 'Variant'
    if ($Evaluation) {
        $Record['capabilitiesLoaded'] = $Evaluation.CapabilitiesLoaded
        $Record['commandHit'] = $Evaluation.CommandHit
        $Record['commandWithoutSkill'] = $Evaluation.CommandWithoutSkill
        $Record['overBudget'] = $Evaluation.OverBudget
    }
}

function Invoke-Rescore {
    # Re-evaluates recorded pass/fail runs against the current scenario expectations, without model
    # calls. Writes runs.rescored.jsonl and summary.rescored.md next to the originals, which stay untouched.
    param(
        [Parameter(Mandatory)][string]$ResultsDir,
        [Parameter(Mandatory)][object[]]$Scenarios
    )

    $runsPath = Join-Path $ResultsDir 'runs.jsonl'
    if (-not (Test-Path -LiteralPath $runsPath)) { throw "No runs.jsonl in $ResultsDir" }
    $outRuns = Join-Path $ResultsDir 'runs.rescored.jsonl'
    $byId = @{}
    foreach ($s in $Scenarios) { $byId[$s.Id] = $s }

    $changed = 0
    $total = 0
    $transitions = [ordered]@{}
    $writer = [System.IO.StreamWriter]::new($outRuns, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        foreach ($line in [System.IO.File]::ReadLines($runsPath)) {
            if (-not $line.Trim()) { continue }
            $total++
            $rec = $line | ConvertFrom-Json -AsHashtable -Depth 64
            $rec.originalStatus = $rec.status
            if (-not $rec.ContainsKey('expectationNotes')) { $rec.expectationNotes = @() }
            if ($byId[$rec.scenario]) { Add-EvaluationFields -Record $rec -Scenario $byId[$rec.scenario] }
            if ($rec.status -in 'pass', 'fail', 'n/a', 'partial') {
                $s = $byId[$rec.scenario]
                if (-not $s) {
                    $rec.expectationNotes = @($rec.expectationNotes) + 'scenario no longer defined; status not rescored'
                }
                elseif ($rec.ContainsKey('promptHash') -and $rec.promptHash -and $rec.promptHash -ne (Get-ShortHash $s.Prompt)) {
                    $rec.expectationNotes = @($rec.expectationNotes) + 'prompt changed since this run; status not rescored'
                }
                else {
                    $installed = @(if ($rec.preflight) { $rec.preflight.expectedSkills })
                    $eval = Test-ScenarioExpectations -Expect $s.Expect -LoadedSkills @($rec.skillsLoaded) -InstalledSkills $installed `
                        -WinappCommands ($rec.ContainsKey('winappCommands') ? $rec.winappCommands : $null) `
                        -SkillContextTokens ($rec.ContainsKey('skillContextTokensApprox') ? $rec.skillContextTokensApprox : $null)
                    $rec.status = $eval.Status
                    $rec.reason = $eval.Failures -join '; '
                    $rec.expectationNotes = @($eval.Notes)
                    Add-EvaluationFields -Record $rec -Scenario $s -Evaluation $eval
                    if ($rec.configuration -notin $s.Configurations) { $rec.expectationNotes += 'configuration no longer listed for this scenario' }
                }
            }
            if ($rec.status -ne $rec.originalStatus) {
                $changed++
                $k = "$($rec.originalStatus) -> $($rec.status)"
                $transitions[$k] = 1 + ($transitions.Contains($k) ? $transitions[$k] : 0)
            }
            $writer.WriteLine(($rec | ConvertTo-Json -Depth 16 -Compress))
        }
    }
    finally { $writer.Dispose() }

    $header = [ordered]@{ 'Rescored' = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'); 'Source' = $runsPath }
    $infoPath = Join-Path $ResultsDir 'run-info.json'
    if (Test-Path -LiteralPath $infoPath) {
        $info = Get-Content -Raw $infoPath | ConvertFrom-Json
        $header['Copilot CLI'] = $info.copilotVersion
        $header['Models'] = $info.models -join ', '
        $header['Iterations'] = $info.iterations
        $header['Plugins'] = ($info.plugins | ForEach-Object {
                $sha = if ($_.sha) { " @ $($_.sha.Substring(0, 12))" } else { '' }
                "$($_.name) v$($_.version) from $($_.source)$sha"
            }) -join '; '
    }
    $header['Status changes'] = "$changed of $total runs" + $(if ($transitions.Count) { " ($(@($transitions.Keys | ForEach-Object { "$_ $($transitions[$_])" }) -join ', '))" } else { '' })
    $summary = Join-Path $ResultsDir 'summary.rescored.md'
    Write-BenchmarkSummary -RunsPath $outRuns -SummaryPath $summary -Header $header -ScenarioOrder @($Scenarios.Id)
    return [pscustomobject]@{ Runs = $total; Changed = $changed; Transitions = $transitions; RunsPath = $outRuns; SummaryPath = $summary }
}

function Read-ComparisonRuns {
    # Loads runs.jsonl from result folders without modifying them. Scored runs are re-evaluated in
    # memory against the current scenarios, so both sides of a comparison use the same expectations.
    param([Parameter(Mandatory)][string[]]$ResultsDirs, [Parameter(Mandatory)][object[]]$Scenarios)
    $byId = @{}
    foreach ($s in $Scenarios) { $byId[$s.Id] = $s }
    foreach ($dir in $ResultsDirs) {
        $path = Join-Path $dir 'runs.jsonl'
        if (-not (Test-Path -LiteralPath $path)) { throw "No runs.jsonl in $dir" }
        foreach ($line in [System.IO.File]::ReadLines($path)) {
            if (-not $line.Trim()) { continue }
            $rec = $line | ConvertFrom-Json -Depth 64
            $status = $rec.status
            $expectedInstalled = $null
            $s = $byId[$rec.scenario]
            if ($s -and $status -in 'pass', 'fail', 'n/a', 'partial') {
                $pre = Get-RecordValue $rec 'preflight'
                $installed = @(if ($pre) { $pre.expectedSkills })
                $eval = Test-ScenarioExpectations -Expect $s.Expect -LoadedSkills @($rec.skillsLoaded) -InstalledSkills $installed
                $status = $eval.Status
                $expectedInstalled = $eval.ExpectedInstalled
            }
            $tokens = Get-RecordValue $rec 'tokens'
            [pscustomobject]@{
                Key          = "$($rec.model)|$($rec.scenario)|$($rec.configuration)"
                Scenario     = $rec.scenario
                Configuration = $rec.configuration
                Model        = $rec.model
                Set          = if ($s) { Get-RecordValue $s 'Set' } else { Get-RecordValue $rec 'set' }
                Cohort       = if ($s) { Get-RecordValue $s 'Cohort' } else { Get-RecordValue $rec 'cohort' }
                Agent        = Get-RecordValue $rec 'agent'
                Status       = $status
                ExpectedInstalled = $expectedInstalled
                Ctx          = Get-RecordValue $rec 'skillContextTokensApprox'
                Input        = if ($tokens) { $tokens.input } else { $null }
                Credits      = Get-RecordValue $rec 'aiCredits'
                Repeats      = Get-RecordValue $rec 'skillRepeatDeliveries'
                RepeatTokens = Get-RecordValue $rec 'skillRepeatContextTokensApprox'
            }
        }
    }
}

function Get-ComparisonStats {
    param([AllowEmptyCollection()][object[]]$Runs)
    $done = @($Runs | Where-Object { $_.Status -in 'pass', 'partial', 'fail', 'n/a' })
    $mean = {
        param($values)
        $v = @($values | Where-Object { $null -ne $_ })
        if ($v.Count) { ($v | Measure-Object -Average).Average } else { $null }
    }
    $rep = @($done | Where-Object { $null -ne $_.Repeats })
    [pscustomobject]@{
        Done     = $done.Count
        Pass     = @($done | Where-Object Status -eq 'pass').Count
        Partial  = @($done | Where-Object Status -eq 'partial').Count
        Scored   = @($done | Where-Object Status -in 'pass', 'partial', 'fail').Count
        NA       = @($done | Where-Object Status -eq 'n/a').Count
        # Whether an expected skill was installed; differs when a candidate adds or removes one.
        Checks   = @($done | Where-Object Status -in 'pass', 'partial', 'fail' | ForEach-Object { $_.ExpectedInstalled } | Select-Object -Unique | Sort-Object) -join ','
        Ctx      = & $mean @($done | ForEach-Object { $_.Ctx })
        Input    = & $mean @($done | ForEach-Object { $_.Input })
        Credits  = & $mean @($done | ForEach-Object { $_.Credits })
        Repeats  = if ($rep) { [int](($rep | ForEach-Object { $_.Repeats } | Measure-Object -Sum).Sum) } else { $null }
    }
}

function Get-ComparisonReport {
    # Markdown comparison of baseline and candidate result folders over identical
    # (model, scenario, configuration) cells.
    param(
        [Parameter(Mandatory)][string[]]$Baseline,
        [Parameter(Mandatory)][string[]]$Candidate,
        [Parameter(Mandatory)][object[]]$Scenarios,
        [string[]]$ScenarioFilter = @(),
        [string[]]$ConfigurationFilter = @(),
        [string[]]$ModelFilter = @()
    )

    $filter = {
        param($runs)
        @($runs | Where-Object {
                (-not $ScenarioFilter -or $_.Scenario -in $ScenarioFilter) -and
                (-not $ConfigurationFilter -or $_.Configuration -in $ConfigurationFilter) -and
                (-not $ModelFilter -or $_.Model -in $ModelFilter)
            })
    }
    $b = & $filter (Read-ComparisonRuns -ResultsDirs $Baseline -Scenarios $Scenarios)
    $c = & $filter (Read-ComparisonRuns -ResultsDirs $Candidate -Scenarios $Scenarios)
    $bCells = @{}; foreach ($g in ($b | Group-Object Key)) { $bCells[$g.Name] = @($g.Group) }
    $cCells = @{}; foreach ($g in ($c | Group-Object Key)) { $cCells[$g.Name] = @($g.Group) }
    $shared = @($cCells.Keys | Where-Object { $bCells.ContainsKey($_) } | Sort-Object)

    $ratio = {
        param($old, $new)
        if ($null -eq $old -or $null -eq $new) { return 'n/a' }
        if ($old -eq 0) { return $(if ($new -eq 0) { '=' } else { 'new' }) }
        $d = [Math]::Round(100 * ($new - $old) / $old)
        if ($d -eq 0) { '=' } else { '{0:+0;-0}%' -f $d }
    }
    $num = { param($v, [switch]$Credits) if ($null -eq $v) { 'n/a' } elseif ($Credits) { '{0:N1}' -f $v } else { Format-Count $v } }
    $passText = { param($s) if ($s.Scored) { "$($s.Pass)/$($s.Scored)" } elseif ($s.NA) { 'n/a' } else { '-' } }
    $passDelta = {
        param($bs, $cs)
        if (-not $bs.Scored -or -not $cs.Scored) { return 'n/a' }
        if ($bs.Checks -ne $cs.Checks) { return 'check differs' }
        $d = [Math]::Round(100 * ($cs.Pass / $cs.Scored - $bs.Pass / $bs.Scored))
        if ($d -eq 0) { '=' } else { '{0:+0;-0} pp' -f $d }
    }
    $repText = { param($bs, $cs) if ($null -eq $bs.Repeats -and $null -eq $cs.Repeats) { 'n/a' } else { "$($bs.Repeats ?? '-') → $($cs.Repeats ?? '-')" } }
    $row = {
        param($label, $bs, $cs)
        "| $label | $(& $passText $bs) → $(& $passText $cs) | $(& $passDelta $bs $cs) | " +
        "$(& $num $bs.Ctx) → $(& $num $cs.Ctx) | $(& $ratio $bs.Ctx $cs.Ctx) | " +
        "$(& $num $bs.Input) → $(& $num $cs.Input) | $(& $ratio $bs.Input $cs.Input) | " +
        "$(& $num $bs.Credits -Credits) → $(& $num $cs.Credits -Credits) | $(& $ratio $bs.Credits $cs.Credits) | $(& $repText $bs $cs) |"
    }
    $source = {
        param($dirs, $runs)
        $runs = @($runs | Where-Object { $_ })
        $agents = @($runs | ForEach-Object { $_.Agent } | Where-Object { $_ } | Select-Object -Unique)
        $parents = @($dirs | ForEach-Object { Split-Path $_ -Parent } | Select-Object -Unique)
        $names = if ($parents.Count -eq 1) { "$(($dirs | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') in $($parents[0])" } else { $dirs -join ', ' }
        "$names ($($runs.Count) runs$(if ($agents) { "; agent $($agents -join ', ')" }))"
    }
    $header = '| Pass (base → cand) | Δ pass | Skill context/run (~tokens) | Δ | Input/run | Δ | AI credits/run | Δ | Repeated deliveries |'
    $rule = '|---|---|---|---|---|---|---|---|---|'

    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add('# Benchmark comparison')
    $md.Add('')
    $md.Add("- **Baseline**: $(& $source $Baseline $b)")
    $md.Add("- **Candidate**: $(& $source $Candidate $c)")
    $md.Add("- **Compared cells**: $($shared.Count) (model, scenario, configuration) present on both sides; " +
        "$(@($bCells.Keys | Where-Object { -not $cCells.ContainsKey($_) }).Count) baseline-only and " +
        "$(@($cCells.Keys | Where-Object { -not $bCells.ContainsKey($_) }).Count) candidate-only cells skipped")
    $md.Add('')
    $md.Add('Pass/fail is re-evaluated against the current scenario expectations, so both sides use the same rules.')
    $md.Add('Pass rates leave out `n/a` runs (nothing in the expectation applies). A cell shows `check differs` when an')
    $md.Add('expected skill is installed on one side only, for example when a candidate adds a skill to a plugin; such cells')
    $md.Add('and `n/a` cells are left out of pooled pass rates. Means cover completed runs; timeouts and harness errors are')
    $md.Add('left out. Skill context is characters / 4. Repeated deliveries are only measured by runs recorded with this')
    $md.Add('version of the harness.')

    if (-not $shared) {
        $md.Add('')
        $md.Add('No cells are present on both sides.')
        return ($md -join "`n")
    }

    $pooled = {
        param($label, $keys)
        $bs = Get-ComparisonStats @($keys | ForEach-Object { $bCells[$_] })
        $cs = Get-ComparisonStats @($keys | ForEach-Object { $cCells[$_] })
        # Pooled pass only over cells that are applicable, and check the same thing, on both sides.
        $both = @($keys | Where-Object {
                $x = Get-ComparisonStats $bCells[$_]
                $y = Get-ComparisonStats $cCells[$_]
                $x.Scored -and $y.Scored -and $x.Checks -eq $y.Checks
            })
        $bp = Get-ComparisonStats @($both | ForEach-Object { $bCells[$_] })
        $cp = Get-ComparisonStats @($both | ForEach-Object { $cCells[$_] })
        foreach ($k in 'Pass', 'Partial', 'Scored', 'NA', 'Checks') { $bs.$k = $bp.$k; $cs.$k = $cp.$k }
        & $row "$label | $($keys.Count)" $bs $cs
    }
    # The no-plugin control and prompts that name the command are not routing results.
    $plugged = @($shared | Where-Object { $bCells[$_][0].Configuration -ne 'none' })
    $routing = @($plugged | Where-Object { $bCells[$_][0].Cohort -ne 'explicit-command' })

    $md.Add('')
    $md.Add('## By model')
    $md.Add('')
    $md.Add('Leaves out the `none` control and explicit-command scenarios.')
    $md.Add('')
    $md.Add("| Model | Cells $header")
    $md.Add("|---|---$rule")
    foreach ($m in ($routing | ForEach-Object { $bCells[$_][0].Model } | Select-Object -Unique | Sort-Object)) {
        $md.Add((& $pooled $m @($routing | Where-Object { $bCells[$_][0].Model -eq $m })))
    }

    $groups = @($plugged | Group-Object { "$($bCells[$_][0].Set) | $($bCells[$_][0].Cohort)" } | Sort-Object Name)
    if ($groups.Count -gt 1 -or ($groups -and $groups[0].Name -ne ' | ')) {
        $md.Add('')
        $md.Add('## By set and cohort')
        $md.Add('')
        $md.Add("| Set | Cohort | Cells $header")
        $md.Add("|---|---|---$rule")
        foreach ($g in $groups) { $md.Add((& $pooled $g.Name @($g.Group))) }
    }

    $md.Add('')
    $md.Add('## By cell')
    $md.Add('')
    $md.Add("| Model | Scenario | Configuration $header")
    $md.Add("|---|---|---$rule")
    foreach ($k in $shared) {
        $r0 = $bCells[$k][0]
        $md.Add((& $row "$($r0.Model) | $($r0.Scenario) | $($r0.Configuration)" (Get-ComparisonStats $bCells[$k]) (Get-ComparisonStats $cCells[$k])))
    }
    return ($md -join "`n")
}

Export-ModuleMember -Function Get-ScenarioDefinitions, Get-PluginSkillNames, Get-ConfigurationPlugins, New-ChildEnvironment,
Invoke-LoggedProcess, Get-FileTail, Read-SessionEvents, Get-DirectorySnapshot, Compare-DirectorySnapshot, Test-Expectations,
Get-Median, Write-BenchmarkSummary, Invoke-Rescore, Split-ListArgument, Get-WinappCommands, Get-BareSkillName, Get-ComparisonReport,
Get-ShortHash, Get-SkillSetHash, Read-CapabilityMap, Get-CapabilityMap, Set-CapabilityMap, Resolve-SkillCapabilities,
Test-CapabilityExpectations, Test-ScenarioExpectations, Add-EvaluationFields, Get-StatusStats
