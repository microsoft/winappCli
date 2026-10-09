Set-StrictMode -Version Latest

# Outcome scoring for scenarios with a rubric (the demand set): judge prompts, judge replies,
# verdict scores, and the statistics demand-analyze.ps1 reports.

$script:VerdictScore = @{ solved = 1.0; partial = 0.5; unsolved = 0.0 }
# Two judges from different model families, so no answer is graded only by its own family.
$script:DefaultJudges = @('claude-opus-5.5', 'gpt-6.1-sol')

function Get-DefaultJudge { , @($script:DefaultJudges) }

$script:JudgeTemplate = @'
You are grading answers from AI coding assistants to a Windows app developer's request. Be strict, fair, and consistent. Grade each answer ONLY against the rubric below; do not reward length, confidence, or tool usage, and do not penalize an answer for using a different correct approach that the rubric lists as acceptable. The assistants could read the project files but could NOT run shell commands or edit files, so an answer that gives the developer correct, specific steps or commands to run counts the same as having run them. Telling the developer to run something themselves is fine.

## The developer's request
{PROMPT}

## Project files the assistant could see
{FIXTURE}

## Rubric (frozen before any answers were collected)
Goal: {GOAL}

Must include (each is a point a correct answer covers; wording can differ):
{MUST}

Must NOT (wrong, harmful, or unsafe advice; any one of these caps the verdict at "partial", and a serious one at "unsolved"):
{MUSTNOT}

Acceptable alternative approaches (a correct answer may use any of these instead of the approach implied above):
{ALT}

Verdict definitions:
- solved: {SOLVED}
- partial: {PARTIAL}
- unsolved: anything less, including answers that only ask clarifying questions when the files already contain the needed information, that give generic advice not specific to this situation, or that are wrong.

## Answers to grade
{ANSWERS}

## Output
Reply with ONLY a JSON array, one object per answer, in this shape and nothing else:
[{"id":"A1","verdict":"solved|partial|unsolved","must_include_met":[1,3],"must_not_violated":[],"rationale":"<= 40 words"}]
'@

function Get-VerdictScore {
    param([AllowNull()][string]$Verdict)
    if ($Verdict -and $script:VerdictScore.ContainsKey($Verdict)) { return $script:VerdictScore[$Verdict] }
    return $null
}

function Get-FixtureText {
    # The project files the answering agent could read, inlined for the judge.
    param([AllowNull()][string]$Path, [int]$MaxChars = 6000)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return '(no project files)' }
    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\') + '\'
    $sb = [System.Text.StringBuilder]::new()
    foreach ($f in Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName) {
        $rel = $f.FullName.Substring($root.Length)
        $isText = $f.Length -lt 20000 -and $f.Extension -notin '.png', '.ico', '.jpg', '.pfx', '.msix', '.exe', '.dll', '.zip'
        [void]$sb.AppendLine("--- $rel ($($f.Length) bytes)")
        if ($isText) { [void]$sb.AppendLine((Get-Content -Raw -LiteralPath $f.FullName)) }
        if ($sb.Length -gt $MaxChars) { [void]$sb.AppendLine('... (truncated)'); break }
    }
    return $sb.ToString()
}

function New-JudgePrompt {
    # One judge request for a batch of answers to the same scenario. Answers are numbered A1..An in
    # the order given; nothing about the answering model, candidate, or configuration is included.
    param(
        [Parameter(Mandatory)]$Scenario,
        [Parameter(Mandatory)][string[]]$Answers,
        [string]$FixtureText
    )
    $r = $Scenario.Rubric
    if (-not $r) { throw "Scenario '$($Scenario.Id)' has no rubric." }
    if (-not $PSBoundParameters.ContainsKey('FixtureText')) { $FixtureText = Get-FixtureText $Scenario.FixturePath }
    $numbered = { param($items) $i = 0; (@($items) | ForEach-Object { $i++; "$i. $_" }) -join "`n" }
    $i = 0
    $answerText = (@($Answers) | ForEach-Object { $i++; "=== ANSWER A$i ===`n$($_.Trim())`n=== END A$i ===`n" }) -join "`n"
    $alt = @($r.acceptable_alternatives | Where-Object { $_ })
    $script:JudgeTemplate.Replace('{PROMPT}', $Scenario.Prompt).Replace('{FIXTURE}', $FixtureText).
        Replace('{GOAL}', [string]$r.goal).Replace('{MUST}', (& $numbered $r.must_include)).Replace('{MUSTNOT}', (& $numbered $r.must_not)).
        Replace('{ALT}', $(if ($alt) { & $numbered $alt } else { '(none listed)' })).
        Replace('{SOLVED}', [string]$r.solved).Replace('{PARTIAL}', [string]$r.partial).Replace('{ANSWERS}', $answerText)
}

function ConvertFrom-JudgeReply {
    # Extracts the verdict array from a judge's reply. Valid only when it has exactly one valid
    # verdict for each of A1..A<Count>; otherwise Verdicts is $null and Error says why.
    param([AllowNull()][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][int]$Count)
    $m = [regex]::Match([string]$Text, '(?s)\[\s*\{.*\}\s*\]')
    if (-not $m.Success) {
        $t = [string]$Text
        return [pscustomobject]@{ Verdicts = $null; Error = "no JSON array in reply: $($t.Substring(0, [Math]::Min(200, $t.Length)))" }
    }
    try { $arr = @($m.Value | ConvertFrom-Json) }
    catch { return [pscustomobject]@{ Verdicts = $null; Error = "json parse: $($_.Exception.Message)" } }
    $want = @(1..$Count | ForEach-Object { "A$_" })
    $byId = @{}
    foreach ($v in $arr) {
        $idProp = $v.PSObject.Properties['id']
        $verdictProp = $v.PSObject.Properties['verdict']
        if (-not $idProp -or -not $verdictProp) { return [pscustomobject]@{ Verdicts = $null; Error = 'a verdict object lacks id or verdict' } }
        $id = [string]$idProp.Value
        if ($id -notin $want) { return [pscustomobject]@{ Verdicts = $null; Error = "unexpected id '$id'" } }
        if ($byId.ContainsKey($id)) { return [pscustomobject]@{ Verdicts = $null; Error = "duplicate id '$id'" } }
        if ($null -eq (Get-VerdictScore ([string]$verdictProp.Value))) { return [pscustomobject]@{ Verdicts = $null; Error = "invalid verdict '$($verdictProp.Value)' for $id" } }
        $byId[$id] = $v
    }
    $missing = @($want | Where-Object { -not $byId.ContainsKey($_) })
    if ($missing) { return [pscustomobject]@{ Verdicts = $null; Error = "missing ids: $($missing -join ',')" } }
    $verdicts = foreach ($id in $want) {
        $v = $byId[$id]
        [pscustomobject]@{
            Id              = $id
            Verdict         = [string]$v.verdict
            MustIncludeMet  = @(if ($v.PSObject.Properties['must_include_met']) { $v.must_include_met })
            MustNotViolated = @(if ($v.PSObject.Properties['must_not_violated']) { $v.must_not_violated })
            Rationale       = if ($v.PSObject.Properties['rationale']) { [string]$v.rationale } else { '' }
        }
    }
    [pscustomobject]@{ Verdicts = @($verdicts); Error = $null }
}

function New-JudgeBatch {
    # Groups answers that still need a verdict from each judge into batches of one scenario each,
    # shuffled with a fixed seed so the order inside a batch is not the result-folder order.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Answers,
        [Parameter(Mandatory)][string[]]$Judges,
        [hashtable]$Done = @{},
        [int]$BatchSize = 6,
        [int]$Seed = 20261008
    )
    $rng = [System.Random]::new($Seed)
    foreach ($judge in $Judges) {
        $pending = @($Answers | Where-Object { $_.Response.Trim() -and -not $Done.ContainsKey("$($_.Key)|$judge") })
        foreach ($g in ($pending | Group-Object Scenario | Sort-Object Name)) {
            $items = @($g.Group | Sort-Object Key | Sort-Object { $rng.Next() })
            for ($i = 0; $i -lt $items.Count; $i += $BatchSize) {
                [pscustomobject]@{
                    Id       = "$($g.Name)-$judge-$($i / $BatchSize)"
                    Judge    = $judge
                    Scenario = $g.Name
                    Items    = @($items[$i..([Math]::Min($i + $BatchSize, $items.Count) - 1)])
                }
            }
        }
    }
}

function Get-OutcomeScore {
    # Mean verdict score across judges; $null unless every judge graded the answer.
    param([Parameter(Mandatory)][hashtable]$Verdicts, [Parameter(Mandatory)][string[]]$Judges)
    $scores = @(foreach ($j in $Judges) { if ($Verdicts.ContainsKey($j)) { Get-VerdictScore $Verdicts[$j] } })
    if (-not $Judges.Count -or $scores.Count -ne $Judges.Count -or @($scores | Where-Object { $null -eq $_ }).Count) { return $null }
    return ($scores | Measure-Object -Average).Average
}

function Get-ClusterWeightedMean {
    # Mean score per cluster, weighted by each cluster's demand share. Clusters without a share
    # are left out. Returns $null when nothing is weighted.
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs, [Parameter(Mandatory)][hashtable]$Share)
    $num = 0.0; $den = 0.0
    foreach ($g in (@($Runs | Where-Object { $null -ne $_.Score }) | Group-Object Cluster)) {
        if (-not $Share.ContainsKey([string]$g.Name)) { continue }
        $w = [double]$Share[[string]$g.Name]
        $num += $w * ($g.Group | Measure-Object Score -Average).Average
        $den += $w
    }
    if ($den) { return $num / $den }
    return $null
}

function Get-PairedBootstrap {
    # Candidate minus baseline score, paired by (scenario, model, iteration). The 95% interval
    # resamples scenarios (not runs), since runs of one scenario are correlated.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Baseline,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Candidate,
        [int]$Iterations = 2000,
        [int]$Seed = 7
    )
    $base = @{}
    foreach ($r in $Baseline) { if ($null -ne $r.Score) { $base["$($r.Scenario)|$($r.Model)|$($r.Iter)"] = $r.Score } }
    $pairs = @(foreach ($r in $Candidate) {
            $k = "$($r.Scenario)|$($r.Model)|$($r.Iter)"
            if ($null -ne $r.Score -and $base.ContainsKey($k)) { [pscustomobject]@{ S = $r.Base; D = [double]$r.Score - [double]$base[$k] } }
        })
    if (-not $pairs) { return $null }
    $byScenario = @($pairs | Group-Object S | ForEach-Object { , [double[]]@($_.Group | ForEach-Object D) })
    $rng = [System.Random]::new($Seed)
    $means = [double[]]::new($Iterations)
    for ($b = 0; $b -lt $Iterations; $b++) {
        $tot = 0.0; $cnt = 0
        for ($k = 0; $k -lt $byScenario.Count; $k++) { foreach ($d in $byScenario[$rng.Next($byScenario.Count)]) { $tot += $d; $cnt++ } }
        $means[$b] = $tot / $cnt
    }
    [Array]::Sort($means)
    [pscustomobject]@{
        N      = $pairs.Count
        Mean   = ($pairs | Measure-Object D -Average).Average
        Lo     = $means[[int][Math]::Floor(0.025 * $Iterations)]
        Hi     = $means[[int][Math]::Ceiling(0.975 * $Iterations) - 1]
        Wins   = @($pairs | Where-Object D -gt 0).Count
        Losses = @($pairs | Where-Object D -lt 0).Count
    }
}

function Get-JudgeAgreement {
    # Agreement between two judges over answers both graded: exact agreement, Cohen's kappa, the
    # share within one verdict step, and the confusion matrix (rows: first judge).
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Pairs)
    $cats = 'solved', 'partial', 'unsolved'
    $n = @($Pairs).Count
    if (-not $n) { return $null }
    $matrix = [ordered]@{}
    foreach ($a in $cats) { $matrix[$a] = [ordered]@{}; foreach ($b in $cats) { $matrix[$a][$b] = 0 } }
    foreach ($p in $Pairs) { $matrix[$p.A][$p.B]++ }
    $agree = 0; foreach ($c in $cats) { $agree += $matrix[$c][$c] }
    $po = $agree / [double]$n
    $pe = 0.0
    foreach ($c in $cats) {
        $rowA = 0; $colB = 0
        foreach ($x in $cats) { $rowA += $matrix[$c][$x]; $colB += $matrix[$x][$c] }
        $pe += ($rowA / [double]$n) * ($colB / [double]$n)
    }
    $within = @($Pairs | Where-Object { [Math]::Abs((Get-VerdictScore $_.A) - (Get-VerdictScore $_.B)) -le 0.5 }).Count
    [pscustomobject]@{
        N           = $n
        Exact       = $po
        Kappa       = if ($pe -lt 1) { ($po - $pe) / (1 - $pe) } else { $null }
        WithinOne   = $within / [double]$n
        Matrix      = $matrix
    }
}

Export-ModuleMember -Function Get-DefaultJudge, Get-VerdictScore, Get-FixtureText, New-JudgePrompt, ConvertFrom-JudgeReply, New-JudgeBatch,
    Get-OutcomeScore, Get-ClusterWeightedMean, Get-PairedBootstrap, Get-JudgeAgreement
