#Requires -Version 7.4
<#
.SYNOPSIS
    Demand-set analysis: joins run records with judge verdicts and rubrics, and writes markdown tables.

.DESCRIPTION
    -Spec is a JSON file mapping a label for each plugin candidate to its result folders (relative
    to the spec file's folder). The -Baseline label (default: the first one) is compared against
    every other label:

        { "base": ["dm-base-sonnet-i1", "dm-base-sonnet-i2"], "cand": ["dm-cand-sonnet-i1", "dm-cand-sonnet-i2"], "none": ["dm-none-sonnet-i1"] }

    Runs are paired by scenario, model, and iteration. A folder name ending in -i<n> sets the
    iteration; otherwise the run's own iteration is used, so give repeated single-iteration runs
    distinct -i<n> folder names.

.EXAMPLE
    pwsh benchmarks\agents\demand-analyze.ps1 -Spec benchmarks\agents\results\spec.json -Judgments benchmarks\agents\results\dm-judge\judgments.jsonl -Clusters benchmarks\agents\demand-meta\clusters.json -Out benchmarks\agents\results\demand.md
#>
param(
    [Parameter(Mandatory)][string]$Spec,
    [Parameter(Mandatory)][string[]]$Judgments,
    [string]$Clusters,
    [string]$Out,
    [string]$Calibration,
    [string]$Export,
    [string]$Baseline,
    [string]$Title = 'Demand set',
    [int]$Boot = 2000
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Benchmark.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\Outcome.psm1') -Force
$Judgments = @(Split-ListArgument $Judgments | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
$specPath = (Resolve-Path -LiteralPath $Spec).Path
$scen = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot 'scenarios') | Where-Object Set -eq 'demand'
$byId = @{}; foreach ($s in $scen) { $byId[$s.Id] = $s }
$rubrics = @{}
foreach ($s in $scen) { $rubrics[$s.BaseId] = $s.Rubric }
$share = @{}
$clusterName = @{}
if ($Clusters) { foreach ($c in (Get-Content -Raw $Clusters | ConvertFrom-Json)) { $share[[string]$c.id] = [double]$c.share_percent; $clusterName[[string]$c.id] = $c.name } }

$verd = @{}
foreach ($jp in $Judgments) {
    foreach ($line in [System.IO.File]::ReadLines($jp)) {
        if (-not $line.Trim()) { continue }
        $j = $line | ConvertFrom-Json
        $verd["$($j.key)|$($j.judge)"] = $j
    }
}
$judges = @($verd.Values | ForEach-Object judge | Sort-Object -Unique)
$specObj = Get-Content -Raw -LiteralPath $specPath | ConvertFrom-Json -AsHashtable
if (-not $specObj.Count) { throw "Spec $specPath names no candidates." }
if (-not $Baseline) { $Baseline = @($specObj.Keys)[0] }
if ($Baseline -notin $specObj.Keys) { throw "Baseline '$Baseline' is not a label in $specPath ($(@($specObj.Keys) -join ', '))." }
function GetV($r, $n) { $p = $r.PSObject.Properties[$n]; if ($p) { $p.Value } else { $null } }

$runs = foreach ($cand in $specObj.Keys) {
    foreach ($d in @($specObj[$cand])) {
        $full = [System.IO.Path]::GetFullPath($d, (Split-Path $specPath))
        $folder = Split-Path $full -Leaf
        $p = Join-Path $full 'runs.jsonl'
        if (-not (Test-Path $p)) { Write-Warning "missing $p"; continue }
        $n = 0
        foreach ($line in [System.IO.File]::ReadLines($p)) {
            $n++
            if (-not $line.Trim()) { continue }
            $rec = $line | ConvertFrom-Json -Depth 64
            $s = $byId[$rec.scenario]; if (-not $s) { continue }
            $key = "$folder#$n"
            $rb = $rubrics[$s.BaseId]
            $vs = @{}; foreach ($jm in $judges) { $v = $verd["$key|$jm"]; if ($v) { $vs[$jm] = $v.verdict } }
            $resp = [string](GetV $rec 'finalResponse')
            $iter = if ($d -match '-i(\d+)$') { [int]$Matches[1] } else { [int]$rec.iteration }
            [pscustomobject]@{
                Cand = $cand; Key = $key; Scenario = $rec.scenario; Base = $s.BaseId; Config = $rec.configuration; Model = $rec.model; Iter = $iter
                Agent = GetV $rec 'agent'; Status = $rec.status
                Cluster = [string]$rb.cluster; Framework = [string]$rb.framework
                Verdicts = $vs; Score = Get-OutcomeScore -Verdicts $vs -Judges $judges
                BothSolved = $(if ($vs.Count -eq $judges.Count) { -not @($vs.Values | Where-Object { $_ -ne 'solved' }) } else { $null })
                WinappInAnswer = $resp -match '(?i)\bwinapp\b'
                WinappTried = [bool]@(GetV $rec 'winappCommandsDenied').Where({ $_ }).Count
                Skills = @(GetV $rec 'skillsLoaded' | Where-Object { $_ }); Files = @(GetV $rec 'skillFilesRead' | Where-Object { $_ })
                Ctx = GetV $rec 'skillContextTokensApprox'; FileTok = GetV $rec 'skillFileTokensApprox'; Credits = GetV $rec 'aiCredits'
                Response = $resp
            }
        }
    }
}
$runs = @($runs)
$graded = @($runs | Where-Object { $null -ne $_.Score })
$cands = @($specObj.Keys)
$models = @($runs | ForEach-Object Model | Sort-Object -Unique)
$sb = [System.Text.StringBuilder]::new()
function W([string]$t = '') { [void]$sb.AppendLine($t) }
function Mean($v) { $x = @($v | Where-Object { $null -ne $_ }); if ($x.Count) { ($x | Measure-Object -Average).Average } else { $null } }
function F($x, $fmt = '{0:N2}') { if ($null -eq $x) { '-' } else { $fmt -f $x } }
function Pct($n, $d) { if (-not $d) { '-' } else { '{0:0}%' -f (100.0 * $n / $d) } }
function Weighted($rs) { if ($share.Count) { Get-ClusterWeightedMean -Runs @($rs) -Share $share } }
function Boot-Diff($base, $cand) { Get-PairedBootstrap -Baseline @($base) -Candidate @($cand) -Iterations $Boot }

W "# $Title"; W
W "Outcome = LLM-judge verdict against the scenario's frozen rubric (solved 1, partial 0.5, unsolved 0), averaged over judges ($($judges -join ', ')). Every answer is graded by every judge. Scenarios: $(@($scen | ForEach-Object BaseId | Sort-Object -Unique).Count). Runs graded: $($graded.Count) of $($runs.Count)."; W

foreach ($cfg in @($runs | ForEach-Object Config | Sort-Object -Unique)) {
    $rc = @($graded | Where-Object Config -eq $cfg)
    W "## Configuration: $cfg"; W
    W "### Outcome by candidate and model"; W
    W ('| Candidate | Model | runs | mean score | cluster-weighted | both judges solved | ' + (($judges | ForEach-Object { "solved ($_)" }) -join ' | ') + ' | names winapp | loaded nothing | skill ctx | ref tok | credits/run |')
    W ('|---|---|---:|---:|---:|---:|' + (($judges | ForEach-Object { '---:' }) -join '|') + '|---:|---:|---:|---:|---:|')
    foreach ($c in $cands) {
        foreach ($m in @('ALL') + $models) {
            $r = @($rc | Where-Object { $_.Cand -eq $c -and ($m -eq 'ALL' -or $_.Model -eq $m) }); if (-not $r) { continue }
            $js = foreach ($jm in $judges) { Pct @($r | Where-Object { $_.Verdicts[$jm] -eq 'solved' }).Count $r.Count }
            $label = if ($m -eq 'ALL') { "**$c**" } else { $c }
            W "| $label | $m | $($r.Count) | $(F (Mean ($r | ForEach-Object Score))) | $(F (Weighted $r)) | $(Pct @($r | Where-Object BothSolved).Count $r.Count) | $($js -join ' | ') | $(Pct @($r | Where-Object WinappInAnswer).Count $r.Count) | $(Pct @($r | Where-Object { $_.Skills.Count -eq 0 }).Count $r.Count) | $(F (Mean ($r | ForEach-Object Ctx)) '{0:N0}') | $(F (Mean ($r | ForEach-Object FileTok)) '{0:N0}') | $(F (Mean ($r | ForEach-Object Credits)) '{0:N1}') |"
        }
    }
    W
    $base = @($rc | Where-Object Cand -eq $Baseline)
    if ($base -and @($cands | Where-Object { $_ -ne $Baseline })) {
        W "### Paired difference vs $Baseline (same scenario, model, iteration; 95% bootstrap CI over scenarios)"; W
        W '| Candidate | Model | pairs | mean diff | 95% CI | better | worse |'; W '|---|---|---:|---:|---|---:|---:|'
        foreach ($c in $cands | Where-Object { $_ -ne $Baseline }) {
            foreach ($m in @('ALL') + $models) {
                $x = Boot-Diff @($base | Where-Object { $m -eq 'ALL' -or $_.Model -eq $m }) @($rc | Where-Object { $_.Cand -eq $c -and ($m -eq 'ALL' -or $_.Model -eq $m) })
                if ($x) { W "| $c | $m | $($x.N) | $(F $x.Mean '{0:+0.000;-0.000;0.000}') | [$(F $x.Lo '{0:+0.000;-0.000;0.000}'), $(F $x.Hi '{0:+0.000;-0.000;0.000}')] | $($x.Wins) | $($x.Losses) |" }
            }
        }
        W
    }
    W "### Mean score by cluster (sorted by demand share)"; W
    $clIds = @($rc | ForEach-Object Cluster | Sort-Object -Unique | Sort-Object { - [double]($share[$_] ?? 0) })
    W ('| Cluster | share % | scenarios | ' + ($cands -join ' | ') + ' |'); W ('|---|---:|---:|' + (($cands | ForEach-Object { '---:' }) -join '|') + '|')
    foreach ($cl in $clIds) {
        $cells = foreach ($c in $cands) { F (Mean ($rc | Where-Object { $_.Cand -eq $c -and $_.Cluster -eq $cl } | ForEach-Object Score)) }
        $nm = if ($clusterName[$cl]) { "$cl ($($clusterName[$cl]))" } else { $cl }
        W "| $nm | $(F $share[$cl] '{0:N1}') | $(@($rc | Where-Object Cluster -eq $cl | ForEach-Object Base | Sort-Object -Unique).Count) | $($cells -join ' | ') |"
    }
    W
    W "### Scenario detail (mean score per candidate)"; W
    W ('| Scenario | cluster | framework | ' + ($cands -join ' | ') + ' |'); W ('|---|---|---|' + (($cands | ForEach-Object { '---:' }) -join '|') + '|')
    foreach ($b in @($rc | ForEach-Object Base | Sort-Object -Unique)) {
        $r0 = $rc | Where-Object Base -eq $b | Select-Object -First 1
        $cells = foreach ($c in $cands) { F (Mean ($rc | Where-Object { $_.Cand -eq $c -and $_.Base -eq $b } | ForEach-Object Score)) }
        W "| $b | $($r0.Cluster) | $($r0.Framework) | $($cells -join ' | ') |"
    }
    W
}

W "## Judge agreement"; W
if ($judges.Count -ge 2) {
    $a = $judges[0]; $b = $judges[1]
    $both = @($graded | Where-Object { $_.Verdicts[$a] -and $_.Verdicts[$b] })
    $ag = Get-JudgeAgreement -Pairs @($both | ForEach-Object { [pscustomobject]@{ A = $_.Verdicts[$a]; B = $_.Verdicts[$b] } })
    if ($ag) {
        W "Answers graded by both: $($ag.N). Exact agreement $(Pct ($ag.Exact * $ag.N) $ag.N); Cohen's kappa $(F $ag.Kappa); within one step $(Pct ($ag.WithinOne * $ag.N) $ag.N)."; W
        W "| $a ↓ / $b → | solved | partial | unsolved |"; W '|---|---:|---:|---:|'
        foreach ($x in $ag.Matrix.Keys) { W "| $x | $(@($ag.Matrix[$x].Values) -join ' | ') |" }
        W
        W "Self-family check: mean score each judge gives to each answering model's answers (all candidates)."; W
        W ('| Answering model | ' + (($judges | ForEach-Object { "judge $_" }) -join ' | ') + ' |'); W ('|---|' + (($judges | ForEach-Object { '---:' }) -join '|') + '|')
        foreach ($m in $models) { W "| $m | $(($judges | ForEach-Object { $jm = $_; F (Mean ($both | Where-Object Model -eq $m | ForEach-Object { Get-VerdictScore $_.Verdicts[$jm] })) }) -join ' | ') |" }
        W
    }
}

W "## winapp usage vs outcome (plugin configurations)"; W
W '| Candidate | answers naming winapp | mean score when named | mean score when not named | tried a winapp command (denied) |'; W '|---|---:|---:|---:|---:|'
foreach ($c in $cands) {
    $r = @($graded | Where-Object Cand -eq $c)
    W "| $c | $(Pct @($r | Where-Object WinappInAnswer).Count $r.Count) | $(F (Mean ($r | Where-Object WinappInAnswer | ForEach-Object Score))) | $(F (Mean ($r | Where-Object { -not $_.WinappInAnswer } | ForEach-Object Score))) | $(Pct @($r | Where-Object WinappTried).Count $r.Count) |"
}
W
W "## Routing diagnostics: skills loaded (top 12 per candidate) and reference reads"; W
foreach ($c in $cands) {
    $r = @($runs | Where-Object Cand -eq $c)
    $top = @($r | ForEach-Object { $_.Skills } | Group-Object | Sort-Object Count -Descending | Select-Object -First 12 | ForEach-Object { "$($_.Name) ($($_.Count))" })
    W "- **$c**: runs $($r.Count); loaded nothing $(Pct @($r | Where-Object { $_.Skills.Count -eq 0 }).Count $r.Count); runs reading references $(Pct @($r | Where-Object { $_.Files.Count -gt 0 }).Count $r.Count). $($top -join ', ')"
}
W
W "## Score by whether any skill loaded (diagnostic)"; W
W '| Candidate | score, a skill loaded | score, nothing loaded |'; W '|---|---:|---:|'
foreach ($c in $cands) { $r = @($graded | Where-Object Cand -eq $c); W "| $c | $(F (Mean ($r | Where-Object { $_.Skills.Count } | ForEach-Object Score))) ($(@($r | Where-Object { $_.Skills.Count }).Count)) | $(F (Mean ($r | Where-Object { -not $_.Skills.Count } | ForEach-Object Score))) ($(@($r | Where-Object { -not $_.Skills.Count }).Count)) |" }
W
W "## Credits"; W
W '| Candidate | runs | credits | mean/run |'; W '|---|---:|---:|---:|'
foreach ($c in $cands) { $r = @($runs | Where-Object Cand -eq $c); $sum = ($r | Where-Object { $null -ne $_.Credits } | Measure-Object Credits -Sum).Sum; W "| $c | $($r.Count) | $(F $sum '{0:N0}') | $(F (Mean ($r | ForEach-Object Credits)) '{0:N1}') |" }

$text = $sb.ToString()
if ($Out) { Set-Content -Path $Out -Value $text -Encoding utf8NoBOM; Write-Host "wrote $Out" } else { $text }

if ($Calibration) {
    $rngC = [System.Random]::new(42); $pick = @($graded | Sort-Object Key | Sort-Object { $rngC.Next() } | Select-Object -First 15)
    $cb = [System.Text.StringBuilder]::new()
    [void]$cb.AppendLine("# Judge calibration sample (15 random graded answers)`n")
    [void]$cb.AppendLine("Pseudo-random sample across all candidates and configurations. Spot-check: does each verdict match the rubric? Candidate and model are shown here but were hidden from the judges.`n")
    $i = 0
    foreach ($p in $pick) {
        $i++; $rb = $rubrics[$p.Base]
        [void]$cb.AppendLine("## $i. $($p.Scenario) — $($p.Cand), $($p.Model), config $($p.Config)`n")
        [void]$cb.AppendLine("**Prompt:** $($byId[$p.Scenario].Prompt)`n")
        [void]$cb.AppendLine("**Rubric goal:** $($rb.goal)`n`n**Must include:**`n$((@($rb.must_include) | ForEach-Object { "- $_" }) -join "`n")`n`n**Must not:**`n$((@($rb.must_not) | ForEach-Object { "- $_" }) -join "`n")`n`n**Acceptable alternatives:** $((@($rb.acceptable_alternatives)) -join '; ')`n")
        foreach ($jm in $judges) { $v = $verd["$($p.Key)|$jm"]; [void]$cb.AppendLine("**$jm verdict:** $($v.verdict) — met $(@($v.mustIncludeMet) -join ','); violated $(@($v.mustNotViolated) -join ','); $($v.rationale)`n") }
        [void]$cb.AppendLine("<details><summary>Answer</summary>`n`n$($p.Response.Trim())`n`n</details>`n")
    }
    Set-Content -Path $Calibration -Value $cb.ToString() -Encoding utf8NoBOM; Write-Host "wrote $Calibration"
}
if ($Export) { $runs | Select-Object Cand, Key, Scenario, Base, Config, Model, Iter, Cluster, Framework, Score, BothSolved, WinappInAnswer, @{n = 'Skills'; e = { $_.Skills -join ',' } }, @{n = 'Files'; e = { $_.Files -join ',' } }, Ctx, Credits, @{n = 'V'; e = { ($_.Verdicts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';' } } | ConvertTo-Csv -NoTypeInformation | Set-Content $Export }

