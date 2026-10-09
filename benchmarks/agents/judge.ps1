#Requires -Version 7.4
<#
.SYNOPSIS
    Grades benchmark answers against each scenario's frozen rubric with LLM judges.

.DESCRIPTION
    For scenarios that ship a rubric.json (the demand set), every completed run's final response is
    graded solved / partial / unsolved by each judge model, independently of which skills loaded.
    Answers are anonymized (no model, configuration, or plugin), shuffled, and graded in small
    batches per scenario. Every answer is graded by every judge, so a model never grades its own
    family's answers without a judge from another family also grading them.

    Judgments append to <OutDir>\judgments.jsonl and are cached: rerunning grades only new
    (run, judge) pairs, so new result folders can be added later. An answer is keyed by its result
    folder's name and line number in runs.jsonl, so result folder names must be unique.

.EXAMPLE
    pwsh benchmarks\agents\judge.ps1 -Results benchmarks\agents\results\dm-base-sonnet-i1,benchmarks\agents\results\dm-cand-sonnet-i1 -OutDir benchmarks\agents\results\dm-judge -MaxCredits 500
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Results,
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Judge,
    [int]$BatchSize = 6,
    [int]$Throttle = 6,
    [double]$MaxCredits = 0,
    [double]$AttemptEstimate = 20,
    [int]$TimeoutMinutes = 8,
    [int]$Seed = 20261008,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'lib\Benchmark.psm1'
$outcomePath = Join-Path $PSScriptRoot 'lib\Outcome.psm1'
Import-Module $modulePath -Force
Import-Module $outcomePath -Force
$Results = Split-ListArgument $Results
$Judge = if ($Judge) { Split-ListArgument $Judge } else { Get-DefaultJudge }
$byId = @{}
foreach ($s in Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot 'scenarios')) { $byId[$s.Id] = $s }

# --- Collect answers ----------------------------------------------------------------------
$answers = [System.Collections.Generic.List[object]]::new()
$folders = @{}
foreach ($dir in $Results) {
    $resolved = Resolve-Path -LiteralPath $dir -ErrorAction SilentlyContinue
    if (-not $resolved) { Write-Warning "Result folder not found: $dir"; continue }
    $full = $resolved.Path
    $runsPath = Join-Path $full 'runs.jsonl'
    if (-not (Test-Path -LiteralPath $runsPath)) { Write-Warning "No runs.jsonl in $full"; continue }
    $folder = Split-Path $full -Leaf
    if ($folders.ContainsKey($folder)) { throw "Two result folders are named '$folder'; answers are keyed by folder name, so rename one." }
    $folders[$folder] = $true
    $n = 0
    foreach ($line in [System.IO.File]::ReadLines($runsPath)) {
        $n++
        if (-not $line.Trim()) { continue }
        $rec = $line | ConvertFrom-Json -Depth 64
        $s = $byId[$rec.scenario]
        if (-not $s -or -not $s.Rubric) { continue }
        if ($rec.status -notin 'pass', 'partial', 'fail', 'n/a') { continue }
        $resp = $rec.PSObject.Properties['finalResponse'] ? [string]$rec.finalResponse : ''
        $answers.Add([pscustomobject]@{ Key = "$folder#$n"; Scenario = $rec.scenario; Response = $resp })
    }
}
Write-Host "$($answers.Count) answers from $($Results.Count) result folder(s)"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$judgPath = Join-Path $OutDir 'judgments.jsonl'
# Every batch that ran, graded or not, records its credits here, so -MaxCredits also counts batches
# that failed in earlier invocations.
$spendPath = Join-Path $OutDir 'spend.jsonl'
$done = @{}
$spent = 0.0
if (Test-Path -LiteralPath $judgPath) {
    foreach ($line in [System.IO.File]::ReadLines($judgPath)) {
        if (-not $line.Trim()) { continue }
        $j = $line | ConvertFrom-Json
        $done["$($j.key)|$($j.judge)"] = $true
    }
}
if (Test-Path -LiteralPath $spendPath) {
    foreach ($line in [System.IO.File]::ReadLines($spendPath)) { if ($line.Trim()) { $spent += [double]($line | ConvertFrom-Json).credits } }
}

function Write-Judgment([hashtable]$Fields) {
    [ordered]@{
        key = $Fields.key; scenario = $Fields.scenario; judge = $Fields.judge; verdict = $Fields.verdict
        mustIncludeMet = @($Fields.mustIncludeMet); mustNotViolated = @($Fields.mustNotViolated); rationale = $Fields.rationale
        batch = $Fields.batch
    } | ConvertTo-Json -Compress -Depth 5 | Add-Content -LiteralPath $judgPath -Encoding utf8NoBOM
}

$batches = @(New-JudgeBatch -Answers @($answers) -Judges $Judge -Done $done -BatchSize $BatchSize -Seed $Seed)
$empty = @($answers | Where-Object { -not $_.Response.Trim() })
Write-Host "$($batches.Count) judge batches to run, $($empty.Count) empty answers (graded unsolved without a judge); already spent $([Math]::Round($spent, 1)) credits"
if ($Plan) { return }

# Empty answers need no judge.
foreach ($a in $empty) {
    foreach ($m in $Judge) {
        if ($done.ContainsKey("$($a.Key)|$m")) { continue }
        Write-Judgment @{ key = $a.Key; scenario = $a.Scenario; judge = $m; verdict = 'unsolved'; rationale = 'empty final response' }
        $done["$($a.Key)|$m"] = $true
    }
}
if (-not $batches) { Write-Host "Nothing to grade. Judgments: $judgPath"; return }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-judge\$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
$prepared = foreach ($b in $batches) {
    $s = $byId[$b.Scenario]
    $text = New-JudgePrompt -Scenario $s -Answers @($b.Items | ForEach-Object Response)
    $dir = Join-Path $work $b.Id
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'ws'), (Join-Path $dir 'logs') | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'ws\judge-input.md') -Value $text -Encoding utf8NoBOM
    [pscustomobject]@{ Batch = $b; Dir = $dir; Keys = @($b.Items | ForEach-Object Key) }
}

$copilotExe = (Get-Command copilot -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
# -MaxCredits holds across parallel workers: each attempt reserves an estimate before it starts (the
# largest attempt cost seen so far, at least $AttemptEstimate) and none starts once spent plus
# reserved credits would pass the limit.
$budget = [hashtable]::Synchronized(@{ Spent = $spent; Reserved = 0.0; Estimate = [double]$AttemptEstimate; Max = $MaxCredits })
$results = $prepared | ForEach-Object -ThrottleLimit $Throttle -Parallel {
    $p = $_
    Import-Module $using:modulePath -Force
    Import-Module $using:outcomePath -Force
    $b = $p.Batch
    $out = [ordered]@{ Id = $b.Id; Judge = $b.Judge; Scenario = $b.Scenario; Keys = $p.Keys; Verdicts = $null; Credits = 0.0; Error = $null }
    $bud = $using:budget
    foreach ($attempt in 1..2) {
        $reserved = 0.0
        [System.Threading.Monitor]::Enter($bud.SyncRoot)
        try {
            if (-not $bud.Max -or $bud.Spent + $bud.Reserved + $bud.Estimate -le $bud.Max) { $reserved = $bud.Estimate; $bud.Reserved += $reserved }
        }
        finally { [System.Threading.Monitor]::Exit($bud.SyncRoot) }
        if ($bud.Max -and -not $reserved) { $out.Error = "skipped: -MaxCredits $($bud.Max) would be exceeded"; break }
        # Each attempt gets an empty Copilot home, like a benchmark run: no plugins, no login state.
        $copilotHome = Join-Path $p.Dir "home$attempt"
        New-Item -ItemType Directory -Force -Path $copilotHome | Out-Null
        Set-Content -LiteralPath (Join-Path $copilotHome 'config.json') -Value '{"autoUpdate": false}'
        $envMap = New-ChildEnvironment -CopilotHome $copilotHome
        $judgeArgs = @('-C', (Join-Path $p.Dir 'ws'), '-p', 'Read judge-input.md in the current directory and follow its instructions exactly. Reply with only the JSON array.',
            '--model', $b.Judge, '--output-format', 'json', '--stream', 'off', '--allow-all-tools', '--deny-tool=shell', '--deny-tool=write',
            '--deny-tool=url', '--disable-builtin-mcps', '--no-custom-instructions', '--no-ask-user')
        $r = Invoke-LoggedProcess -FilePath $using:copilotExe -Arguments $judgeArgs -WorkingDirectory (Join-Path $p.Dir 'ws') -Environment $envMap `
            -StdoutPath (Join-Path $p.Dir "logs\judge$attempt.out") -StderrPath (Join-Path $p.Dir "logs\judge$attempt.err") -TimeoutSeconds ($using:TimeoutMinutes * 60)
        $ev = Get-ChildItem -Path (Join-Path $copilotHome 'session-state') -Filter events.jsonl -Recurse -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
        $parsed = Read-SessionEvents -Path ($ev ? $ev.FullName : '')
        # An attempt that reported no credits (for example a timeout) counts at its reservation.
        $measured = $null -ne $parsed.aiCredits
        $credits = $measured ? [double]$parsed.aiCredits : [double]$reserved
        $out.Credits += $credits
        [System.Threading.Monitor]::Enter($bud.SyncRoot)
        try {
            $bud.Spent += $credits; $bud.Reserved -= $reserved
            if ($measured) { $bud.Estimate = [Math]::Max($bud.Estimate, $credits) }
            [ordered]@{ batch = $b.Id; judge = $b.Judge; attempt = $attempt; credits = $credits; measured = $measured } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath $using:spendPath -Encoding utf8NoBOM
        }
        finally { [System.Threading.Monitor]::Exit($bud.SyncRoot) }
        $reply = ConvertFrom-JudgeReply -Text ([string]$parsed.finalResponse) -Count $p.Keys.Count
        if ($reply.Verdicts) { $out.Verdicts = $reply.Verdicts; $out.Error = $null; break }
        $out.Error = "$($reply.Error) (exit $($r.ExitCode), timed out $($r.TimedOut))"
    }
    [pscustomobject]$out | ConvertTo-Json -Depth 8 -Compress
}

$failed = 0
foreach ($res in @($results | ForEach-Object { $_ | ConvertFrom-Json })) {
    $spent += [double]$res.Credits
    if (-not $res.Verdicts) { $failed++; Write-Warning "Batch $($res.Id) failed: $($res.Error)"; continue }
    for ($i = 0; $i -lt $res.Keys.Count; $i++) {
        $v = $res.Verdicts[$i]
        Write-Judgment @{
            key = $res.Keys[$i]; scenario = $res.Scenario; judge = $res.Judge; verdict = $v.Verdict
            mustIncludeMet = $v.MustIncludeMet; mustNotViolated = $v.MustNotViolated; rationale = $v.Rationale
            batch = $res.Id
        }
    }
}
Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
Write-Host "Done: $($batches.Count - $failed) batches graded, $failed failed; judge credits so far $([Math]::Round($spent, 1)). Judgments: $judgPath"
if ($MaxCredits -and $spent -gt $MaxCredits) { Write-Warning "Judge credits $([Math]::Round($spent, 1)) exceed -MaxCredits $MaxCredits" }
if ($failed) { exit 1 }
