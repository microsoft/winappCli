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
        # prefix (149) + skill body from skill.invoked (20, trimmed in this fixture) + suffix (17)
        $r.skillContextChars | Should -Be 186
        $r.skillContextTokensApprox | Should -Be 46
        $r.skillContextReason | Should -BeNullOrEmpty
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
        $r.skillContextChars | Should -BeNullOrEmpty
    }

    It 'reports zero skill context, not null, when a session loaded no skills' {
        $path = Join-Path $TestDrive 'no-skills.jsonl'
        Get-Content (Join-Path $PSScriptRoot 'events-complete.jsonl') |
            Where-Object { $_ -notmatch '"type":"skill\.' } | Set-Content $path
        $r = Read-SessionEvents -Path $path
        $r.skillContextChars | Should -Be 0
        @($r.skillsInvoked).Count | Should -Be 0
    }

    It 'records the selected agent and winapp commands from shell calls and the final answer' {
        $path = Join-Path $TestDrive 'agent.jsonl'
        @(
            '{"type":"session.start","data":{"copilotVersion":"1.0.87-0"}}'
            '{"type":"subagent.selected","data":{"agentName":"winappcli:winapp"}}'
            '{"type":"tool.execution_start","data":{"toolCallId":"a","toolName":"powershell","arguments":{"command":"winapp --version; winapp cert generate --manifest .\\Package.appxmanifest"}}}'
            '{"type":"assistant.message","data":{"content":"Run winapp sign dist\\app.msix cert.pfx, then winapp sign again."}}'
            '{"type":"assistant.message","data":{"content":"Use the winapp CLI: winapp package .\\out, and see C:\\tools\\winapp run.md"}}'
        ) | Set-Content $path
        $r = Read-SessionEvents -Path $path
        $r.selectedAgent | Should -Be 'winappcli:winapp'
        # Only the last assistant message counts as the answer; prose and paths are ignored.
        @($r.winappCommands) | Should -Be @('cert generate', 'package')
    }
}

Describe 'Get-WinappCommands' {
    It 'returns an empty array when nothing matches' {
        @(Get-WinappCommands -Text @('no commands here', $null)).Count | Should -Be 0
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

Describe 'Write-BenchmarkSummary' {
    It 'writes a table row with skill context and redacts the home path' {
        $runs = Join-Path $TestDrive 'runs.jsonl'
        $userHome = [Environment]::GetFolderPath('UserProfile')
        @(
            [ordered]@{ scenario = 's1'; configuration = 'winapp'; model = 'm'; iteration = 1; status = 'pass'; reason = ''; skillsLoaded = @('winapp-setup'); tokens = @{ input = 100000; output = 2000; cacheRead = 80000 }; skillContextTokensApprox = 6000; aiCredits = 10; durationMs = 20000 }
            [ordered]@{ scenario = 's1'; configuration = 'winapp'; model = 'm'; iteration = 2; status = 'timeout'; reason = "killed in $userHome\x"; skillsLoaded = @(); tokens = $null; skillContextTokensApprox = $null; aiCredits = $null; durationMs = 300000 }
        ) | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 5 } | Set-Content $runs
        $out = Join-Path $TestDrive 'summary.md'
        Write-BenchmarkSummary -RunsPath $runs -SummaryPath $out -Header ([ordered]@{ Models = 'm' }) -ScenarioOrder 's1'
        $text = Get-Content -Raw $out

        $text | Should -Match '\| winapp \| m \| 1/2 \(1 timeout\) \| winapp-setup \(1/1\) \| 100\.0k \| ~6\.0k \| 2\.0k \| 80\.0k \|'
        $text | Should -Match 'Skill context delivered: ~6\.0k tokens'
        $text | Should -Not -Match ([regex]::Escape($userHome))
    }
}

Describe 'Invoke-Rescore' {
    It 're-evaluates pass/fail runs against new expectations and leaves other statuses alone' {
        $dir = Join-Path $TestDrive 'results'
        New-Item -ItemType Directory -Path $dir | Out-Null
        $pre = @{ expectedSkills = @('winapp-frameworks', 'winui-wpf-migration') }
        $common = [ordered]@{ tokens = $null; skillContextTokensApprox = $null; aiCredits = $null; durationMs = 1000; expectationNotes = @(); preflight = $pre }
        @(
            [ordered]@{ scenario = 'wpf'; configuration = 'both'; model = 'm'; iteration = 1; status = 'pass'; reason = ''; skillsLoaded = @('winapp-frameworks') }
            [ordered]@{ scenario = 'wpf'; configuration = 'both'; model = 'm'; iteration = 2; status = 'pass'; reason = ''; skillsLoaded = @('winui-wpf-migration') }
            [ordered]@{ scenario = 'wpf'; configuration = 'both'; model = 'm'; iteration = 3; status = 'timeout'; reason = 'slow'; skillsLoaded = @() }
            [ordered]@{ scenario = 'gone'; configuration = 'both'; model = 'm'; iteration = 1; status = 'fail'; reason = 'x'; skillsLoaded = @() }
        ) | ForEach-Object { $rec = $_; foreach ($k in $common.Keys) { $rec[$k] = $common[$k] }; $rec | ConvertTo-Json -Compress -Depth 5 } |
            Set-Content (Join-Path $dir 'runs.jsonl')
        $original = Get-FileHash (Join-Path $dir 'runs.jsonl')
        $scenario = [pscustomobject]@{
            Id = 'wpf'; Configurations = @('winui', 'both')
            Expect = [pscustomobject]@{ SkillsAny = @('winui-wpf-migration'); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null }
        }

        $r = Invoke-Rescore -ResultsDir $dir -Scenarios @($scenario)

        $r.Runs | Should -Be 4
        $r.Changed | Should -Be 1
        $rows = Get-Content $r.RunsPath | ConvertFrom-Json
        $rows.status | Should -Be @('fail', 'pass', 'timeout', 'fail')
        $rows[0].originalStatus | Should -Be 'pass'
        $rows[3].expectationNotes | Should -Contain 'scenario no longer defined; status not rescored'
        (Get-FileHash (Join-Path $dir 'runs.jsonl')).Hash | Should -Be $original.Hash
        Get-Content -Raw $r.SummaryPath | Should -Match 'Status changes\*\*: 1 of 4 runs'
    }
}

Describe 'Split-ListArgument' {
    It 'splits comma-separated strings and flattens arrays' {
        Split-ListArgument @('a,b , c') | Should -Be @('a', 'b', 'c')
        Split-ListArgument @('a', 'b,c') | Should -Be @('a', 'b', 'c')
        @(Split-ListArgument $null).Count | Should -Be 0
    }

    It 'accepts comma-separated lists through pwsh -File' {
        $run = Join-Path $PSScriptRoot '..\run.ps1'
        $out = & pwsh -NoProfile -File $run -Plan -Scenario 'wpf-to-winui,console-arg-parsing' -Configuration 'winui,both' -Model 'm1,m2' -Iterations 1 2>&1
        $LASTEXITCODE | Should -Be 0
        ($out -join "`n") | Should -Match 'Total agent sessions: 8'
    }

    It 'rejects an unknown configuration' {
        $run = Join-Path $PSScriptRoot '..\run.ps1'
        $out = & pwsh -NoProfile -File $run -Plan -Configuration 'winapp,bogus' 2>&1
        $LASTEXITCODE | Should -Not -Be 0
        ($out -join "`n") | Should -Match 'Unknown configuration\(s\): bogus'
    }
}

Describe 'Scenario definitions' {
    It 'all scenarios load and validate' {
        $s = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        $s.Count | Should -BeGreaterThan 0
        foreach ($x in $s) { $x.Prompt | Should -Not -Match '\b(winapp|winui)-[a-z-]+\b' -Because "scenario '$($x.Id)' prompt must not name a skill" }
    }
}
