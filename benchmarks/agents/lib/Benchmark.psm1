Set-StrictMode -Version Latest

$script:ValidConfigurations = @('none', 'winapp', 'winui', 'both')

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
        $fixture = $null
        if ($s.ContainsKey('fixture') -and $s.fixture) {
            $fixture = Join-Path $dir $s.fixture
            if (-not (Test-Path -LiteralPath $fixture -PathType Container)) { throw "Scenario '$($s.id)' fixture folder not found: $fixture" }
        }
        $expect = $s.expect
        [pscustomobject]@{
            Id             = $s.id
            Description    = $s.description
            Prompt         = $s.prompt
            Configurations = @($s.configurations)
            FixturePath    = $fixture
            TimeoutMinutes = if ($s.ContainsKey('timeoutMinutes')) { $s.timeoutMinutes } else { $null }
            Expect         = [pscustomobject]@{
                SkillsAny    = @(if ($expect.ContainsKey('skillsAny')) { $expect.skillsAny })
                SkillsAll    = @(if ($expect.ContainsKey('skillsAll')) { $expect.skillsAll })
                SkillsForbid = @(if ($expect.ContainsKey('skillsForbid')) { $expect.skillsForbid })
                MaxSkills    = if ($expect.ContainsKey('maxSkills')) { $expect.maxSkills } else { $null }
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
        $unknown = @()
        foreach ($d in $deliveries) {
            $body = $d.body ?? ($d.name -and $skillContentLength.ContainsKey($d.name) ? $skillContentLength[$d.name] : $null)
            if ($null -eq $body) { $unknown += $d.name; continue }
            $chars += $d.wrapper + $body
        }
        if ($unknown) {
            $r.skillContextReason = "body length unknown for: $(($unknown | Select-Object -Unique) -join ', ')"
        }
        else {
            $r.skillContextChars = $chars
            $r.skillContextTokensApprox = [int64][Math]::Round($chars / 4)
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

    return [pscustomobject]@{
        Passed   = $failures.Count -eq 0
        Failures = @($failures)
        Notes    = @($notes)
    }
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
    $tokIn = ($rows | Where-Object { $_.tokens } | ForEach-Object { $_.tokens.input } | Measure-Object -Sum).Sum
    $tokOut = ($rows | Where-Object { $_.tokens } | ForEach-Object { $_.tokens.output } | Measure-Object -Sum).Sum
    $credits = ($rows | Where-Object { $null -ne $_.aiCredits } | ForEach-Object { $_.aiCredits } | Measure-Object -Sum).Sum
    [void]$sb.AppendLine("- Tokens: $(Format-Count $tokIn) input, $(Format-Count $tokOut) output; AI credits: $(if ($null -ne $credits) { '{0:N1}' -f $credits } else { 'n/a' })")
    $ctx = @($rows | Where-Object { $null -ne $_.skillContextTokensApprox })
    $ctxSum = ($ctx | ForEach-Object { $_.skillContextTokensApprox } | Measure-Object -Sum).Sum
    [void]$sb.AppendLine("- Skill context delivered: ~$(Format-Count $ctxSum) tokens (approximate, characters / 4; $($ctx.Count) of $($rows.Count) runs measured)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Input tokens count the full prompt on every model turn, including cached tokens, so they are dominated by')
    [void]$sb.AppendLine("Copilot's own system prompt, tool definitions, and conversation. The skill context column shows what the")
    [void]$sb.AppendLine('loaded skills added once; it is an estimate, not a tokenizer count.')
    [void]$sb.AppendLine()

    $order = @($ScenarioOrder) + @($rows.scenario | Select-Object -Unique | Where-Object { $_ -notin $ScenarioOrder })
    foreach ($scenario in $order) {
        $sr = @($rows | Where-Object scenario -eq $scenario)
        if (-not $sr) { continue }
        [void]$sb.AppendLine("## $scenario")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| Configuration | Model | Pass | Skills loaded (most common, freq) | Median input (incl. cached) | Median skill context (~tokens, approx.) | Median output | Median cache read | Median duration |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|')
        foreach ($g in ($sr | Group-Object configuration, model)) {
            $runs = @($g.Group)
            $first = $runs[0]
            $passed = @($runs | Where-Object status -eq 'pass').Count
            $other = $runs | Where-Object { $_.status -notin 'pass', 'fail' } | Group-Object status | ForEach-Object { "$($_.Count) $($_.Name)" }
            $passText = "$passed/$($runs.Count)" + $(if ($other) { " ($($other -join ', '))" } else { '' })
            $evaluated = @($runs | Where-Object { $_.status -in 'pass', 'fail' })
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
            [void]$sb.AppendLine("| $($first.configuration) | $($first.model) | $passText | $skillText | $(Format-Count $medIn) | $ctxText | $(Format-Count $medOut) | $(Format-Count $medCache) | $durText |")
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
    $writer = [System.IO.StreamWriter]::new($outRuns, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        foreach ($line in [System.IO.File]::ReadLines($runsPath)) {
            if (-not $line.Trim()) { continue }
            $total++
            $rec = $line | ConvertFrom-Json -AsHashtable -Depth 64
            $rec.originalStatus = $rec.status
            if (-not $rec.ContainsKey('expectationNotes')) { $rec.expectationNotes = @() }
            if ($rec.status -in 'pass', 'fail') {
                $s = $byId[$rec.scenario]
                if (-not $s) {
                    $rec.expectationNotes = @($rec.expectationNotes) + 'scenario no longer defined; status not rescored'
                }
                else {
                    $installed = @(if ($rec.preflight) { $rec.preflight.expectedSkills })
                    $eval = Test-Expectations -Expect $s.Expect -LoadedSkills @($rec.skillsLoaded) -InstalledSkills $installed
                    $rec.status = $eval.Passed ? 'pass' : 'fail'
                    $rec.reason = $eval.Failures -join '; '
                    $rec.expectationNotes = @($eval.Notes)
                    if ($rec.configuration -notin $s.Configurations) { $rec.expectationNotes += 'configuration no longer listed for this scenario' }
                }
            }
            if ($rec.status -ne $rec.originalStatus) { $changed++ }
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
    $header['Status changes'] = "$changed of $total runs"
    $summary = Join-Path $ResultsDir 'summary.rescored.md'
    Write-BenchmarkSummary -RunsPath $outRuns -SummaryPath $summary -Header $header -ScenarioOrder @($Scenarios.Id)
    return [pscustomobject]@{ Runs = $total; Changed = $changed; RunsPath = $outRuns; SummaryPath = $summary }
}

Export-ModuleMember -Function Get-ScenarioDefinitions, Get-PluginSkillNames, Get-ConfigurationPlugins, New-ChildEnvironment,
Invoke-LoggedProcess, Get-FileTail, Read-SessionEvents, Get-DirectorySnapshot, Compare-DirectorySnapshot, Test-Expectations,
Get-Median, Write-BenchmarkSummary, Invoke-Rescore, Split-ListArgument, Get-WinappCommands, Get-BareSkillName
