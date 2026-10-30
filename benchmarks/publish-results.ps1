<#
.SYNOPSIS
  Collects trial result files from a benchmark run, writes a per-release index.json, and
  (only with -OpenPr) opens a pull request that adds them to the `benchmark-data` branch.
.DESCRIPTION
  Benchmark runs only write files on the machine that ran them. This script is the one
  deliberate step that can publish them, and it is meant to be run by hand after a release
  is chosen. See benchmarks\RESULTS-FORMAT.md, "Publishing a release".

  Without -OpenPr it never touches git or GitHub: it stages the release folder under -OutDir
  and prints a summary, so anyone can preview a run.

  With -OpenPr it refuses unless the results are for a real release run from a clean checkout
  of the commit the results were measured on. It then pushes a `results/<release>` branch and
  opens a pull request into `benchmark-data` (never `main`). The merge is the publish.
.EXAMPLE
  pwsh benchmarks\publish-results.ps1 -Path .\artifacts\bench            # preview only
.EXAMPLE
  pwsh benchmarks\publish-results.ps1 -Path .\artifacts\bench -OpenPr    # release owner
#>
[CmdletBinding()]
param(
    # Folder to search (recursively) for trial files from the `tasks` and `apps` harnesses.
    [Parameter(Mandatory)][string]$Path,
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) "winapp-bench-publish-$PID"),
    [switch]$OpenPr,
    [string]$DataBranch = 'benchmark-data'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()

# 1. Find trial files. Other JSON in the run folders (results.json, runs.jsonl) is ignored.
$candidates = @(Get-ChildItem -LiteralPath $Path -Filter *.json -File -Recurse | Where-Object {
        $_.Name -eq 'trial-result.json' -or $_.Directory.Name -eq 'trials' -or $_.Name -like '*__*__*__*.json'
    })
$trials = foreach ($f in $candidates) {
    $t = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    if ($t.schemaVersion -eq 1 -and $t.benchmark) { [pscustomobject]@{ Source = $f.FullName; Trial = $t } }
}
if (-not $trials) { throw "No trial result files found under $Path." }

$releases = @($trials.Trial.release | Select-Object -Unique)
$commits = @($trials.Trial.commit | Select-Object -Unique)
if ($releases.Count -ne 1) { throw "Results mix releases ($($releases -join ', ')). Publish one release at a time." }
if ($commits.Count -ne 1) { throw "Results were measured on $($commits.Count) different commits ($($commits -join ', ')). A release run must use one commit." }
$release = $releases[0]; $commit = $commits[0]

# 2. Stage <OutDir>\<release>\ with the canonical file names.
$stage = Join-Path $OutDir $release
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$seen = @{}
foreach ($x in $trials) {
    $t = $x.Trial
    $name = '{0}__{1}__{2}__{3}.json' -f $t.scenario, $t.arm, $t.model, $t.trial
    if ($seen.ContainsKey($name)) { throw "Two files describe the same trial ($name): $($seen[$name]) and $($x.Source)." }
    $seen[$name] = $x.Source
    Copy-Item -LiteralPath $x.Source -Destination (Join-Path $stage $name)
}
& (Join-Path $PSScriptRoot 'validate-results.ps1') -Path $stage
if ($LASTEXITCODE -ne 0) { throw 'Validation failed; nothing was published.' }

# 3. index.json: one row per trial and one summary per scenario/arm/model, so a reader
#    needs one request, not hundreds.
function Get-Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if (-not $v) { return $null }
    $mid = [int][math]::Floor($v.Count / 2)
    if ($v.Count % 2) { $v[$mid] } else { [int64](($v[$mid - 1] + $v[$mid]) / 2) }
}
function Sum-Counts($items, $key, $count) {
    $totals = [ordered]@{}
    foreach ($i in $items) { $totals[[string]$i.$key] = ($totals[[string]$i.$key] ?? 0) + [int]($i.$count ?? 1) }
    @($totals.Keys | Sort-Object | ForEach-Object { [ordered]@{ name = $_; count = $totals[$_] } })
}

$rows = @($trials.Trial | Sort-Object benchmark, scenario, arm, model, trial | ForEach-Object {
        [ordered]@{
            file = '{0}__{1}__{2}__{3}.json' -f $_.scenario, $_.arm, $_.model, $_.trial
            benchmark = $_.benchmark; scenario = $_.scenario; set = $_.set; arm = $_.arm; model = $_.model; trial = $_.trial
            outcome = $_.outcome; passed = $_.passed; improvements = $_.improvements
            nanoAiu = $_.usage.nanoAiu; wallClockMs = $_.usage.wallClockMs
        }
    })
$groups = @($trials.Trial | Group-Object { '{0}|{1}|{2}|{3}|{4}' -f $_.benchmark, $_.scenario, $_.set, $_.arm, $_.model } | ForEach-Object {
        $g = @($_.Group); $first = $g[0]
        [ordered]@{
            benchmark = $first.benchmark; scenario = $first.scenario; set = $first.set; arm = $first.arm; model = $first.model
            trials = $g.Count
            validTrials = @($g | Where-Object outcome -eq 'valid').Count
            passed = @($g | Where-Object passed).Count
            improvementsMet = ($g | ForEach-Object { $_.improvements.met } | Measure-Object -Sum).Sum
            improvementsTotal = ($g | ForEach-Object { $_.improvements.total } | Measure-Object -Sum).Sum
            medianNanoAiu = Get-Median ($g | ForEach-Object { $_.usage.nanoAiu })
            medianWallClockMs = Get-Median ($g | ForEach-Object { $_.usage.wallClockMs })
            skills = Sum-Counts @($g | ForEach-Object { $_.skills } | Where-Object { $_ }) 'name' 'count'
            winappCommands = Sum-Counts @($g | ForEach-Object { $_.winappCommands } | Where-Object { $_ }) 'command' 'count'
        }
    } | Sort-Object { $_.benchmark }, { $_.scenario }, { $_.arm }, { $_.model })

$index = [ordered]@{
    schemaVersion = 1
    release = $release
    commit = $commit
    generatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    trialCount = $rows.Count
    groups = $groups
    trials = $rows
}
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'index.json') -Encoding utf8

Write-Host "Staged $($rows.Count) trial(s) in $($groups.Count) group(s) for release $release at $($commit.Substring(0, 8))"
Write-Host "Folder: $stage"
if (-not $OpenPr) { Write-Host 'Preview only. Nothing was pushed. Use -OpenPr to open a pull request.'; return }

# 4. Guards for the one run that is allowed to become public.
if ($release -notmatch '^v\d+\.\d+\.\d+$') { throw "Release is '$release'. Only vX.Y.Z results can be published; dev runs stay local." }
if (@(& git -C $repoRoot status --porcelain).Count) { throw 'The working tree has uncommitted changes. Publish from a clean checkout of the release commit.' }
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($head -ne $commit) { throw "Checked out commit is $($head.Substring(0, 8)) but the results were measured on $($commit.Substring(0, 8)). Check out that commit first." }

# 5. Branch off benchmark-data, add the folder, open the PR.
& git -C $repoRoot fetch origin $DataBranch 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "origin/$DataBranch does not exist. A maintainer must create it first (see RESULTS-FORMAT.md)." }
if (& git -C $repoRoot ls-tree --name-only "origin/$DataBranch" "$release/") { throw "$release is already on $DataBranch. Published releases are not overwritten." }

$branch = "results/$release"
$wt = Join-Path ([IO.Path]::GetTempPath()) "winapp-bench-wt-$PID"
& git -C $repoRoot worktree add -b $branch $wt "origin/$DataBranch" | Out-Null
try {
    Copy-Item -LiteralPath $stage -Destination (Join-Path $wt $release) -Recurse
    & git -C $wt add -- $release
    & git -C $wt commit -q -m "Add benchmark results for $release" -m "Measured on $commit." -m 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
    & git -C $wt push -u origin $branch
    if ($LASTEXITCODE -ne 0) { throw "Could not push $branch." }
    & gh pr create --base $DataBranch --head $branch --title "Benchmark results for $release" --body "Results for $release, measured on commit ``$commit``: $($rows.Count) trials in $($groups.Count) scenario/arm/model groups. Merging publishes them. Files were validated against benchmarks/results.schema.json."
}
finally {
    & git -C $repoRoot worktree remove --force $wt
}
