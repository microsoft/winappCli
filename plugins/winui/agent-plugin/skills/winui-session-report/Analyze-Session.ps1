#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Analyzes an agentic session (GitHub Copilot CLI or Claude Code) from its
    on-disk transcript and outputs a structured markdown report.

.DESCRIPTION
    Auto-detects the harness from the environment and the on-disk format:
      - Copilot CLI:  ~/.copilot/session-state/<id>/events.jsonl
      - Claude Code:  ~/.claude/projects/<encoded-cwd>/<id>.jsonl

    For Claude Code, subagent transcripts under <id>/subagents/agent-*.jsonl
    are also analyzed and rolled into the parent session's report.

.PARAMETER SessionId
    Session ID (UUID) to analyze. The script searches both harness locations.

.PARAMETER EventsFile
    Direct path to a session transcript file. Format is sniffed from content.

.PARAMETER OutputFile
    Path to write the markdown report. If omitted, writes to stdout.

.PARAMETER Format
    Override harness detection. One of: Copilot, ClaudeCode.

.PARAMETER SkipSubagents
    For Claude Code sessions, do not include subagent activity in the report.

.EXAMPLE
    .\Analyze-Session.ps1
    .\Analyze-Session.ps1 -SessionId "f116c51e-a9d1-4636-b250-1e00c746705e"
    .\Analyze-Session.ps1 -EventsFile .\transcript.jsonl -OutputFile session-report.md
#>
param(
    [string]$SessionId,
    [string]$EventsFile,
    [string]$OutputFile,
    [ValidateSet('Copilot', 'ClaudeCode')]
    [string]$Format,
    [switch]$SkipSubagents
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Harness detection
# -----------------------------------------------------------------------------
function Get-CopilotSessionRoot {
    Join-Path $env:USERPROFILE ".copilot\session-state"
}

function Get-ClaudeProjectsRoot {
    Join-Path $env:USERPROFILE ".claude\projects"
}

function Test-CopilotEnvironment {
    if ($env:COPILOT_AGENT_SESSION_ID -or $env:COPILOT_CLI -or $env:COPILOT_HOME -or $env:COPILOT_MODEL -or $env:COPILOT_GITHUB_TOKEN) { return $true }
    return $false
}

function Test-ClaudeEnvironment {
    if ($env:CLAUDECODE -eq '1') { return $true }
    if ($env:CLAUDE_CODE_ENTRYPOINT) { return $true }
    return $false
}

function Get-EventFormatFromContent {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    $sniffed = 0
    foreach ($line in Get-Content $Path -Encoding UTF8) {
        if (-not $line.Trim()) { continue }
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        $type = $obj.type
        if ($type -in 'user.message', 'assistant.turn_start', 'assistant.message',
                     'tool.execution_start', 'tool.execution_complete',
                     'session.skills_loaded', 'session.tools_updated', 'result') {
            return 'Copilot'
        }
        if ($type -in 'assistant', 'user', 'system', 'attachment',
                     'permission-mode', 'file-history-snapshot', 'queue-operation',
                     'ai-title', 'last-prompt') {
            return 'ClaudeCode'
        }
        if (++$sniffed -ge 10) { break }
    }
    return $null
}

function Find-LatestCopilotSession {
    $root = Get-CopilotSessionRoot
    if (-not (Test-Path $root)) { return $null }
    $latest = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "events.jsonl") } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $latest) { return $null }
    return [pscustomobject]@{
        SessionId = $latest.Name
        Path      = Join-Path $latest.FullName "events.jsonl"
        Modified  = $latest.LastWriteTime
    }
}

function Get-ClaudeSessionCwd {
    param([string]$Path)
    foreach ($line in Get-Content $Path -Encoding UTF8 -TotalCount 50) {
        if (-not $line.Trim()) { continue }
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.cwd) { return [string]$obj.cwd }
    }
    return $null
}

function Find-LatestClaudeSession {
    param([string]$PreferCwd)
    $root = Get-ClaudeProjectsRoot
    if (-not (Test-Path $root)) { return $null }

    $candidates = Get-ChildItem $root -Recurse -Filter '*.jsonl' -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Directory.Name -ne 'subagents' -and
            $_.Directory.Parent.Name -eq 'projects'
        } |
        Sort-Object LastWriteTime -Descending

    if (-not $candidates) { return $null }

    if ($PreferCwd) {
        foreach ($c in $candidates) {
            $firstCwd = Get-ClaudeSessionCwd -Path $c.FullName
            if ($firstCwd -and ($firstCwd.TrimEnd('\') -ieq $PreferCwd.TrimEnd('\'))) {
                return [pscustomobject]@{
                    SessionId = [System.IO.Path]::GetFileNameWithoutExtension($c.Name)
                    Path      = $c.FullName
                    Modified  = $c.LastWriteTime
                }
            }
        }
    }

    $top = $candidates | Select-Object -First 1
    return [pscustomobject]@{
        SessionId = [System.IO.Path]::GetFileNameWithoutExtension($top.Name)
        Path      = $top.FullName
        Modified  = $top.LastWriteTime
    }
}

function Find-ClaudeSessionById {
    param([string]$Id)
    $root = Get-ClaudeProjectsRoot
    if (-not (Test-Path $root)) { return $null }
    $hit = Get-ChildItem $root -Recurse -Filter "$Id.jsonl" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -ne 'subagents' } |
        Select-Object -First 1
    if (-not $hit) { return $null }
    return [pscustomobject]@{
        SessionId = $Id
        Path      = $hit.FullName
        Modified  = $hit.LastWriteTime
    }
}

function Find-CopilotSessionById {
    param([string]$Id)
    $p = Join-Path (Get-CopilotSessionRoot) "$Id\events.jsonl"
    if (-not (Test-Path $p)) { return $null }
    return [pscustomobject]@{
        SessionId = $Id
        Path      = $p
        Modified  = (Get-Item $p).LastWriteTime
    }
}

function Write-UnsupportedHarnessError {
    param([string]$ExtraContext)
    $msg = @"
Analyze-Session: could not detect a supported session format.

Looked for:
  - GitHub Copilot CLI:  $((Get-CopilotSessionRoot))\<id>\events.jsonl
  - Claude Code:         $((Get-ClaudeProjectsRoot))\<encoded-cwd>\<id>.jsonl

$ExtraContext

If you're running a different agentic harness (Cursor, Aider, Cline, etc.),
this skill does not currently support it. Please file an issue at
https://github.com/microsoft/win-dev-skills/issues with:
  - The harness name and version
  - A sample session file (redacted)
  - Where the harness stores session transcripts on disk
"@
    Write-Error $msg -ErrorAction Stop
}

# -----------------------------------------------------------------------------
# Tool name normalization
# -----------------------------------------------------------------------------
$script:ToolNameMap = @{
    'Read'             = 'view'
    'Edit'             = 'edit'
    'Write'            = 'create'
    'Glob'             = 'glob'
    'Grep'             = 'grep'
    'Bash'             = 'shell'
    'PowerShell'       = 'powershell'
    'Skill'            = 'skill'
    'Agent'            = 'agent'
    'WebFetch'         = 'web_fetch'
    'WebSearch'        = 'web_search'
    'ToolSearch'       = 'tool_search'
    'Monitor'          = 'monitor'
    'NotebookEdit'     = 'edit'
    'ScheduleWakeup'   = 'schedule_wakeup'
    'TaskCreate'       = 'task_create'
    'TaskUpdate'       = 'task_update'
    'TaskList'         = 'task_list'
    'TaskGet'          = 'task_get'
    'TaskOutput'       = 'task_output'
    'TaskStop'         = 'task_stop'
    'PushNotification' = 'push_notification'
    'AskUserQuestion'  = 'ask_user'
    'EnterPlanMode'    = 'plan_mode'
    'ExitPlanMode'     = 'plan_mode'
    'EnterWorktree'    = 'worktree'
    'ExitWorktree'     = 'worktree'
    'CronCreate'       = 'cron'
    'CronDelete'       = 'cron'
    'CronList'         = 'cron'
}

function Get-NormalizedToolName {
    param([string]$Name)
    if ($script:ToolNameMap.ContainsKey($Name)) { return $script:ToolNameMap[$Name] }
    return $Name.ToLowerInvariant()
}

# -----------------------------------------------------------------------------
# Shared helpers
# -----------------------------------------------------------------------------
function New-NormalizedTurn {
    param([int]$TurnNum, [string]$Timestamp)
    return @{
        TurnNum           = $TurnNum
        Timestamp         = $Timestamp
        Tools             = @()
        OutputTokens      = 0
        CacheReadTokens   = 0
        CacheCreateTokens = 0
        TextSnippets      = @()
        SkillInvocations  = @()
        AgentSpawns       = @()
        Category          = 'other'
    }
}

function Get-ErrorSummary {
    param([string]$ResultText)
    if (-not $ResultText) { return @{ HasError = $false; Summary = @() } }
    $hasErr = $ResultText -match 'error|FAILED|SyntaxError'
    if (-not $hasErr) { return @{ HasError = $false; Summary = @() } }

    $errLines = ($ResultText -split "`n") |
        Where-Object { $_ -match 'error|FAILED|SyntaxError' } |
        Select-Object -First 5
    $errSummary = @()
    foreach ($errLine in $errLines) {
        $cleaned = $errLine.Trim()
        if ($cleaned -match 'error (CS\d+|XLS\d+|XDG\d+|MSB\d+):\s*(.+?)(\s*\[|$)') {
            $errSummary += "$($Matches[1]): $($Matches[2].Trim())"
        } elseif ($cleaned -match 'SyntaxError:\s*(.+)') {
            $errSummary += "SyntaxError: $($Matches[1].Trim())"
        } elseif ($cleaned -match 'error (MSB\d+)') {
            if ($cleaned -match 'error (MSB\d+):.*exited with code (\d+)') {
                $errSummary += "$($Matches[1]): XamlCompiler.exe exited with code $($Matches[2])"
            } else {
                $errSummary += $Matches[1]
            }
        } elseif ($cleaned -match 'BUILD FAILED') {
            $errSummary += "BUILD FAILED"
        } else {
            $errSummary += $cleaned.Substring(0, [Math]::Min($cleaned.Length, 120))
        }
    }
    return @{ HasError = $true; Summary = ($errSummary | Select-Object -Unique) }
}

function Test-NoBuildEnabled {
    param([string]$Command)

    $match = [regex]::Match(
        $Command,
        '(?i)(?:^|\s)--no-build(?:=(?<inline>true|false)|\s+(?<spaced>true|false))?(?=\s|$)'
    )
    if (-not $match.Success) { return $false }

    $value = if ($match.Groups['inline'].Success) {
        $match.Groups['inline'].Value
    } elseif ($match.Groups['spaced'].Success) {
        $match.Groups['spaced'].Value
    } else {
        'true'
    }
    return $value -ieq 'true'
}

function Test-BuildCapableCommand {
    param([string]$Command)

    # Classify transcript text, not today's filesystem. Keep flags scoped to each
    # invocation, and don't mistake arguments passed to the app for CLI options.
    foreach ($segment in [regex]::Split($Command, ';|&&|\|\||\r?\n')) {
        $invocation = ($segment -split '\s+--(?:\s|$)', 2)[0]
        if ($invocation -match '(?:^|\s)(?:--help|-h|-\?)(?=\s|$)') { continue }
        if ($invocation -match '\bmsbuild\b') { return $true }
        if (Test-NoBuildEnabled -Command $invocation) { continue }
        if ($invocation -match '\bdotnet\s+(?:build|publish)\b|BuildAndRun') {
            return $true
        }
        $cli = [regex]::Match($invocation, '(?i)\bwinapp(?:\.exe)?\s+(?<verb>run|pack(?:age)?)\b(?<args>.*)')
        if (-not $cli.Success) { continue }
        $arguments = $cli.Groups['args'].Value.Trim()
        $argumentTokens = @([regex]::Matches($arguments, '(?:"[^"]*"|''[^'']*''|[^\s"''])+') |
            ForEach-Object { $_.Value.Trim([char[]]@('"', "'")) })
        $valueOptions = @('--arch', '--configuration', '-c', '--framework', '-f', '--runtime', '-r',
            '--project', '--on', '--manifest', '--property', '-p', '--output', '-o', '--cert')
        $booleanOptions = @('--no-build', '--no-restore', '--aot', '--detach', '--json', '--no-launch',
            '--no-sign', '--generate-cert', '--install-cert', '--debug-output', '--with-alias', '--clean',
            '--self-contained', '--symbols')
        $inputPath = ''
        $knownOptions = $true
        # Only skip known leading options; unknown options are ambiguous, not
        # evidence that their value is a positional project input.
        for ($index = 0; $index -lt $argumentTokens.Count; $index++) {
            $token = $argumentTokens[$index]
            if (-not $token.StartsWith('-')) { $inputPath = $token; break }
            $option = $token -split '=', 2
            if ($option[0] -in $valueOptions) {
                if ($option.Count -eq 1) {
                    if ($index + 1 -ge $argumentTokens.Count -or $argumentTokens[$index + 1].StartsWith('-')) {
                        $knownOptions = $false; break
                    }
                    $index++
                }
            } elseif ($option[0] -in $booleanOptions) {
                if ($option.Count -eq 2 -and $option[1] -notmatch '^(true|false)$') {
                    $knownOptions = $false; break
                }
                if ($option.Count -eq 1 -and $index + 1 -lt $argumentTokens.Count -and
                    $argumentTokens[$index + 1] -match '^(true|false)$') { $index++ }
            } else {
                $knownOptions = $false; break
            }
        }
        if (-not $knownOptions) { continue }
        if ($cli.Groups['verb'].Value -match '^pack') {
            # Only an explicit .csproj is project packaging. Folder, bundle, and
            # sparse-manifest packaging do not build, even when flags mention a project.
            if ($inputPath -match '\.csproj$') { return $true }
        } elseif (-not $inputPath -or $inputPath -match '^\.[\\/]?$|\.(?:csproj|slnx?|cs)$') {
            # "." / omitted input is the normal project-root workflow. Other
            # directory inputs are ambiguous without historical filesystem state.
            return $true
        }
    }
    return $false
}

function Get-TurnCategory {
    param($Turn)
    $toolNames = $Turn.Tools | ForEach-Object { $_.Name }
    $hasSkill = $Turn.SkillInvocations.Count -gt 0
    $shellCmd = { param($t) ($t.Name -in 'powershell', 'shell') }

    $hasBuild       = $Turn.Tools | Where-Object {
        if (-not (& $shellCmd $_)) { return $false }
        return Test-BuildCapableCommand -Command $_.Args.command
    }
    $hasRun         = $Turn.Tools | Where-Object { (& $shellCmd $_) -and ($_.Args.command -match '\bwinapp(?:\.exe)?\s+run\b|BuildAndRun') }
    $hasGit         = $Turn.Tools | Where-Object { (& $shellCmd $_) -and ($_.Args.command -match '\bgit\b') }
    $hasBuildError  = $Turn.Tools | Where-Object { $_.HasError -and (& $shellCmd $_) }
    $hasScaffold    = $Turn.Tools | Where-Object { $_.Args.command -match 'winapp new|dotnet new|New-Item.*Directory' }
    $isDiagnosing   = $Turn.Tools | Where-Object {
        (& $shellCmd $_) -and ($_.Args.command -match 'XamlCompiler|output\.json|input\.json|-v d\b|-v:d|-verbosity|Select-String.*error|obj\\|temp_output|Remove-Item.*obj|Get-Content.*log|Get-Process')
    }
    $hasCreate = 'create' -in $toolNames
    $hasEdit   = 'edit'   -in $toolNames
    $hasView   = 'view'   -in $toolNames
    $hasAgent  = 'agent'  -in $toolNames

    if ($hasSkill -and $toolNames.Count -le 2)       { return 'skill-load' }
    if ($hasGit -and -not $hasBuild)                 { return 'git' }
    if ($hasBuild -and $hasBuildError)               { return 'build-fix' }
    if ($hasBuild -and -not $hasBuildError)          { return 'build-ok' }
    if ($hasRun)                                     { return 'run' }
    if ($isDiagnosing -and -not $hasEdit)            { return 'diagnosing' }
    if ($hasScaffold)                                { return 'scaffold' }
    if ($hasAgent)                                   { return 'subagent' }
    if ($hasCreate -and -not $hasEdit)               { return 'code-create' }
    if ($hasEdit)                                    { return 'code-edit' }
    if ($hasView -and -not $hasEdit -and -not $hasCreate) { return 'explore' }
    if ($toolNames.Count -eq 0)                      { return 'thinking' }
    return 'other'
}

function Format-ToolList {
    param($Turn)
    $parts = $Turn.Tools | ForEach-Object {
        $err = if ($_.HasError) { " :x:" } else { "" }
        $summary = ""
        if     ($_.Name -in 'powershell', 'shell') { $summary = ($_.Args.command -split "`n")[0]; if ($summary.Length -gt 60) { $summary = $summary.Substring(0, 60) } }
        elseif ($_.Args.path)       { $summary = Split-Path $_.Args.path -Leaf }
        elseif ($_.Args.file_path)  { $summary = Split-Path $_.Args.file_path -Leaf }
        elseif ($_.Name -eq 'skill'){ $summary = $_.Args.skill }
        elseif ($_.Name -eq 'agent'){ $summary = $_.Args.subagent_type }
        elseif ($_.Args.pattern)    { $summary = $_.Args.pattern }
        if ($summary) { "$($_.Name)($summary)$err" } else { "$($_.Name)$err" }
    }
    $skills = if ($Turn.SkillInvocations.Count -gt 0) { " [skill: $($Turn.SkillInvocations -join ',')]" } else { "" }
    return ($parts -join ', ') + $skills
}

# -----------------------------------------------------------------------------
# Copilot parser
# -----------------------------------------------------------------------------
function Parse-CopilotEvents {
    param([string]$Path)

    $lines = Get-Content $Path -Encoding UTF8
    $events = @()
    foreach ($line in $lines) {
        if (-not $line.Trim()) { continue }
        try { $events += ($line | ConvertFrom-Json) } catch { }
    }
    if ($events.Count -eq 0) {
        Write-Error "No events found in $Path"
    }

    $userMsg      = $events | Where-Object { $_.type -eq 'user.message' } | Select-Object -First 1
    $resultEvent  = $events | Where-Object { $_.type -eq 'result' }       | Select-Object -First 1
    $skillsLoaded = $events | Where-Object { $_.type -eq 'session.skills_loaded' } | Select-Object -First 1
    $modelEvent   = $events | Where-Object { $_.type -eq 'session.tools_updated' } | Select-Object -First 1

    $prompt   = if ($userMsg.data.content) { $userMsg.data.content } else { "(no prompt found)" }
    $sid      = if ($resultEvent.sessionId) { $resultEvent.sessionId } else { "(unknown)" }
    $model    = if ($modelEvent.data.model) { $modelEvent.data.model } else { "(unknown)" }
    $exitCode = $resultEvent.exitCode
    $usage    = if ($resultEvent.usage) { $resultEvent.usage } else { @{} }

    $firstTs = if ($events[0].timestamp)  { [DateTime]::Parse($events[0].timestamp) }  else { $null }
    $lastTs  = if ($events[-1].timestamp) { [DateTime]::Parse($events[-1].timestamp) } else { $null }
    $durationMin = if ($firstTs -and $lastTs) { [math]::Round(($lastTs - $firstTs).TotalMinutes, 1) } else { 0 }

    $availableSkills = @()
    if ($skillsLoaded.data.skills) {
        $availableSkills = $skillsLoaded.data.skills | ForEach-Object { $_.name }
    }

    $turns = @()
    $currentTurn = $null
    $toolStarts = @{}

    foreach ($ev in $events) {
        switch ($ev.type) {
            'assistant.turn_start' {
                $currentTurn = New-NormalizedTurn -TurnNum ($turns.Count + 1) -Timestamp $ev.timestamp
            }
            'assistant.message' {
                if ($currentTurn) {
                    $currentTurn.OutputTokens = if ($ev.data.outputTokens) { $ev.data.outputTokens } else { 0 }
                    if ($ev.data.toolRequests) {
                        foreach ($tr in $ev.data.toolRequests) {
                            $callId = if ($tr.id) { $tr.id } else { $tr.toolCallId }
                            $currentTurn.Tools += @{
                                Name         = (Get-NormalizedToolName $tr.name)
                                RawName      = $tr.name
                                Args         = $tr.arguments
                                CallId       = $callId
                                HasError     = $false
                                ErrorSummary = @()
                            }
                        }
                    }
                }
            }
            'assistant.message_delta' {
                if ($currentTurn -and $ev.data.deltaContent) {
                    $currentTurn.TextSnippets += $ev.data.deltaContent
                }
            }
            'tool.execution_start' {
                $toolStarts[$ev.data.toolCallId] = @{
                    Name = $ev.data.toolName
                    Args = $ev.data.arguments
                }
            }
            'tool.execution_complete' {
                $start = $toolStarts[$ev.data.toolCallId]
                if ($start -and $currentTurn) {
                    $resultText = ""
                    $r = $ev.data.result
                    if     ($r -is [string])     { $resultText = $r }
                    elseif ($r.textResultForLlm) { $resultText = $r.textResultForLlm }
                    elseif ($r.content)          { $resultText = $r.content }
                    else                         { $resultText = ($r | ConvertTo-Json -Depth 3 -Compress) }

                    if ((Get-NormalizedToolName $start.Name) -eq 'skill' -and $start.Args.skill) {
                        $currentTurn.SkillInvocations += $start.Args.skill
                    }

                    $errInfo = Get-ErrorSummary -ResultText $resultText
                    foreach ($t in $currentTurn.Tools) {
                        if ($t.CallId -eq $ev.data.toolCallId) {
                            $t.HasError = $errInfo.HasError
                            if ($errInfo.HasError) { $t.ErrorSummary = $errInfo.Summary }
                            break
                        }
                    }
                }
            }
            'assistant.turn_end' {
                if ($currentTurn) {
                    $turns += [PSCustomObject]$currentTurn
                    $currentTurn = $null
                }
            }
        }
    }

    return [pscustomobject]@{
        Format          = 'Copilot'
        SessionId       = $sid
        Model           = $model
        DurationMin     = $durationMin
        Prompt          = $prompt
        ExitCode        = $exitCode
        Usage           = $usage
        Turns           = $turns
        AvailableSkills = $availableSkills
        Subagents       = @()
    }
}

# -----------------------------------------------------------------------------
# Claude Code parser
# -----------------------------------------------------------------------------
function Parse-ClaudeEvents {
    param(
        [string]$Path,
        [bool]$IsSubagent = $false
    )

    $events = @()
    foreach ($line in (Get-Content $Path -Encoding UTF8)) {
        if (-not $line.Trim()) { continue }
        try { $events += ($line | ConvertFrom-Json) } catch { }
    }
    if ($events.Count -eq 0) {
        Write-Error "No events found in $Path"
    }

    # Find the first "real" user prompt: not a meta event, not a sidechain
    # event, not a tool_result, and not a slash-command/local-command wrapper.
    $prompt = "(no prompt found)"
    foreach ($ev in $events) {
        if ($ev.type -ne 'user') { continue }
        if ($ev.isMeta -or $ev.isSidechain) { continue }
        $content = $ev.message.content
        $candidate = $null
        if ($content -is [string]) {
            $candidate = $content
        } elseif ($content -is [array]) {
            $hasToolResult = $false
            $textParts = @()
            foreach ($block in $content) {
                if ($block.type -eq 'tool_result') { $hasToolResult = $true; break }
                if ($block.type -eq 'text' -and $block.text) { $textParts += $block.text }
            }
            if (-not $hasToolResult -and $textParts.Count -gt 0) {
                $candidate = $textParts -join "`n"
            }
        }
        if (-not $candidate) { continue }
        # Skip slash-command stdout/caveat wrappers (they aren't user intent).
        $stripped = $candidate.Trim()
        if ($stripped -match '^<(local-)?command-(name|message|args|stdout)>') { continue }
        if ($stripped -match '^<local-command-caveat>') { continue }
        $prompt = $candidate
        break
    }

    $sid = if ($events[0].sessionId) { $events[0].sessionId } else {
        [System.IO.Path]::GetFileNameWithoutExtension($Path)
    }

    $firstAssistant = $events | Where-Object { $_.type -eq 'assistant' } | Select-Object -First 1
    $model = if ($firstAssistant.message.model) { $firstAssistant.message.model } else { "(unknown)" }

    $firstTs = $null; $lastTs = $null
    foreach ($ev in $events) {
        if ($ev.timestamp) {
            try {
                $ts = [DateTime]::Parse($ev.timestamp)
                if (-not $firstTs) { $firstTs = $ts }
                $lastTs = $ts
            } catch { }
        }
    }
    $durationMin = if ($firstTs -and $lastTs) { [math]::Round(($lastTs - $firstTs).TotalMinutes, 1) } else { 0 }

    $toolResults = @{}
    foreach ($ev in $events) {
        if ($ev.type -eq 'user' -and $ev.message.content -is [array]) {
            foreach ($block in $ev.message.content) {
                if ($block.type -eq 'tool_result') {
                    $resultText = ""
                    $c = $block.content
                    if     ($c -is [string]) { $resultText = $c }
                    elseif ($c -is [array]) {
                        foreach ($item in $c) {
                            if ($item.type -eq 'text' -and $item.text) {
                                $resultText += "`n" + $item.text
                            }
                        }
                    }
                    $isError = ($block.is_error -eq $true)
                    $toolResults[[string]$block.tool_use_id] = @{
                        Text        = $resultText
                        IsErrorFlag = $isError
                    }
                }
            }
        }
    }

    $turns = @()
    $turnNum = 0
    foreach ($ev in $events) {
        if ($ev.type -ne 'assistant') { continue }
        # Parent transcripts: skip sidechain events (those belong to subagents
        # and live in their own files). Subagent transcripts: keep them - the
        # subagent's own activity is stored as sidechain from the parent's
        # perspective.
        if (-not $IsSubagent -and $ev.isSidechain) { continue }

        $turnNum++
        $turn = New-NormalizedTurn -TurnNum $turnNum -Timestamp $ev.timestamp
        $usage = $ev.message.usage
        if ($usage) {
            $turn.OutputTokens      = if ($usage.output_tokens)               { [int]$usage.output_tokens }               else { 0 }
            $turn.CacheReadTokens   = if ($usage.cache_read_input_tokens)     { [int]$usage.cache_read_input_tokens }     else { 0 }
            $turn.CacheCreateTokens = if ($usage.cache_creation_input_tokens) { [int]$usage.cache_creation_input_tokens } else { 0 }
        }

        $content = $ev.message.content
        if ($content -is [array]) {
            foreach ($block in $content) {
                switch ($block.type) {
                    'text' {
                        if ($block.text) { $turn.TextSnippets += $block.text }
                    }
                    'tool_use' {
                        $callId   = [string]$block.id
                        $args     = $block.input
                        $normName = Get-NormalizedToolName $block.name
                        $tool = @{
                            Name         = $normName
                            RawName      = $block.name
                            Args         = $args
                            CallId       = $callId
                            HasError     = $false
                            ErrorSummary = @()
                        }
                        if ($normName -eq 'skill' -and $args.skill) {
                            $turn.SkillInvocations += [string]$args.skill
                        }
                        if ($normName -eq 'agent') {
                            $turn.AgentSpawns += @{
                                ToolUseId   = $callId
                                AgentType   = if ($args.subagent_type) { [string]$args.subagent_type } else { 'general-purpose' }
                                Description = if ($args.description)   { [string]$args.description }   else { '' }
                            }
                        }
                        $tr = $toolResults[$callId]
                        if ($tr) {
                            $errInfo = Get-ErrorSummary -ResultText $tr.Text
                            if ($tr.IsErrorFlag -or $errInfo.HasError) {
                                $tool.HasError = $true
                                $tool.ErrorSummary = $errInfo.Summary
                            }
                        }
                        $turn.Tools += $tool
                    }
                }
            }
        }

        $turns += [PSCustomObject]$turn
    }

    $subagents = @()
    if (-not $IsSubagent) {
        $sessionDir   = Join-Path (Split-Path $Path) ([System.IO.Path]::GetFileNameWithoutExtension($Path))
        $subagentsDir = Join-Path $sessionDir 'subagents'
        if (Test-Path $subagentsDir) {
            $agentFiles = Get-ChildItem $subagentsDir -Filter 'agent-*.jsonl' -ErrorAction SilentlyContinue
            foreach ($af in $agentFiles) {
                $agentId  = $af.BaseName -replace '^agent-', ''
                $metaPath = Join-Path $af.DirectoryName "agent-$agentId.meta.json"
                $meta = $null
                if (Test-Path $metaPath) {
                    try { $meta = Get-Content $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
                }
                $subResult = Parse-ClaudeEvents -Path $af.FullName -IsSubagent $true
                $subagents += [pscustomobject]@{
                    AgentId     = $agentId
                    AgentType   = if ($meta.agentType)   { $meta.agentType }   else { '(unknown)' }
                    Description = if ($meta.description) { $meta.description } else { '' }
                    Turns       = $subResult.Turns
                    DurationMin = $subResult.DurationMin
                    Model       = $subResult.Model
                }
            }
        }
    }

    return [pscustomobject]@{
        Format          = 'ClaudeCode'
        SessionId       = $sid
        Model           = $model
        DurationMin     = $durationMin
        Prompt          = $prompt
        ExitCode        = $null
        Usage           = @{}
        Turns           = $turns
        AvailableSkills = @()
        Subagents       = $subagents
    }
}

# -----------------------------------------------------------------------------
# Resolve which session to analyze
# -----------------------------------------------------------------------------
$session = $null
$detectedFormat = $Format

if ($EventsFile) {
    if (-not (Test-Path $EventsFile)) {
        Write-Error "Events file not found: $EventsFile"
        exit 1
    }
    if (-not $detectedFormat) {
        $detectedFormat = Get-EventFormatFromContent -Path $EventsFile
        if (-not $detectedFormat) {
            Write-UnsupportedHarnessError -ExtraContext "The file at '$EventsFile' did not match either known format. Use -Format to override."
        }
    }
    $session = [pscustomobject]@{
        SessionId = [System.IO.Path]::GetFileNameWithoutExtension($EventsFile)
        Path      = (Resolve-Path $EventsFile).Path
        Modified  = (Get-Item $EventsFile).LastWriteTime
    }
} elseif ($SessionId) {
    $session = Find-CopilotSessionById -Id $SessionId
    if ($session) {
        if (-not $detectedFormat) { $detectedFormat = 'Copilot' }
    } else {
        $session = Find-ClaudeSessionById -Id $SessionId
        if ($session -and -not $detectedFormat) { $detectedFormat = 'ClaudeCode' }
    }
    if (-not $session) {
        Write-UnsupportedHarnessError -ExtraContext "Session ID '$SessionId' was not found in either harness location."
    }
} else {
    # Auto-detect: prefer the *current* session via harness-provided env vars
    # (COPILOT_AGENT_SESSION_ID / CLAUDE_SESSION_ID) so we don't accidentally
    # pick up a more-recently-modified parallel session from another terminal.
    if (-not $detectedFormat) {
        if (Test-ClaudeEnvironment)      { $detectedFormat = 'ClaudeCode' }
        elseif (Test-CopilotEnvironment) { $detectedFormat = 'Copilot' }
    }

    $envSessionId = $null
    switch ($detectedFormat) {
        'Copilot'    { if ($env:COPILOT_AGENT_SESSION_ID) { $envSessionId = $env:COPILOT_AGENT_SESSION_ID } }
        'ClaudeCode' { if ($env:CLAUDE_SESSION_ID)        { $envSessionId = $env:CLAUDE_SESSION_ID } }
    }

    if ($envSessionId) {
        switch ($detectedFormat) {
            'Copilot'    { $session = Find-CopilotSessionById -Id $envSessionId }
            'ClaudeCode' { $session = Find-ClaudeSessionById  -Id $envSessionId }
        }
        if (-not $session) {
            Write-Warning "Current session ID '$envSessionId' from $detectedFormat env var was not found on disk; falling back to most recent."
        }
    }

    if (-not $session) {
        switch ($detectedFormat) {
            'ClaudeCode' { $session = Find-LatestClaudeSession -PreferCwd $PWD.Path }
            'Copilot'    { $session = Find-LatestCopilotSession }
            default {
                $cop = Find-LatestCopilotSession
                $cla = Find-LatestClaudeSession -PreferCwd $PWD.Path
                if ($cop -and $cla) {
                    if ($cop.Modified -ge $cla.Modified) { $session = $cop; $detectedFormat = 'Copilot' }
                    else                                 { $session = $cla; $detectedFormat = 'ClaudeCode' }
                } elseif ($cop) { $session = $cop; $detectedFormat = 'Copilot' }
                elseif ($cla)   { $session = $cla; $detectedFormat = 'ClaudeCode' }
            }
        }
    }
    if (-not $session) {
        Write-UnsupportedHarnessError -ExtraContext "No sessions were found in either harness's storage directory."
    }
}

# -----------------------------------------------------------------------------
# Parse the session
# -----------------------------------------------------------------------------
switch ($detectedFormat) {
    'Copilot'    { $parsed = Parse-CopilotEvents -Path $session.Path }
    'ClaudeCode' { $parsed = Parse-ClaudeEvents -Path $session.Path }
    default      { Write-UnsupportedHarnessError -ExtraContext "Format '$detectedFormat' is not supported." }
}

# Fall back to the session ID we resolved during discovery if the parser
# couldn't recover one from the events (e.g., Copilot transcripts that were
# truncated before the `result` event was written).
if ($parsed.SessionId -eq '(unknown)' -and $session.SessionId) {
    $parsed.SessionId = $session.SessionId
}

foreach ($t in $parsed.Turns) { $t.Category = Get-TurnCategory -Turn $t }
foreach ($sa in $parsed.Subagents) {
    foreach ($t in $sa.Turns) { $t.Category = Get-TurnCategory -Turn $t }
}

# -----------------------------------------------------------------------------
# Aggregate analysis
# -----------------------------------------------------------------------------
$includeSubagents = -not $SkipSubagents -and $parsed.Subagents.Count -gt 0
$allTurns = @($parsed.Turns)
if ($includeSubagents) {
    foreach ($sa in $parsed.Subagents) { $allTurns += $sa.Turns }
}

$buildAttemptTurns = @($allTurns | Where-Object {
    $_.Tools | Where-Object {
        if ($_.Name -notin 'powershell', 'shell') { return $false }
        return Test-BuildCapableCommand -Command $_.Args.command
    }
})
$buildFailureTurns = @($buildAttemptTurns | Where-Object {
    $_.Tools | Where-Object {
        if (-not $_.HasError -or $_.Name -notin 'powershell', 'shell') { return $false }
        return Test-BuildCapableCommand -Command $_.Args.command
    }
})
$buildWorkflowAttempts  = $buildAttemptTurns.Count
$buildWorkflowFailures  = $buildFailureTurns.Count
$buildWorkflowSuccesses = $buildWorkflowAttempts - $buildWorkflowFailures

$buildErrors = @()
foreach ($t in $allTurns) {
    foreach ($tool in $t.Tools) {
        if ($tool.HasError -and $tool.Name -in 'powershell', 'shell' -and
            (Test-BuildCapableCommand -Command $tool.Args.command) -and $tool.ErrorSummary.Count -gt 0) {
            $buildErrors += @{ Turn = $t.TurnNum; Errors = $tool.ErrorSummary }
        }
    }
}

$workflowLabels = @()
foreach ($workflow in @(
    @{ Pattern = '\bdotnet\s+(?:build|publish)\b'; Label = 'dotnet build/publish' }
    @{ Pattern = '\bwinapp(?:\.exe)?\s+(?:run|pack(?:age)?)\b'; Label = 'winapp project build/publish/package' }
    @{ Pattern = 'BuildAndRun'; Label = 'historical BuildAndRun.ps1' }
    @{ Pattern = '\bmsbuild\b'; Label = 'MSBuild' }
)) {
    $count = @($buildAttemptTurns | Where-Object {
        $_.Tools | Where-Object {
            if ($_.Name -notin 'powershell', 'shell') { return $false }
            [regex]::Split($_.Args.command, ';|&&|\|\||\r?\n') | Where-Object {
                $_ -match $workflow.Pattern -and (Test-BuildCapableCommand -Command $_)
            }
        }
    }).Count
    if ($count) { $workflowLabels += "$($workflow.Label): $count turn(s)" }
}
$projectBuildStatus = if ($workflowLabels.Count) { $workflowLabels -join '; ' } else { 'No build commands detected' }

$sandboxCommands = @(
    foreach ($turn in $allTurns) {
        foreach ($tool in $turn.Tools) {
            if ($tool.Name -in 'powershell', 'shell' -and
                $tool.Args.command -match '(?i)--on(?:\s+|=)sandbox\b|\bwinapp\s+target\s+\S+\s+sandbox\b') {
                [pscustomobject]@{ Turn = $turn.TurnNum; HasError = $tool.HasError }
            }
        }
    }
)
$sandboxFailures = @($sandboxCommands | Where-Object HasError).Count

$skillTimeline = @()
foreach ($t in $parsed.Turns) {
    foreach ($skill in $t.SkillInvocations) {
        $skillTimeline += @{ Turn = $t.TurnNum; Skill = $skill; Origin = 'parent' }
    }
}
foreach ($sa in $parsed.Subagents) {
    foreach ($t in $sa.Turns) {
        foreach ($skill in $t.SkillInvocations) {
            $skillTimeline += @{ Turn = $t.TurnNum; Skill = $skill; Origin = "subagent:$($sa.AgentType)" }
        }
    }
}
$invokedSkills = $skillTimeline | ForEach-Object { $_.Skill } | Select-Object -Unique
$notInvoked = $parsed.AvailableSkills | Where-Object { $_ -notin $invokedSkills -and $_ -ne 'customize-cloud-agent' }

$totalOutputTokens      = ($allTurns | Measure-Object -Property OutputTokens -Sum).Sum
$totalCacheReadTokens   = ($allTurns | Measure-Object -Property CacheReadTokens -Sum).Sum
$totalCacheCreateTokens = ($allTurns | Measure-Object -Property CacheCreateTokens -Sum).Sum

$categoryGroups = $allTurns | Group-Object -Property Category
$categoryTable  = $categoryGroups | ForEach-Object {
    $tokens = ($_.Group | Measure-Object -Property OutputTokens -Sum).Sum
    @{ Category = $_.Name; Turns = $_.Count; Tokens = $tokens }
} | Sort-Object { $_.Turns } -Descending

$stuckPatterns = @()
$fileReads = @{}
foreach ($t in $allTurns) {
    foreach ($tool in $t.Tools) {
        if ($tool.Name -eq 'view' -and ($tool.Args.path -or $tool.Args.file_path)) {
            $p = if ($tool.Args.path) { $tool.Args.path } else { $tool.Args.file_path }
            $file = Split-Path $p -Leaf
            $fileReads[$file] = $(if ($fileReads[$file]) { $fileReads[$file] } else { 0 }) + 1
        }
    }
}
$excessiveReads = $fileReads.GetEnumerator() | Where-Object { $_.Value -ge 3 }
if ($excessiveReads) {
    $detail = ($excessiveReads | ForEach-Object { "$($_.Key) ($($_.Value)x)" }) -join ', '
    $stuckPatterns += "Repeated file reads: $detail"
}

$consecutive = 0; $maxConsec = 0
foreach ($t in $allTurns) {
    if     ($t.Category -eq 'build-fix') { $consecutive++; $maxConsec = [Math]::Max($maxConsec, $consecutive) }
    elseif ($t.Category -eq 'build-ok')  { $consecutive = 0 }
}
if ($maxConsec -ge 3) {
    $stuckPatterns += "Build loop: $maxConsec consecutive build failures before success"
}

$objCleans = ($allTurns | Where-Object {
    $_.Tools | Where-Object { $_.Args.command -match 'Remove-Item.*obj' }
}).Count
if ($objCleans -ge 2) {
    $stuckPatterns += "Cleaned obj/ directory ${objCleans}x (suggests stale XAML compiler state)"
}

$toolingIssues = @()
if ($buildErrors | Where-Object { $_.Errors -match 'MSB3073' }) {
    $toolingIssues += @{
        Area       = "XAML Compiler"
        Issue      = "XamlCompiler.exe crashed (MSB3073) - agent could not diagnose from error output"
        Suggestion = "Clean obj/ first when MSB3073 occurs. CS0103 errors for x:Name elements are a side-effect of XAML compiler failure - fix XAML before C#."
    }
}
$devWorkflowEntry = $skillTimeline | Where-Object { $_.Skill -match 'winui-dev-workflow' } | Select-Object -First 1
$firstBuildTurn   = ($buildAttemptTurns | Select-Object -First 1).TurnNum
if ($devWorkflowEntry -and $firstBuildTurn -and $devWorkflowEntry.Turn -gt $firstBuildTurn) {
    $toolingIssues += @{
        Area       = "Skill timing"
        Issue      = "dev-workflow skill loaded at turn $($devWorkflowEntry.Turn) but first build was turn $firstBuildTurn"
        Suggestion = "Agent should load dev-workflow before its first build attempt."
    }
}

# -----------------------------------------------------------------------------
# Render markdown report
# -----------------------------------------------------------------------------
$categoryLabels = @{
    'skill-load' = 'Skill loading';        'explore'    = 'Reading/exploring'; 'scaffold'  = 'Scaffolding'
    'code-create' = 'Creating files';      'code-edit'  = 'Editing code';      'build-ok'  = 'Build (success)'
    'build-fix'   = 'Build (failed)';      'run'        = 'Running app';       'git'       = 'Git operations'
    'thinking'    = 'Thinking (no tools)'; 'diagnosing' = 'Diagnosing errors'; 'subagent'  = 'Subagent dispatch'
    'other'       = 'Other'
}

$md = @()
$md += "# Session Analysis Report"
$md += ""
$md += "## Overview"
$md += ""
$md += "| Field | Value |"
$md += "|-------|-------|"
$md += "| Harness | $($parsed.Format) |"
$md += "| Session ID | " + '`' + $parsed.SessionId + '`' + " |"
$md += "| Model | $($parsed.Model) |"
$md += "| Duration | $($parsed.DurationMin) min |"
$md += "| Turns (parent) | $($parsed.Turns.Count) |"
if ($includeSubagents) {
    $subTurnTotal = ($parsed.Subagents | ForEach-Object { $_.Turns.Count } | Measure-Object -Sum).Sum
    $md += "| Subagents | $($parsed.Subagents.Count) ($subTurnTotal turns) |"
}
$md += "| Output tokens (combined) | $($totalOutputTokens.ToString('N0')) |"
if ($parsed.Format -eq 'ClaudeCode') {
    $md += "| Cache read tokens | $($totalCacheReadTokens.ToString('N0')) |"
    $md += "| Cache create tokens | $($totalCacheCreateTokens.ToString('N0')) |"
}
if ($parsed.Format -eq 'Copilot') {
    $md += "| Premium requests | $(if ($parsed.Usage.premiumRequests) { $parsed.Usage.premiumRequests } else { 'N/A' }) |"
    $md += "| Exit code | $($parsed.ExitCode) |"
    $md += "| Lines added | $(if ($parsed.Usage.codeChanges.linesAdded) { $parsed.Usage.codeChanges.linesAdded } else { 'N/A' }) |"
    $md += "| Files modified | $(if ($parsed.Usage.codeChanges.filesModified) { $parsed.Usage.codeChanges.filesModified.Count } else { 'N/A' }) |"
}
$md += ""

$md += "## Prompt"
$md += ""
$promptDisplay = if ($parsed.Prompt.Length -gt 500) { $parsed.Prompt.Substring(0, 500) + "..." } else { $parsed.Prompt }
$md += '```'
$md += $promptDisplay
$md += '```'
$md += ""

$md += "## Turn Breakdown"
$md += ""
if ($includeSubagents) {
    $md += "_Combined parent + $($parsed.Subagents.Count) subagent transcript(s)._"
    $md += ""
}
$md += "| Category | Turns | Output Tokens |"
$md += "|----------|------:|--------------:|"
foreach ($cat in $categoryTable) {
    $label = if ($categoryLabels[$cat.Category]) { $categoryLabels[$cat.Category] } else { $cat.Category }
    $md += "| $label | $($cat.Turns) | $($cat.Tokens.ToString('N0')) |"
}
$md += ""

$md += "## Skills"
$md += ""
if ($skillTimeline.Count -gt 0) {
    $md += "**Invoked:**"
    foreach ($s in $skillTimeline) {
        $origin = if ($s.Origin -eq 'parent') { '' } else { " _(in $($s.Origin))_" }
        $md += "- Turn $($s.Turn): " + '`' + $s.Skill + '`' + $origin
    }
} else {
    $md += "_No skills were invoked during this session._"
}
$md += ""
if ($parsed.AvailableSkills.Count -gt 0 -and $notInvoked.Count -gt 0) {
    $notInvokedStr = ($notInvoked | ForEach-Object { '`' + $_ + '`' }) -join ', '
    $md += "**Available but not invoked:** $notInvokedStr"
    $md += ""
} elseif ($parsed.Format -eq 'ClaudeCode') {
    $md += "_Available-skill enumeration is not yet supported for Claude Code transcripts._"
    $md += ""
}

if ($includeSubagents) {
    $md += "## Subagents"
    $md += ""
    $md += "| Agent ID | Type | Turns | Duration | Description |"
    $md += "|---|---|---:|---:|---|"
    foreach ($sa in $parsed.Subagents) {
        $desc = if ($sa.Description.Length -gt 60) { $sa.Description.Substring(0, 60) + '...' } else { $sa.Description }
        $md += "| ``$($sa.AgentId)`` | $($sa.AgentType) | $($sa.Turns.Count) | $($sa.DurationMin) min | $desc |"
    }
    $md += ""
}

$md += "## Build Analysis"
$md += ""
$md += "- **Build-capable workflow attempts:** $buildWorkflowAttempts ($buildWorkflowSuccesses completed without a command error, $buildWorkflowFailures command failures)"
$md += "- **Project build workflow:** $projectBuildStatus"
$md += "- When installed, NuGet-delivered analyzers run in ordinary dotnet build/publish and project-mode CLI workflows; direct dotnet usage is not evidence of a missing analyzer. If unavailable, report the coverage gap without treating it as a task blocker."
$md += "- Classification is based on command text: explicit project inputs and current-directory run workflows are recognized; other run directory inputs are ambiguous. Folder/manifest packaging and --no-build invocations are not builds."
$md += ""
if ($buildErrors.Count -gt 0) {
    $md += "**Build/publish workflow command errors encountered:**"
    $md += ""
    foreach ($be in $buildErrors) {
        $md += "Turn $($be.Turn):"
        foreach ($err in $be.Errors) {
            $md += "- " + '`' + $err + '`'
        }
    }
    $md += ""
}

if ($sandboxCommands.Count) {
    $md += "## Sandbox Execution"
    $md += ""
    $md += "- **Sandbox command calls:** $($sandboxCommands.Count) ($sandboxFailures with reported errors). Builds run on the host; deployment, launch, and UI run in the guest."
    $md += "- Review failed target startup/readiness, deployment, UI, and capture calls separately from compiler failures. An error in a build-capable run command does not prove compilation failed."
    $md += "- Check target scope on every UI call (including picker HWNDs), fresh-guest PID invalidation, one WINAPP_UI_WORKFLOW_ID per cooperating flow, and an unlocked host with a connected, nonminimized Sandbox client for input/capture. Tree reads alone do not prove input readiness."
    $md += "- Preserve delivered host evidence and recovery paths before a consented shutdown. Local execution after explaining unavailable Windows Sandbox is valid unless the user explicitly requested Windows Sandbox. Flag silent switches, reused guest IDs, and capture claims without delivered evidence."
    $md += ""
}

if ($stuckPatterns.Count -gt 0) {
    $md += "## Stuck Patterns"
    $md += ""
    foreach ($sp in $stuckPatterns) { $md += "- $sp" }
    $md += ""
}

if ($toolingIssues.Count -gt 0) {
    $md += "## Tooling Improvement Opportunities"
    $md += ""
    foreach ($ti in $toolingIssues) {
        $md += "### $($ti.Area)"
        $md += "- **Issue:** $($ti.Issue)"
        $md += "- **Suggestion:** $($ti.Suggestion)"
        $md += ""
    }
}

$md += "## Turn Detail"
$md += ""
$md += "_Parent session._"
$md += ""
$md += "| # | Category | Tokens | Tools |"
$md += "|--:|----------|-------:|-------|"
foreach ($t in $parsed.Turns) {
    $md += "| $($t.TurnNum) | $($t.Category) | $($t.OutputTokens.ToString('N0')) | $(Format-ToolList $t) |"
}
$md += ""

if ($includeSubagents) {
    foreach ($sa in $parsed.Subagents) {
        $md += "_Subagent " + '`' + $sa.AgentType + '`' + " (id ``$($sa.AgentId)``)._"
        $md += ""
        $md += "| # | Category | Tokens | Tools |"
        $md += "|--:|----------|-------:|-------|"
        foreach ($t in $sa.Turns) {
            $md += "| $($t.TurnNum) | $($t.Category) | $($t.OutputTokens.ToString('N0')) | $(Format-ToolList $t) |"
        }
        $md += ""
    }
}

$report = $md -join "`n"

# -----------------------------------------------------------------------------
# Privacy notice (kept in sync with SKILL.md)
# -----------------------------------------------------------------------------
$privacyHeading = "## Privacy and sensitivity — read before sharing this file"
$privacyBody = @(
    ""
    "**This report was generated from your live agent session and was NOT redacted.** It can include any of the following, depending on what your session involved:"
    ""
    "- File contents and paths the agent read or edited (code, configuration, secrets accidentally pasted into prompts, internal URLs, customer data, anything you asked the agent about)."
    "- Your prompts verbatim — including any credentials, tokens, identifiers, or proprietary information you typed."
    "- Tool output — ``git`` history, environment variables echoed by failing commands, build logs containing machine names and ``C:\Users\<you>\…`` paths, etc."
    "- Error messages quoting source code or stack traces from third-party libraries."
    ""
    "**You are responsible for the contents of this file.** Open it in your editor and read it end-to-end before attaching it to a public issue, posting it in chat, or sending it outside your organization. Redact anything sensitive (paths, names, secrets, business logic). When in doubt, share excerpts rather than the whole file, or ask the agent to summarize the metrics instead."
    ""
)
$privacySection = @($privacyHeading) + $privacyBody -join "`n"

$reportLines = $report -split "`n"
$insertAt = 1
for ($i = 0; $i -lt $reportLines.Count; $i++) {
    if ($reportLines[$i] -match '^## Overview') { $insertAt = $i; break }
}
$reportWithNotice = (
    $reportLines[0..($insertAt - 1)] +
    @($privacySection, "") +
    $reportLines[$insertAt..($reportLines.Count - 1)]
) -join "`n"

if ($OutputFile) {
    Set-Content -Path $OutputFile -Value $reportWithNotice -Encoding UTF8
    Write-Host ""
    Write-Host "Report saved to: $OutputFile" -ForegroundColor Green
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Yellow
    Write-Host " PRIVACY NOTICE - READ BEFORE SHARING $OutputFile" -ForegroundColor Yellow
    Write-Host "================================================================" -ForegroundColor Yellow
    Write-Host " This report contains your unredacted session transcript:"     -ForegroundColor Yellow
    Write-Host "   * file contents and paths the agent read or edited"          -ForegroundColor Yellow
    Write-Host "   * your prompts verbatim (including any secrets you pasted)"  -ForegroundColor Yellow
    Write-Host "   * tool output, error messages, local paths, env values"      -ForegroundColor Yellow
    Write-Host ""                                                                -ForegroundColor Yellow
    Write-Host " You are responsible for what you share. Open the file and"     -ForegroundColor Yellow
    Write-Host " read it end-to-end before posting it publicly or sending it"   -ForegroundColor Yellow
    Write-Host " outside your organization. Redact anything sensitive."         -ForegroundColor Yellow
    Write-Host "================================================================" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Output $reportWithNotice
}
