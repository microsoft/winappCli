#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\Benchmark.psm1') -Force
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $script:mapPath = Join-Path $TestDrive 'capabilities.json'
    @{
        capabilities = @{
            'msix.package' = @{ description = 'package'; commands = @('^(package|pack)$') }
            'msix.sign'    = @{ description = 'sign'; commands = @('^sign$', '^cert') }
            'sandbox.run'  = @{ description = 'sandbox'; commands = @() }
            'ui.automate'  = @{ description = 'ui'; commands = @('^ui') }
            'winui.design' = @{ description = 'design'; commands = @() }
            'api.lookup'   = @{ description = 'api'; commands = @('^find-api') }
        }
        maps         = @(
            @{ id = 'a1'; plugin = 'winapp'; source = 't'; skillSetHash = 'x'; skills = @{ 'winapp-package' = @('msix.package'); 'winapp-signing' = @('msix.sign'); 'winapp-sandbox' = @('sandbox.run', 'ui.automate'); 'winapp-ui' = @('ui.automate') } }
            # A candidate that merged signing into packaging: applies only when its skills are all installed.
            @{ id = 'a2'; plugin = 'winapp'; source = 't'; skillSetHash = 'y'; skills = @{ 'winapp-ship' = @('msix.package', 'msix.sign') } }
            @{ id = 'u1'; plugin = 'winui'; source = 't'; skillSetHash = 'z'; skills = @{ 'winui-design' = @('winui.design', 'api.lookup') } }
        )
    } | ConvertTo-Json -Depth 6 | Set-Content $script:mapPath
    $script:map = Read-CapabilityMap -Path $script:mapPath
    $script:installed = @('winapp-package', 'winapp-signing', 'winapp-sandbox', 'winapp-ui', 'winui-design')
    function New-CapExpect($primary = @(), $acceptable = @(), $forbid = @(), $max = $null, $budget = $null, $commands = @()) {
        [pscustomobject]@{
            SkillsAny = @(); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $max; Commands = @($commands)
            Capabilities = [pscustomobject]@{ Primary = @($primary); Acceptable = @($acceptable); Forbid = @($forbid); BudgetTokens = $budget }
        }
    }
}

Describe 'Capability map' {
    It 'applies the map whose skills are all installed and reports unmapped skills' {
        $r = Resolve-SkillCapabilities -InstalledSkills @('winapp-ship', 'winui-design', 'extra') -Map $map
        $r.Maps | Should -Be @('a2', 'u1')
        $r.SkillCapabilities['winapp-ship'] | Should -Be @('msix.package', 'msix.sign')
        $r.Unmapped | Should -Be @('extra')
    }

    It 'rejects a map that names an unknown capability' {
        $bad = Join-Path $TestDrive 'bad.json'
        @{ capabilities = @{ a = @{ description = 'a'; commands = @() } }; maps = @(@{ id = 'm'; plugin = 'p'; skills = @{ s = @('nope') } }) } | ConvertTo-Json -Depth 5 | Set-Content $bad
        { Read-CapabilityMap -Path $bad } | Should -Throw '*unknown capabilities: nope*'
    }

    It 'has a map for the current skill set of each repo plugin' {
        $real = Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')
        foreach ($p in @(@{ n = 'winapp'; path = 'plugins\winapp' }, @{ n = 'winui'; path = 'plugins\winui\agent-plugin' })) {
            $skills = Get-PluginSkillNames -PluginPath (Join-Path $repoRoot $p.path)
            $m = @($real.Maps | Where-Object { $_.Plugin -eq $p.n -and $_.SkillSetHash -eq (Get-SkillSetHash $skills) })
            $m.Count | Should -Be 1 -Because "capabilities.json must map the current '$($p.n)' skills; update skillSetHash and skills when they change"
            @($m[0].Skills.Keys | Sort-Object) | Should -Be @($skills | Sort-Object)
        }
    }

    It 'every capability a scenario names exists' {
        $real = Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')
        foreach ($s in Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')) {
            $c = $s.Expect.Capabilities
            $c | Should -Not -BeNullOrEmpty -Because "scenario '$($s.Id)' should use capability expectations"
            foreach ($id in @($c.Primary | ForEach-Object { $_ }) + @($c.Acceptable)) { $real.Capabilities.Contains($id) | Should -BeTrue -Because "$($s.Id): $id" }
            foreach ($pattern in $c.Forbid) { @($real.Capabilities.Keys | Where-Object { $_ -like $pattern }).Count | Should -BeGreaterThan 0 -Because "$($s.Id): $pattern" }
        }
    }
}

Describe 'Test-CapabilityExpectations' {
    It 'passes when a primary capability loads' {
        (Test-CapabilityExpectations -Expect (New-CapExpect -primary 'msix.sign') -LoadedSkills 'winapp-signing' -InstalledSkills $installed -Map $map).Status | Should -Be 'pass'
    }

    It 'requires every capability of a primary group, and scores part of it as partial' {
        $e = New-CapExpect -primary @(, @('sandbox.run', 'ui.automate'))
        (Test-CapabilityExpectations -Expect $e -LoadedSkills 'winapp-sandbox' -InstalledSkills $installed -Map $map).Status | Should -Be 'pass'
        $r = Test-CapabilityExpectations -Expect $e -LoadedSkills 'winapp-ui' -InstalledSkills $installed -Map $map
        $r.Status | Should -Be 'partial'
        $r.Notes | Should -Match 'partial'
    }

    It 'scores only acceptable capabilities as partial and nothing as fail' {
        $e = New-CapExpect -primary 'msix.package' -acceptable 'msix.sign'
        (Test-CapabilityExpectations -Expect $e -LoadedSkills 'winapp-signing' -InstalledSkills $installed -Map $map).Status | Should -Be 'partial'
        $r = Test-CapabilityExpectations -Expect $e -LoadedSkills @() -InstalledSkills $installed -Map $map
        $r.Status | Should -Be 'fail'
        $r.Failures[0] | Should -Match 'no primary capability loaded \(msix\.package\)'
    }

    It 'fails on a forbidden capability even when a primary one loads' {
        $r = Test-CapabilityExpectations -Expect (New-CapExpect -primary 'api.lookup' -forbid 'winui.*') -LoadedSkills 'winui-design' -InstalledSkills $installed -Map $map
        $r.Status | Should -Be 'fail'
        $r.Failures[0] | Should -Match 'forbidden capability loaded: winui\.design \(via winui-design\)'
    }

    It 'is n/a when no primary capability and nothing forbidden is installed' {
        $r = Test-CapabilityExpectations -Expect (New-CapExpect -primary 'winui.design' -acceptable 'msix.package') -LoadedSkills 'winapp-package' -InstalledSkills @('winapp-package') -Map $map
        $r.Status | Should -Be 'n/a'
        $r.ExpectedInstalled | Should -BeFalse
        $r.Notes | Should -Contain 'primary capability not installed'
    }

    It 'scores near-misses with forbid * and maxSkills 0' {
        $e = New-CapExpect -forbid '*' -max 0
        (Test-CapabilityExpectations -Expect $e -LoadedSkills @() -InstalledSkills $installed -Map $map).Status | Should -Be 'pass'
        (Test-CapabilityExpectations -Expect $e -LoadedSkills 'winapp-ui' -InstalledSkills $installed -Map $map).Status | Should -Be 'fail'
        (Test-CapabilityExpectations -Expect $e -LoadedSkills @() -InstalledSkills @() -Map $map).Status | Should -Be 'pass'
    }

    It 'treats a primary capability only reachable through a forbidden skill as not installed' {
        $r = Test-CapabilityExpectations -Expect (New-CapExpect -primary 'api.lookup' -forbid 'winui.*') -LoadedSkills @() -InstalledSkills @('winui-design') -Map $map
        $r.Status | Should -Be 'pass'
        $r.ExpectedInstalled | Should -BeFalse
        $r.Notes | Should -Contain 'primary capability only installed in a skill that also carries a forbidden capability'
    }
    It 'scores renamed skills through the candidate map' {
        (Test-CapabilityExpectations -Expect (New-CapExpect -primary 'msix.sign') -LoadedSkills 'winapp-ship' -InstalledSkills @('winapp-ship') -Map $map).Status | Should -Be 'pass'
    }
}

Describe 'Test-ScenarioExpectations' {
    It 'reports the right command named without a primary skill' {
        $r = Test-ScenarioExpectations -Expect (New-CapExpect -primary 'msix.sign') -LoadedSkills @() -InstalledSkills $installed -WinappCommands @('sign') -Map $map
        $r.Status | Should -Be 'fail'
        $r.CommandHit | Should -BeTrue
        $r.CommandWithoutSkill | Should -BeTrue
        (Test-ScenarioExpectations -Expect (New-CapExpect -primary 'msix.sign') -LoadedSkills 'winapp-signing' -InstalledSkills $installed -WinappCommands @('cert generate') -Map $map).CommandWithoutSkill | Should -BeFalse
    }

    It 'uses scenario command patterns over capability ones, and null when commands were not recorded' {
        $e = New-CapExpect -primary 'msix.sign' -commands '^store'
        (Test-ScenarioExpectations -Expect $e -LoadedSkills @() -InstalledSkills $installed -WinappCommands @('sign') -Map $map).CommandHit | Should -BeFalse
        (Test-ScenarioExpectations -Expect $e -LoadedSkills @() -InstalledSkills $installed -WinappCommands $null -Map $map).CommandHit | Should -BeNullOrEmpty
    }

    It 'flags skill context over budget' {
        $r = Test-ScenarioExpectations -Expect (New-CapExpect -primary 'msix.sign' -budget 1000) -LoadedSkills 'winapp-signing' -InstalledSkills $installed -SkillContextTokens 2500 -Map $map
        $r.OverBudget | Should -BeTrue
        $r.Status | Should -Be 'pass'
    }

    It 'still scores skill-name expectations' {
        $legacy = [pscustomobject]@{ SkillsAny = @('winapp-package'); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null }
        (Test-ScenarioExpectations -Expect $legacy -LoadedSkills 'winapp-package' -InstalledSkills $installed -Map $map).Status | Should -Be 'pass'
    }
}

Describe 'Scenario sets, cohorts, and paraphrases' {
    BeforeAll {
        $root = Join-Path $TestDrive 'scen'
        New-Item -ItemType Directory -Path (Join-Path $root 'held') -Force | Out-Null
        @{ id = 'held'; description = 'd'; set = 'heldout'; cohort = 'trap'; prompt = 'base'; paraphrases = [ordered]@{ novice = 'plain'; terse = 'short' }
            configurations = @('none', 'both'); expect = @{ capabilities = @{ primary = @('a', @('b', 'c')); forbid = @('*') } }
        } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'held\scenario.json')
        $loaded = Get-ScenarioDefinitions -ScenariosRoot $root
    }

    It 'expands paraphrases into scenarios that share expectations' {
        $loaded.Id | Should -Be @('held', 'held.novice', 'held.terse')
        $loaded.Variant | Should -Be @('base', 'novice', 'terse')
        $loaded[2].Prompt | Should -Be 'short'
        @($loaded.BaseId | Select-Object -Unique) | Should -Be 'held'
        @($loaded | ForEach-Object Set | Select-Object -Unique) | Should -Be 'heldout'
    }

    It 'parses primary alternatives as single capabilities or groups' {
        $p = $loaded[0].Expect.Capabilities.Primary
        $p.Count | Should -Be 2
        @($p[0]) | Should -Be @('a')
        @($p[1]) | Should -Be @('b', 'c')
    }

    It 'requires a known cohort and set' {
        $bad = Join-Path $TestDrive 'bad'
        New-Item -ItemType Directory -Path (Join-Path $bad 'x') -Force | Out-Null
        @{ id = 'x'; description = 'd'; prompt = 'p'; configurations = @('both'); expect = @{} } | ConvertTo-Json | Set-Content (Join-Path $bad 'x\scenario.json')
        { Get-ScenarioDefinitions -ScenariosRoot $bad } | Should -Throw '*needs a cohort*'
        @{ id = 'x'; description = 'd'; set = 'test'; cohort = 'trap'; prompt = 'p'; configurations = @('both'); expect = @{} } | ConvertTo-Json | Set-Content (Join-Path $bad 'x\scenario.json')
        { Get-ScenarioDefinitions -ScenariosRoot $bad } | Should -Throw "*unknown set 'test'*"
    }

    It 'runs only the dev set unless asked, and filters by base id and variant' {
        $run = Join-Path $PSScriptRoot '..\run.ps1'
        $dev = (& pwsh -NoProfile -File $run -Plan -Model m -Iterations 1 2>&1) -join "`n"
        $dev | Should -Not -Match 'heldout'
        $id = (Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios') | Where-Object Set -eq 'heldout' | Select-Object -First 1).BaseId
        $out = (& pwsh -NoProfile -File $run -Plan -Set heldout -Scenario $id -Configuration both -Model m -Iterations 1 2>&1) -join "`n"
        $out | Should -Match 'Total agent sessions: 3'
        $out = (& pwsh -NoProfile -File $run -Plan -Set heldout -Scenario $id -Variant base -Configuration both -Model m -Iterations 1 2>&1) -join "`n"
        $out | Should -Match 'Total agent sessions: 1'
    }
}

Describe 'Scenario set composition' {
    BeforeAll {
        $all = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        $bases = @($all | Where-Object Variant -eq 'base')
    }

    It 'keeps near-misses and traps at 20% or more of each set' {
        foreach ($set in 'dev', 'heldout') {
            $s = @($bases | Where-Object Set -eq $set)
            $s.Count | Should -BeGreaterThan 0
            (@($s | Where-Object Cohort -in 'near-miss', 'trap').Count / $s.Count) | Should -BeGreaterOrEqual 0.2 -Because "set $set"
        }
    }

    It 'runs every held-out scenario with the no-plugin control and two paraphrases' {
        foreach ($s in @($bases | Where-Object Set -eq 'heldout')) {
            $s.Configurations | Should -Contain 'none' -Because $s.Id
            @($all | Where-Object BaseId -eq $s.BaseId).Count | Should -Be 3 -Because $s.Id
        }
    }

    It 'never makes a held-out primary reachable only through a forbidden capability' {
        $real = Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')
        $plugin = @{ winapp = @('winapp'); winui = @('winui'); both = @('winapp', 'winui') }
        foreach ($s in @($bases | Where-Object { $_.Set -eq 'heldout' -and $_.Expect.Capabilities.Primary.Count })) {
            foreach ($cfg in @($s.Configurations | Where-Object { $_ -ne 'none' })) {
                $skills = @($real.Maps | Where-Object { $_.Plugin -in $plugin[$cfg] } | ForEach-Object { $_.Skills.Keys })
                $r = Test-CapabilityExpectations -Expect $s.Expect -LoadedSkills @() -InstalledSkills $skills -Map $real
                $r.Notes | Should -Not -Contain 'primary capability only installed in a skill that also carries a forbidden capability' -Because "$($s.Id) [$cfg]"
            }
        }
    }
    It 'covers every capability as primary at least twice in the held-out set' {
        $real = Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')
        $primary = @($bases | Where-Object Set -eq 'heldout' | ForEach-Object { @($_.Expect.Capabilities.Primary | ForEach-Object { $_ }) | Select-Object -Unique })
        foreach ($cap in $real.Capabilities.Keys) {
            @($primary | Where-Object { $_ -eq $cap }).Count | Should -BeGreaterOrEqual 2 -Because "capability $cap"
        }
    }
}

Describe 'Rescore and summaries with capabilities' {
    BeforeAll {
        $scenario = [pscustomobject]@{
            Id = 's1'; BaseId = 's1'; Variant = 'base'; Set = 'dev'; Cohort = 'implicit'; Prompt = 'current prompt'; Configurations = @('both')
            Expect = New-CapExpect -primary 'msix.sign' -acceptable 'msix.package'
        }
        $explicit = [pscustomobject]@{
            Id = 's2'; BaseId = 's2'; Variant = 'base'; Set = 'heldout'; Cohort = 'explicit-command'; Prompt = 'p'; Configurations = @('none', 'both')
            Expect = New-CapExpect -primary 'msix.package'
        }
        Set-CapabilityMap -Path $mapPath
    }
    AfterAll { Set-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json') }

    It 'rescores to partial, records signals, and skips runs whose prompt changed' {
        $dir = Join-Path $TestDrive 'r'
        New-Item -ItemType Directory -Path $dir | Out-Null
        $pre = @{ expectedSkills = $installed }
        @(
            [ordered]@{ scenario = 's1'; configuration = 'both'; model = 'm'; iteration = 1; status = 'pass'; skillsLoaded = @('winapp-package'); winappCommands = @('sign'); tokens = $null; skillContextTokensApprox = $null; aiCredits = $null; durationMs = 1; preflight = $pre }
            [ordered]@{ scenario = 's1'; configuration = 'both'; model = 'm'; iteration = 2; status = 'pass'; skillsLoaded = @(); promptHash = 'stale'; tokens = $null; skillContextTokensApprox = $null; aiCredits = $null; durationMs = 1; preflight = $pre }
        ) | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 5 } | Set-Content (Join-Path $dir 'runs.jsonl')
        $r = Invoke-Rescore -ResultsDir $dir -Scenarios @($scenario)
        $rows = Get-Content $r.RunsPath | ConvertFrom-Json
        $rows[0].status | Should -Be 'partial'
        $rows[0].cohort | Should -Be 'implicit'
        $rows[0].commandWithoutSkill | Should -BeTrue
        $rows[0].capabilitiesLoaded | Should -Be @('msix.package')
        $rows[1].status | Should -Be 'pass'
        $rows[1].expectationNotes | Should -Contain 'prompt changed since this run; status not rescored'
    }

    It 'reports cohorts, leaves explicit-command and the none control out of the pass rate, and lists the control' {
        $runs = Join-Path $TestDrive 'cohort.jsonl'
        $base = @{ model = 'm'; reason = ''; tokens = $null; skillContextTokensApprox = 10; aiCredits = 2; durationMs = 1000; skillsLoaded = @() }
        @(
            @{ scenario = 's1'; configuration = 'both'; set = 'dev'; cohort = 'implicit'; status = 'pass'; commandHit = $true; commandWithoutSkill = $false }
            @{ scenario = 's1'; configuration = 'both'; set = 'dev'; cohort = 'implicit'; status = 'partial'; commandHit = $true; commandWithoutSkill = $true }
            @{ scenario = 's2'; configuration = 'both'; set = 'heldout'; cohort = 'explicit-command'; status = 'fail'; commandHit = $false; commandWithoutSkill = $false }
            @{ scenario = 's2'; configuration = 'none'; set = 'heldout'; cohort = 'explicit-command'; status = 'n/a'; commandHit = $true; winappCommands = @('package') }
        ) | ForEach-Object { $rec = $_ + $base; $rec | ConvertTo-Json -Compress -Depth 5 } | Set-Content $runs
        $out = Join-Path $TestDrive 'cohort.md'
        Write-BenchmarkSummary -RunsPath $runs -SummaryPath $out -Header ([ordered]@{ Models = 'm' })
        $text = Get-Content -Raw $out
        $text | Should -Match 'Pass rate: 1/2 \(50%\), 1 partial'
        $text | Should -Match 'Explicit-command pass rate: 0/1'
        $text | Should -Match 'Right winapp command named without loading a primary skill: 1 of 2'
        $text | Should -Match '\| dev \| implicit \| m \| 2 \| 1 \| 1 \| 0 \| 0 \| 50% \| n/a \| 1 \|'
        $text | Should -Match '## No-plugin control'
        $text | Should -Match '\| heldout \| explicit-command \| m \| 1 \| 1 \| 1/1 \| n/a \| 2\.0 \|'
    }

    It 'compares by set and cohort and counts partial runs as scored' {
        $b = Join-Path $TestDrive 'cb'; $c = Join-Path $TestDrive 'cc'
        foreach ($d in $b, $c) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
        $pre = @{ expectedSkills = $installed }
        @{ scenario = 's1'; configuration = 'both'; model = 'm'; status = 'pass'; skillsLoaded = @('winapp-package'); preflight = $pre } | ConvertTo-Json -Compress -Depth 5 | Set-Content (Join-Path $b 'runs.jsonl')
        @{ scenario = 's1'; configuration = 'both'; model = 'm'; status = 'pass'; skillsLoaded = @('winapp-signing'); preflight = $pre } | ConvertTo-Json -Compress -Depth 5 | Set-Content (Join-Path $c 'runs.jsonl')
        $report = Get-ComparisonReport -Baseline $b -Candidate $c -Scenarios @($scenario, $explicit)
        $report | Should -Match ([regex]::Escape('| m | 1 | 0/1 → 1/1 | +100 pp |'))
        $report | Should -Match ([regex]::Escape('| dev | implicit | 1 | 0/1 → 1/1 | +100 pp |'))
    }
}
