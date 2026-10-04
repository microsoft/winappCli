#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\Benchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\ScenarioLint.psm1') -Force
    $script:mapPath = Join-Path $TestDrive 'capabilities.json'
    @{
        capabilities = @{
            'msix.sign'     = @{ description = 'sign'; commands = @('^sign$'); answer = @{ require = @(, @('winapp (sign|cert)\b')) } }
            'msix.package'  = @{ description = 'package'; commands = @('^package$'); answer = @{ require = @(, @('winapp package\b')) } }
            'msix.manifest' = @{ description = 'manifest'; commands = @(); answer = @{ require = @(, @('winapp manifest\b', '<uap:Protocol\b')); forbid = @('regedit') } }
            'winui.migrate' = @{ description = 'migrate'; commands = @(); answer = @{ require = @(@('Microsoft\.UI\.Xaml'), @('DispatcherQueue')) } }
            'winui.design'  = @{ description = 'design'; commands = @() }
        }
        maps         = @(@{ id = 'a'; plugin = 'winapp'; source = 't'; skillSetHash = 'x'; skills = @{ 'winapp-signing' = @('msix.sign'); 'winapp-package' = @('msix.package') } })
    } | ConvertTo-Json -Depth 8 | Set-Content $script:mapPath
    $script:map = Read-CapabilityMap -Path $script:mapPath
    function New-Expect($primary = @(), $acceptable = @(), $forbid = @()) {
        [pscustomobject]@{
            SkillsAny = @(); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null; Commands = @()
            Capabilities = [pscustomobject]@{ Primary = @($primary); Acceptable = @($acceptable); Forbid = @($forbid); BudgetTokens = $null }
        }
    }
}

Describe 'Test-AnswerExpectations' {
    It 'passes when the response names the signal of a primary capability' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -Response 'Run `winapp cert generate`, then `winapp sign app.msix cert.pfx`.' -Map $map
        $r.Status | Should -Be 'pass'
        $r.Basis | Should -Be 'response'
    }

    It 'fails when the response names no signal, and is case-insensitive' {
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -Response 'Use signtool.' -Map $map).Status | Should -Be 'fail'
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -Response 'WINAPP SIGN it' -Map $map).Status | Should -Be 'pass'
    }

    It 'needs every require group, and scores some groups as partial' {
        $e = New-Expect -primary 'winui.migrate'
        (Test-AnswerExpectations -Expect $e -Response 'using Microsoft.UI.Xaml; DispatcherQueue.TryEnqueue(...)' -Map $map).Status | Should -Be 'pass'
        (Test-AnswerExpectations -Expect $e -Response 'using Microsoft.UI.Xaml;' -Map $map).Status | Should -Be 'partial'
    }

    It 'needs every signaled capability of a group alternative, and gives partial credit for part of it or for acceptable ones' {
        $e = New-Expect -primary @(, @('msix.package', 'msix.sign'))
        (Test-AnswerExpectations -Expect $e -Response 'winapp package . then winapp sign' -Map $map).Status | Should -Be 'pass'
        (Test-AnswerExpectations -Expect $e -Response 'winapp package .' -Map $map).Status | Should -Be 'partial'
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.package' -acceptable 'msix.sign') -Response 'winapp sign' -Map $map).Status | Should -Be 'partial'
    }

    It 'fails on a forbidden answer pattern' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary 'msix.manifest') -Response 'Add <uap:Protocol Name="x"/> or just use regedit.' -Map $map
        $r.Status | Should -Be 'fail'
        $r.Notes | Should -Match 'forbidden answer pattern'
    }

    It 'is n/a when no primary capability has answer signals, or nothing was recorded' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary 'winui.design') -Response 'anything' -Map $map
        $r.Status | Should -Be 'n/a'
        $r.Notes | Should -Match 'no answer signals for: winui.design'
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -Map $map).Status | Should -Be 'n/a'
    }

    It 'passes near-misses that name no winapp command and fails those that do' {
        $e = New-Expect -forbid '*'
        (Test-AnswerExpectations -Expect $e -Response 'Use argparse.' -Map $map).Status | Should -Be 'pass'
        (Test-AnswerExpectations -Expect $e -Response 'Package it with winapp package first.' -Map $map).Status | Should -Be 'fail'
        (Test-AnswerExpectations -Expect $e -WinappCommands @() -Map $map).Status | Should -Be 'pass'
    }

    It 'falls back to recorded winapp commands and checks only command signal groups' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -WinappCommands @('cert generate') -Map $map
        $r.Status | Should -Be 'pass'
        $r.Basis | Should -Be 'commands'
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -WinappCommands @('package') -Map $map).Status | Should -Be 'fail'
        $m = Test-AnswerExpectations -Expect (New-Expect -primary 'winui.migrate') -WinappCommands @('package') -Map $map
        $m.Status | Should -Be 'n/a'
        $m.Notes | Should -Match 'checkable from commands'
    }

    It 'prefers the response over recorded commands' {
        (Test-AnswerExpectations -Expect (New-Expect -primary 'msix.sign') -Response 'nothing useful' -WinappCommands @('sign') -Map $map).Basis | Should -Be 'response'
    }

    It 'rejects an invalid answer regex' {
        $bad = Join-Path $TestDrive 'bad.json'
        @{ capabilities = @{ a = @{ description = 'a'; commands = @(); answer = @{ require = @(, @('(')) } } }; maps = @() } | ConvertTo-Json -Depth 8 | Set-Content $bad
        { Read-CapabilityMap -Path $bad } | Should -Throw "*invalid answer regex*"
    }
}

Describe 'Final response' {
    It 'records the last non-empty assistant message' {
        $path = Join-Path $TestDrive 'resp.jsonl'
        @(
            '{"type":"assistant.message","data":{"content":"Looking at the project..."}}'
            '{"type":"assistant.message","data":{"content":"Run winapp sign app.msix."}}'
            '{"type":"assistant.message","data":{"content":"  "}}'
        ) | Set-Content $path
        (Read-SessionEvents -Path $path).finalResponse | Should -Be 'Run winapp sign app.msix.'
        (Read-SessionEvents -Path (Join-Path $TestDrive 'missing.jsonl')).finalResponse | Should -BeNullOrEmpty
    }
}

Describe 'Routing and answer side by side' {
    It 'keeps the routing status and adds the answer to scenario evaluation' {
        $r = Test-ScenarioExpectations -Expect (New-Expect -primary 'msix.sign') -LoadedSkills @() -InstalledSkills @('winapp-signing', 'winapp-package') -Response 'winapp sign it' -Map $map
        $r.Status | Should -Be 'fail'
        $r.Answer | Should -Be 'pass'
        $r.AnswerBasis | Should -Be 'response'
    }

    It 'records routing and answer per primary capability' {
        $r = Test-ScenarioExpectations -Expect (New-Expect -primary @('msix.sign', 'winui.design')) -LoadedSkills 'winapp-signing' -InstalledSkills @('winapp-signing', 'winapp-package') -Response 'nothing' -Map $map
        $r.CapabilityResults['msix.sign'].routing | Should -Be 'loaded'
        $r.CapabilityResults['msix.sign'].answer | Should -Be 'missed'
        $r.CapabilityResults['winui.design'].routing | Should -Be 'n/a'
        $r.CapabilityResults['winui.design'].answer | Should -BeNullOrEmpty
    }

    It 'is n/a, not fail, when another primary alternative cannot be checked' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary @('msix.sign', 'winui.design')) -Response 'Use a Grid.' -Map $map
        $r.Status | Should -Be 'n/a'
        (Test-AnswerExpectations -Expect (New-Expect -primary @('msix.sign', 'winui.design')) -Response 'winapp sign' -Map $map).Status | Should -Be 'pass'
    }

    It 'gives at most partial credit to a group alternative with an uncheckable capability' {
        (Test-AnswerExpectations -Expect (New-Expect -primary @(, @('msix.sign', 'winui.design'))) -Response 'winapp sign' -Map $map).Status | Should -Be 'partial'
    }

    It 'does not check groups with patterns recorded commands cannot show' {
        $r = Test-AnswerExpectations -Expect (New-Expect -primary 'msix.manifest') -WinappCommands @('package') -Map $map
        $r.Status | Should -Be 'n/a'
    }

    It 'counts the routing x answer matrix over runs where both are scored' {
        $runs = @(
            [pscustomobject]@{ status = 'pass'; answer = 'pass' }
            [pscustomobject]@{ status = 'pass'; answer = 'fail' }
            [pscustomobject]@{ status = 'fail'; answer = 'pass' }
            [pscustomobject]@{ status = 'partial'; answer = 'partial' }
            [pscustomobject]@{ status = 'n/a'; answer = 'pass' }
            [pscustomobject]@{ status = 'pass'; answer = 'n/a' }
        )
        $m = Get-RoutingAnswerMatrix $runs
        $m.Total | Should -Be 4
        @($m.RoutedAnswered, $m.RoutedOnly, $m.AnsweredOnly, $m.Neither) | Should -Be @(1, 1, 1, 1)
    }

    It 'shows routing and answer by model and capability in the summary, and answers for the control' {
        $runs = Join-Path $TestDrive 'ra.jsonl'
        $base = @{ model = 'm'; reason = ''; tokens = $null; skillContextTokensApprox = 1; aiCredits = 1; durationMs = 1; skillsLoaded = @(); set = 'heldout'; cohort = 'error'; primaryCapabilities = @('msix.sign') }
        @(
            @{ scenario = 's'; configuration = 'both'; status = 'pass'; answer = 'pass'; answerBasis = 'response'; capabilityResults = @{ 'msix.sign' = @{ routing = 'loaded'; answer = 'met' } } }
            @{ scenario = 's'; configuration = 'both'; status = 'fail'; answer = 'pass'; answerBasis = 'response'; capabilityResults = @{ 'msix.sign' = @{ routing = 'missed'; answer = 'met' } } }
            @{ scenario = 's'; configuration = 'both'; status = 'fail'; answer = 'fail'; answerBasis = 'commands'; capabilityResults = @{ 'msix.sign' = @{ routing = 'missed'; answer = 'missed' } } }
            @{ scenario = 's'; configuration = 'none'; status = 'n/a'; answer = 'fail'; answerBasis = 'response'; winappCommands = @() }
        ) | ForEach-Object { $rec = $_ + $base; $rec | ConvertTo-Json -Compress -Depth 5 } | Set-Content $runs
        $out = Join-Path $TestDrive 'ra.md'
        Write-BenchmarkSummary -RunsPath $runs -SummaryPath $out -Header ([ordered]@{ Models = 'm' })
        $text = Get-Content -Raw $out
        $text | Should -Match 'Answer pass rate: 2/3 \(67%\), 0 partial over the same runs; 0 n/a\. Basis: 1 from commands, 2 from response'
        $text | Should -Match 'Routed and answered 1, routed only 0, answered only 1, neither 1 \(of 3'
        $text | Should -Match '\| heldout \| m \| 33% \(1/3\) \| 67% \(2/3\) \| 1 \| 0 \| 1 \| 1 \|'
        $text | Should -Match '\| heldout \| msix\.sign \| 3 \| 33% \(1/3\) \| 67% \(2/3\) \|'
        $text | Should -Match '\| heldout \| error \| m \| 1 \| 0 \| n/a \| 0% \(0/1\) \| 1\.0 \|'
    }

    It 'compares answers next to routing, with a 2x2 and a capability table' {
        Set-CapabilityMap -Path $mapPath
        try {
            $scenario = [pscustomobject]@{ Id = 's1'; BaseId = 's1'; Variant = 'base'; Set = 'dev'; Cohort = 'implicit'; Prompt = 'p'; Configurations = @('both'); Expect = New-Expect -primary 'msix.sign' }
            $b = Join-Path $TestDrive 'ab'; $c = Join-Path $TestDrive 'ac'
            foreach ($d in $b, $c) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
            $pre = @{ expectedSkills = @('winapp-signing', 'winapp-package') }
            @{ scenario = 's1'; configuration = 'both'; model = 'm'; status = 'fail'; skillsLoaded = @(); winappCommands = @('package'); preflight = $pre } | ConvertTo-Json -Compress -Depth 5 | Set-Content (Join-Path $b 'runs.jsonl')
            @{ scenario = 's1'; configuration = 'both'; model = 'm'; status = 'fail'; skillsLoaded = @(); finalResponse = 'winapp sign'; preflight = $pre } | ConvertTo-Json -Compress -Depth 5 | Set-Content (Join-Path $c 'runs.jsonl')
            $report = Get-ComparisonReport -Baseline $b -Candidate $c -Scenarios @($scenario)
            $report | Should -Match ([regex]::Escape('| 0/1 → 1/1 | +100 pp |'))
            $report | Should -Match ([regex]::Escape('| m | candidate | 1 | 0 | 0 | 1 | 0 |'))
            $report | Should -Match ([regex]::Escape('| msix.sign | 1 | 0/1 → 0/1 | = |'))
            # An empty recorded command list is 'named nothing', not 'not recorded'.
            $near = [pscustomobject]@{ Id = 'n1'; BaseId = 'n1'; Variant = 'base'; Set = 'dev'; Cohort = 'near-miss'; Prompt = 'p'; Configurations = @('both'); Expect = New-Expect -forbid '*' }
            @{ scenario = 'n1'; configuration = 'both'; model = 'm'; status = 'pass'; skillsLoaded = @(); winappCommands = @(); preflight = $pre } | ConvertTo-Json -Compress -Depth 5 | Set-Content (Join-Path $b 'runs.jsonl')
            Copy-Item (Join-Path $b 'runs.jsonl') (Join-Path $c 'runs.jsonl')
            Get-ComparisonReport -Baseline $b -Candidate $c -Scenarios @($near) | Should -Match ([regex]::Escape('| m | n1 | both | 1/1 → 1/1 | = | n/a → n/a | n/a | n/a → n/a | n/a | n/a → n/a | n/a | n/a | 1/1 → 1/1 | = |'))
        }
        finally { Set-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json') }
    }
}

Describe 'Answer signals in the repository' {
    It 'every capability with a winapp command has answer signals that name winapp' {
        $real = Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')
        foreach ($k in $real.Capabilities.Keys) {
            $c = $real.Capabilities[$k]
            if ($c.Commands -and $c.Answer) {
                @($c.Answer.Require | ForEach-Object { $_ } | Where-Object { $_ -match '^winapp\b' }).Count | Should -BeGreaterThan 0 -Because $k
            }
        }
    }

    It 'flags a prompt that already contains an answer signal of its expected capability' {
        $s = [pscustomobject]@{ Id = 'x'; Set = 'heldout'; Prompt = 'I tried winapp sign already'; FixturePath = $null; RoutingSnapshot = $true; LeakAllow = @(); Expect = New-Expect -primary 'msix.sign' }
        $corpus = [pscustomobject]@{ Documents = @(); BigramDocFrequency = @{} }
        $f = @(Invoke-ScenarioLint -Scenarios $s -Corpus $corpus -CapabilityMap $map)
        ($f | Where-Object Rule -eq 'leak-answer').Level | Should -Be 'error'
    }
}
