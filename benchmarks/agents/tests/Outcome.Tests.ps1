#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\Benchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\Outcome.psm1') -Force

    function New-Rubric([hashtable]$Override = @{}) {
        $r = [ordered]@{
            cluster = 'c07'; framework = 'WPF'; goal = 'Sign the package.'
            must_include = @('Says the Publisher must equal the certificate subject.', 'Gives the signtool command.')
            must_not = @('Suggests -AllowUnsigned for production.')
            acceptable_alternatives = @('MSBuild signing properties.')
            solved = 'Both points.'; partial = 'One point.'; sources = @('https://example.com/q/1')
        }
        foreach ($k in $Override.Keys) { $r[$k] = $Override[$k] }
        $r
    }

    function New-RubricScenario([string]$Root, [string]$Id, [string]$Set = 'demand', $Rubric = (New-Rubric)) {
        $dir = Join-Path $Root $Id
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'fixture') | Out-Null
        'x' | Set-Content (Join-Path $dir 'fixture\a.txt')
        @{ id = $Id; description = 'd'; set = $Set; cohort = 'error'; prompt = 'help'; configurations = @('none', 'both'); fixture = 'fixture'
            expect = @{ capabilities = @{ primary = @(); acceptable = @(); forbid = @() } }
        } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'scenario.json')
        if ($Rubric) { $Rubric | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'rubric.json') }
    }

    function New-Run($scenario, $model, $iter, $score, $cluster = 'c1') {
        [pscustomobject]@{ Scenario = $scenario; Base = $scenario; Model = $model; Iter = $iter; Score = $score; Cluster = $cluster }
    }
}

Describe 'Scenario rubrics' {
    It 'loads a rubric next to scenario.json and fills optional lists' {
        $root = Join-Path $TestDrive 'ok'
        New-RubricScenario $root 'c07-sign'
        $s = Get-ScenarioDefinitions -ScenariosRoot $root
        $s.Rubric.cluster | Should -Be 'c07'
        @($s.Rubric.must_include).Count | Should -Be 2
        $s.Rubric.ContainsKey('verified_with') | Should -BeTrue
    }

    It 'requires a rubric for demand scenarios only' {
        $root = Join-Path $TestDrive 'missing'
        New-RubricScenario $root 'dev-one' -Set 'dev' -Rubric $null
        @(Get-ScenarioDefinitions -ScenariosRoot $root)[0].Rubric | Should -BeNullOrEmpty
        New-RubricScenario $root 'demand-one' -Rubric $null
        { Get-ScenarioDefinitions -ScenariosRoot $root } | Should -Throw '*needs a rubric.json*'
    }

    It 'rejects a rubric without <Field>' -ForEach @(
        @{ Field = 'goal'; Value = '' }
        @{ Field = 'must_include'; Value = @() }
        @{ Field = 'must_not'; Value = @(' ') }
        @{ Field = 'sources'; Value = $null }
        @{ Field = 'solved'; Value = $null }
    ) {
        $p = Join-Path $TestDrive "rubric-$Field.json"
        New-Rubric @{ $Field = $Value } | ConvertTo-Json -Depth 5 | Set-Content $p
        { Read-ScenarioRubric -Path $p -ScenarioId 'x' } | Should -Throw "*'$Field'*"
    }

    It 'gives every repo demand scenario a complete rubric' {
        $all = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        $demand = @($all | Where-Object Set -eq 'demand')
        $demand.Count | Should -BeGreaterThan 0
        @($demand | Where-Object { -not $_.Rubric }).Count | Should -Be 0
        # Only explicit-command scenarios may name the tool; every other prompt stays tool-neutral.
        @($demand | Where-Object { $_.Cohort -ne 'explicit-command' -and $_.Prompt -match '(?i)\bwinapp\b' }).Id | Should -BeNullOrEmpty
    }
}

Describe 'New-JudgePrompt' {
    BeforeAll {
        $root = Join-Path $TestDrive 'prompt'
        New-RubricScenario $root 'c07-sign' -Rubric (New-Rubric)
        $script:scenario = Get-ScenarioDefinitions -ScenariosRoot $root
    }

    It 'numbers rubric points and answers, and inlines the fixture' {
        $text = New-JudgePrompt -Scenario $scenario -Answers @(' first ', 'second')
        $text | Should -Match '(?m)^1\. Says the Publisher must equal'
        $text | Should -Match '(?m)^2\. Gives the signtool command\.'
        $text | Should -Match '=== ANSWER A1 ===\nfirst\n=== END A1 ==='
        $text | Should -Match '=== ANSWER A2 ===\nsecond\n'
        $text | Should -Match '--- a\.txt'
        $text | Should -Not -Match '\{(PROMPT|FIXTURE|GOAL|MUST|MUSTNOT|ALT|SOLVED|PARTIAL|ANSWERS)\}'
    }

    It 'says when no alternatives are listed' {
        $s = $scenario.PSObject.Copy()
        $s.Rubric = New-Rubric @{ acceptable_alternatives = @() }
        New-JudgePrompt -Scenario $s -Answers 'a' -FixtureText 'f' | Should -Match '\(none listed\)'
    }

    It 'refuses a scenario without a rubric' {
        $s = $scenario.PSObject.Copy(); $s.Rubric = $null
        { New-JudgePrompt -Scenario $s -Answers 'a' } | Should -Throw '*no rubric*'
    }
}

Describe 'ConvertFrom-JudgeReply' {
    It 'reads a verdict array wrapped in prose and returns it in answer order' {
        $text = @'
Here you go:
```json
[{"id":"A2","verdict":"partial","must_include_met":[1]},{"id":"A1","verdict":"solved","must_include_met":[1,2],"must_not_violated":[],"rationale":"ok"}]
```
'@
        $r = ConvertFrom-JudgeReply -Text $text -Count 2
        $r.Error | Should -BeNullOrEmpty
        $r.Verdicts.Id | Should -Be @('A1', 'A2')
        $r.Verdicts.Verdict | Should -Be @('solved', 'partial')
        $r.Verdicts[0].MustIncludeMet | Should -Be @(1, 2)
        $r.Verdicts[1].Rationale | Should -Be ''
    }

    It 'rejects <Name>' -ForEach @(
        @{ Name = 'a missing answer'; Text = '[{"id":"A1","verdict":"solved"}]'; Err = 'missing ids: A2' }
        @{ Name = 'an unknown id'; Text = '[{"id":"A1","verdict":"solved"},{"id":"A3","verdict":"solved"}]'; Err = "unexpected id 'A3'" }
        @{ Name = 'a duplicate id'; Text = '[{"id":"A1","verdict":"solved"},{"id":"A1","verdict":"partial"}]'; Err = "duplicate id 'A1'" }
        @{ Name = 'an invalid verdict'; Text = '[{"id":"A1","verdict":"mostly"},{"id":"A2","verdict":"solved"}]'; Err = "invalid verdict 'mostly'*" }
        @{ Name = 'a reply without JSON'; Text = 'I cannot grade these.'; Err = 'no JSON array*' }
        @{ Name = 'an empty reply'; Text = ''; Err = 'no JSON array*' }
    ) {
        $r = ConvertFrom-JudgeReply -Text $Text -Count 2
        $r.Verdicts | Should -BeNullOrEmpty
        $r.Error | Should -BeLike $Err
    }
}

Describe 'New-JudgeBatch' {
    BeforeAll {
        $script:answers = @(
            foreach ($i in 1..7) { [pscustomobject]@{ Key = "r#$i"; Scenario = 's1'; Response = "answer $i" } }
            [pscustomobject]@{ Key = 'r#8'; Scenario = 's2'; Response = 'answer 8' }
            [pscustomobject]@{ Key = 'r#9'; Scenario = 's2'; Response = '  ' }
        )
    }

    It 'gives each judge every non-empty answer once, in single-scenario batches' {
        $b = @(New-JudgeBatch -Answers $answers -Judges 'j1', 'j2' -BatchSize 3)
        foreach ($j in 'j1', 'j2') {
            $keys = @($b | Where-Object Judge -eq $j | ForEach-Object { $_.Items.Key })
            ($keys | Sort-Object) | Should -Be @(1..8 | ForEach-Object { "r#$_" })
        }
        @($b | Where-Object { @($_.Items.Scenario | Sort-Object -Unique).Count -ne 1 }).Count | Should -Be 0
        @($b | Where-Object { $_.Items.Count -gt 3 }).Count | Should -Be 0
        @($b.Id | Sort-Object -Unique).Count | Should -Be $b.Count
    }

    It 'skips answers a judge already graded' {
        $b = @(New-JudgeBatch -Answers $answers -Judges 'j1', 'j2' -Done @{ 'r#8|j1' = $true })
        @($b | Where-Object { $_.Judge -eq 'j1' -and $_.Scenario -eq 's2' }).Count | Should -Be 0
        @($b | Where-Object { $_.Judge -eq 'j2' -and $_.Scenario -eq 's2' }).Count | Should -Be 1
    }

    It 'shuffles reproducibly with a seed' {
        $a = (New-JudgeBatch -Answers $answers -Judges 'j' -Seed 1 | ForEach-Object { $_.Items.Key }) -join ','
        $b = (New-JudgeBatch -Answers $answers -Judges 'j' -Seed 1 | ForEach-Object { $_.Items.Key }) -join ','
        $a | Should -Be $b
        $a | Should -Not -Be ((1..8 | ForEach-Object { "r#$_" }) -join ',')
    }
}

Describe 'Outcome scores' {
    It 'maps verdicts to 1, 0.5, and 0' {
        Get-VerdictScore 'solved' | Should -Be 1.0
        Get-VerdictScore 'partial' | Should -Be 0.5
        Get-VerdictScore 'unsolved' | Should -Be 0.0
        Get-VerdictScore 'other' | Should -BeNullOrEmpty
    }

    It 'averages judges and needs every judge' {
        Get-OutcomeScore -Verdicts @{ a = 'solved'; b = 'partial' } -Judges 'a', 'b' | Should -Be 0.75
        Get-OutcomeScore -Verdicts @{ a = 'solved' } -Judges 'a', 'b' | Should -BeNullOrEmpty
        Get-OutcomeScore -Verdicts @{ a = 'solved'; b = 'bogus' } -Judges 'a', 'b' | Should -BeNullOrEmpty
    }

    It 'weights cluster means by demand share and ignores unweighted or ungraded runs' {
        $runs = @(
            New-Run 's1' 'm' 1 1.0 'big'; New-Run 's2' 'm' 1 0.0 'big'   # big: mean 0.5
            New-Run 's3' 'm' 1 1.0 'small'                              # small: mean 1.0
            New-Run 's4' 'm' 1 0.0 'none'                               # no share: ignored
            New-Run 's5' 'm' 1 $null 'small'                            # ungraded: ignored
        )
        Get-ClusterWeightedMean -Runs $runs -Share @{ big = 3.0; small = 1.0 } | Should -Be 0.625
        Get-ClusterWeightedMean -Runs $runs -Share @{ other = 1.0 } | Should -BeNullOrEmpty
    }
}

Describe 'Get-PairedBootstrap' {
    It 'pairs by scenario, model, and iteration and counts wins and losses' {
        $base = @(New-Run 's1' 'm' 1 0.5; New-Run 's2' 'm' 1 1.0; New-Run 's3' 'm' 1 0.0; New-Run 's4' 'm' 1 1.0)
        $cand = @(New-Run 's1' 'm' 1 1.0; New-Run 's2' 'm' 1 0.5; New-Run 's3' 'm' 1 0.0; New-Run 's4' 'm' 2 0.0; New-Run 's5' 'm' 1 1.0)
        $r = Get-PairedBootstrap -Baseline $base -Candidate $cand -Iterations 200
        $r.N | Should -Be 3
        $r.Mean | Should -Be 0
        $r.Wins | Should -Be 1
        $r.Losses | Should -Be 1
        $r.Lo | Should -BeLessOrEqual $r.Mean
        $r.Hi | Should -BeGreaterOrEqual $r.Mean
    }

    It 'gives a zero-width interval when every scenario moves by the same amount' {
        $base = @(1..5 | ForEach-Object { New-Run "s$_" 'm' 1 0.0 })
        $cand = @(1..5 | ForEach-Object { New-Run "s$_" 'm' 1 0.5 })
        $r = Get-PairedBootstrap -Baseline $base -Candidate $cand -Iterations 100
        $r.Mean | Should -Be 0.5
        $r.Lo | Should -Be 0.5
        $r.Hi | Should -Be 0.5
    }

    It 'is reproducible for a seed and returns null with nothing to pair' {
        $base = @(1..8 | ForEach-Object { New-Run "s$_" 'm' 1 0.0 })
        $cand = @(1..8 | ForEach-Object { New-Run "s$_" 'm' 1 ($_ % 3 / 2.0) })
        $a = Get-PairedBootstrap -Baseline $base -Candidate $cand -Seed 3
        $b = Get-PairedBootstrap -Baseline $base -Candidate $cand -Seed 3
        "$($a.Lo),$($a.Hi)" | Should -Be "$($b.Lo),$($b.Hi)"
        $a.Lo | Should -BeLessThan $a.Hi
        Get-PairedBootstrap -Baseline $base -Candidate @(New-Run 'x' 'm' 1 1.0) | Should -BeNullOrEmpty
    }
}

Describe 'Get-JudgeAgreement' {
    It 'computes exact agreement, Cohen''s kappa, and within-one-step agreement' {
        $pairs = @(
            1..4 | ForEach-Object { [pscustomobject]@{ A = 'solved'; B = 'solved' } }
            1..2 | ForEach-Object { [pscustomobject]@{ A = 'solved'; B = 'partial' } }
            1..3 | ForEach-Object { [pscustomobject]@{ A = 'unsolved'; B = 'unsolved' } }
            [pscustomobject]@{ A = 'solved'; B = 'unsolved' }
        )
        $r = Get-JudgeAgreement -Pairs $pairs
        $r.N | Should -Be 10
        $r.Exact | Should -Be 0.7
        # Expected agreement: solved 0.7*0.4 + partial 0*0.2 + unsolved 0.3*0.4 = 0.40.
        [Math]::Round($r.Kappa, 4) | Should -Be 0.5
        $r.WithinOne | Should -Be 0.9
        $r.Matrix['solved']['partial'] | Should -Be 2
        $r.Matrix['unsolved']['unsolved'] | Should -Be 3
    }

    It 'returns null with no pairs and no kappa when both judges give one verdict' {
        Get-JudgeAgreement -Pairs @() | Should -BeNullOrEmpty
        (Get-JudgeAgreement -Pairs @([pscustomobject]@{ A = 'solved'; B = 'solved' })).Kappa | Should -BeNullOrEmpty
    }
}
