#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\Benchmark.psm1') -Force
}

Describe 'Read-SessionEvents' {
    It 'extracts skills, tokens, credits, and counts from a completed session' {
        $r = Read-SessionEvents -Path (Join-Path $PSScriptRoot 'events-complete.jsonl')

        $r.copilotVersion | Should -Be '1.0.87-0'
        $r.model | Should -Be 'claude-sonnet-5.5'
        $r.sessionShutdown | Should -BeTrue
        @($r.skillsInvoked).Count | Should -Be 1
        $r.skillsInvoked[0].name | Should -Be 'winapp-frameworks'
        $r.skillsInvoked[0].trigger | Should -Be 'agent-invoked'
        $r.skillsInvoked[0].plugin | Should -Be 'winappcli'
        @($r.skillsContextDelivered) | Should -Be @('winapp-frameworks')
        $r.tokens.input | Should -Be 115032
        $r.tokens.output | Should -Be 1712
        $r.tokens.cacheRead | Should -Be 89095
        $r.tokens.cacheWrite | Should -Be 25923
        $r.tokensReason | Should -BeNullOrEmpty
        $r.aiCredits | Should -Be 9.977
        $r.modelTurns | Should -Be 5
        $r.toolCalls | Should -Be 5
        $r.toolCallsByName['skill'] | Should -Be 1
        $r.deniedToolCalls['web_fetch'] | Should -Be 1
        $r.unparsedLines | Should -Be 0
    }

    It 'returns null tokens with a reason when session.shutdown is missing' {
        $r = Read-SessionEvents -Path (Join-Path $PSScriptRoot 'events-no-shutdown.jsonl')

        $r.sessionShutdown | Should -BeFalse
        $r.tokens | Should -BeNullOrEmpty
        $r.aiCredits | Should -BeNullOrEmpty
        $r.tokensReason | Should -Be 'session.shutdown event missing'
        $r.skillsInvoked[0].name | Should -Be 'winapp-frameworks'
        $r.unparsedLines | Should -Be 1
    }

    It 'returns nulls, not zeros, when there is no log' {
        $r = Read-SessionEvents -Path (Join-Path $TestDrive 'missing.jsonl')

        $r.tokens | Should -BeNullOrEmpty
        $r.modelTurns | Should -BeNullOrEmpty
        $r.toolCalls | Should -BeNullOrEmpty
        $r.tokensReason | Should -Be 'no persisted events log'
    }
}

Describe 'Test-Expectations' {
    BeforeAll {
        $installed = @('winapp-setup', 'winapp-package', 'winapp-signing')
        function New-Expect($any = @(), $all = @(), $forbid = @(), $max = $null) {
            [pscustomobject]@{ SkillsAny = @($any); SkillsAll = @($all); SkillsForbid = @($forbid); MaxSkills = $max }
        }
    }

    It 'passes when one of skillsAny loaded' {
        (Test-Expectations -Expect (New-Expect -any 'winapp-package', 'winui-packaging') -LoadedSkills 'winapp-package' -InstalledSkills $installed).Passed | Should -BeTrue
    }

    It 'fails when none of the installed skillsAny loaded' {
        $r = Test-Expectations -Expect (New-Expect -any 'winapp-package') -LoadedSkills @() -InstalledSkills $installed
        $r.Passed | Should -BeFalse
        $r.Failures[0] | Should -Match 'none of skillsAny'
    }

    It 'skips skillsAny when none of them are installed' {
        $r = Test-Expectations -Expect (New-Expect -any 'winui-design') -LoadedSkills @() -InstalledSkills $installed
        $r.Passed | Should -BeTrue
        $r.Notes[0] | Should -Match 'not applicable'
    }

    It 'fails on a forbidden wildcard match and on maxSkills' {
        $r = Test-Expectations -Expect (New-Expect -forbid 'winapp-*' -max 0) -LoadedSkills 'winapp-setup' -InstalledSkills $installed
        $r.Passed | Should -BeFalse
        $r.Failures.Count | Should -Be 2
    }

    It 'requires every installed skillsAll entry' {
        $r = Test-Expectations -Expect (New-Expect -all 'winapp-package', 'winapp-signing') -LoadedSkills 'winapp-package' -InstalledSkills $installed
        $r.Passed | Should -BeFalse
        $r.Failures[0] | Should -Match 'winapp-signing'
    }
}

Describe 'Directory snapshots' {
    It 'reports added, modified, and deleted files' {
        $dir = Join-Path $TestDrive 'ws'
        New-Item -ItemType Directory -Path $dir | Out-Null
        Set-Content (Join-Path $dir 'a.txt') 'a'
        Set-Content (Join-Path $dir 'b.txt') 'b'
        $before = Get-DirectorySnapshot -Path $dir
        @(Compare-DirectorySnapshot -Before $before -After (Get-DirectorySnapshot -Path $dir)).Count | Should -Be 0

        Set-Content (Join-Path $dir 'a.txt') 'changed'
        Remove-Item (Join-Path $dir 'b.txt')
        Set-Content (Join-Path $dir 'c.txt') 'c'
        $changes = Compare-DirectorySnapshot -Before $before -After (Get-DirectorySnapshot -Path $dir)
        $changes | Should -Be @('modified a.txt', 'deleted b.txt', 'added c.txt')
    }
}

Describe 'Scenario definitions' {
    It 'all scenarios load and validate' {
        $s = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        $s.Count | Should -BeGreaterThan 0
        foreach ($x in $s) { $x.Prompt | Should -Not -Match '\b(winapp|winui)-[a-z-]+\b' -Because "scenario '$($x.Id)' prompt must not name a skill" }
    }
}
