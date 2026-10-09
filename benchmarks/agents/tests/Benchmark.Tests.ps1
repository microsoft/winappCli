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
        $r.skillRepeatDeliveries | Should -Be 0
        $r.skillRepeatContextTokensApprox | Should -Be 0
        $r.unparsedLines | Should -Be 0
    }

    It 'measures tool time as the union of running tool calls, plus API and session time' {
        $r = Read-SessionEvents -Path (Join-Path $PSScriptRoot 'events-complete.jsonl')
        # 60 + 33 (two overlapping calls 05.063-05.096) + 4 + 4
        $r.toolTimeMs | Should -Be 101
        $r.apiDurationMs | Should -Be 19117
        $r.sessionDurationMs | Should -Be 20618
    }

    It 'leaves tool time out for a call that never completed' {
        $path = Join-Path $TestDrive 'open-tool.jsonl'
        Get-Content (Join-Path $PSScriptRoot 'events-complete.jsonl') |
            Where-Object { $_ -notmatch '"type":"tool\.execution_complete".*toolu_01UQbf1HTNJXhaEVVr2nVocQ' } | Set-Content $path
        $r = Read-SessionEvents -Path $path
        $r.toolTimeMs | Should -Be 97
    }

    It 'counts repeated deliveries of the same skill and the context they add' {
        $path = Join-Path $TestDrive 'repeat.jsonl'
        $lines = Get-Content (Join-Path $PSScriptRoot 'events-complete.jsonl')
        $delivery = $lines | Where-Object { $_ -match '"type":"skill\.context_delivered' }
        # Deliver the same skill twice more after the first delivery.
        $lines + $delivery + $delivery | Set-Content $path
        $r = Read-SessionEvents -Path $path
        @($r.skillsContextDelivered) | Should -Be @('winapp-frameworks')
        $r.skillRepeatDeliveries | Should -Be 2
        $r.skillContextChars | Should -Be (186 * 3)
        $r.skillRepeatContextTokensApprox | Should -Be ([Math]::Round(186 * 2 / 4))
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

    It 'records a find-api verb but not a search query' {
        @(Get-WinappCommands -Text @('winapp find-api navigationview --json', 'winapp find-api members Microsoft.UI.Xaml.Controls.InfoBar')) |
            Should -Be @('find-api', 'find-api members')
    }

    It 'records npm wrapper node subcommands' {
        @(Get-WinappCommands -Text @('npx winapp node create-addon --name myAddon; winapp node add-electron-debug-identity', 'winapp node something-else')) |
            Should -Be @('node create-addon', 'node add-electron-debug-identity', 'node')
    }
}

Describe 'Format-PassRate' {
    It 'scores only pass and fail and lists every other status as excluded' {
        Format-PassRate @('pass', 'timeout') | Should -Be '1/1 (100%); excluded: 1 timeout'
        Format-PassRate @('pass', 'fail', 'n/a', 'harness_error', 'n/a') | Should -Be '1/2 (50%); excluded: 1 harness_error, 2 n/a'
        Format-PassRate @('n/a') | Should -Be '-; excluded: 1 n/a'
        Format-PassRate @() | Should -Be '-'
    }
}

Describe 'Compare-PreflightSkills' {
    BeforeAll {
        $copilotHome = 'C:\bench\home'
        function New-Skill($name, [bool]$enabled = $true, $source = 'plugin', $path = "$copilotHome\installed-plugins\x\SKILL.md") {
            [pscustomobject]@{ name = $name; source = $source; enabled = $enabled; path = $path }
        }
    }

    It 'passes when every plugin copy of a shared skill name is listed' {
        $r = Compare-PreflightSkills -Expected @('winapp-find-api', 'winapp-setup', 'winapp-find-api') -CopilotHome $copilotHome -Listed @(
            New-Skill 'winappcli:winapp-find-api'; New-Skill 'winui:winapp-find-api'; New-Skill 'winapp-setup'; New-Skill 'customize' -source 'builtin' -path 'x')
        @($r.Missing).Count | Should -Be 0
        @($r.Unexpected).Count | Should -Be 0
    }

    It 'fails when one plugin copy of a shared skill name is missing or disabled' {
        $r = Compare-PreflightSkills -Expected @('winapp-find-api', 'winapp-find-api') -CopilotHome $copilotHome -Listed @(New-Skill 'winapp-find-api')
        $r.Missing | Should -Be @('winapp-find-api (1 of 2 copies)')
        $r = Compare-PreflightSkills -Expected @('winapp-find-api', 'winapp-find-api') -CopilotHome $copilotHome -Listed @(
            New-Skill 'winappcli:winapp-find-api'; New-Skill 'winui:winapp-find-api' -enabled $false)
        $r.Missing | Should -Be @('winapp-find-api (1 of 2 copies)')
    }

    It 'reports skills from outside the isolated home, unknown skills, and extra copies' {
        $r = Compare-PreflightSkills -Expected @('winapp-setup') -CopilotHome $copilotHome -Listed @(
            New-Skill 'winapp-setup'; New-Skill 'a:winapp-setup'; New-Skill 'other'; New-Skill 'winapp-setup' -source 'user' -path 'C:\Users\me\.copilot\skills\x')
        $r.Unexpected | Should -Be @('other (plugin)', 'winapp-setup (user)', 'winapp-setup (2 copies, expected 1)')
    }
}

Describe 'Get-BareSkillName' {
    It 'strips the plugin prefix Copilot adds when two plugins ship the same skill name' {
        Get-BareSkillName 'winui:winapp-find-api' | Should -Be 'winapp-find-api'
        Get-BareSkillName 'winapp-find-api' | Should -Be 'winapp-find-api'
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

    It 'skips skillsAny when none of them are installed and reports n/a' {
        $r = Test-Expectations -Expect (New-Expect -any 'winui-design') -LoadedSkills @() -InstalledSkills $installed
        $r.Passed | Should -BeTrue
        $r.NotApplicable | Should -BeTrue
        $r.Status | Should -Be 'n/a'
        $r.Notes[0] | Should -Match 'not applicable'
    }

    It 'is n/a only when nothing in the expectation applies to the installed skills' {
        (Test-Expectations -Expect (New-Expect -any 'winui-design' -all 'winapp-package') -LoadedSkills 'winapp-package' -InstalledSkills $installed).Status | Should -Be 'pass'
        # A forbid-only check of an installed skill (no overreach) is a real pass.
        (Test-Expectations -Expect (New-Expect -any 'winui-design' -forbid 'winapp-setup') -LoadedSkills @() -InstalledSkills $installed).Status | Should -Be 'pass'
        (Test-Expectations -Expect (New-Expect -forbid 'winui-*') -LoadedSkills @() -InstalledSkills $installed).Status | Should -Be 'n/a'
        (Test-Expectations -Expect (New-Expect -forbid 'winui-*' -max 0) -LoadedSkills @() -InstalledSkills $installed).Status | Should -Be 'pass'
    }

    It 'fails rather than n/a when a forbidden skill loads' {
        (Test-Expectations -Expect (New-Expect -any 'winui-design' -forbid 'winapp-setup') -LoadedSkills 'winapp-setup' -InstalledSkills $installed).Status | Should -Be 'fail'
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

        $text | Should -Match '\| winapp \| m \| 1/1 \(100%\); excluded: 1 timeout \| winapp-setup \(1/1\) \| 100\.0k \| ~6\.0k \| 2\.0k \| 80\.0k \|'
        $text | Should -Match 'Pass rate: 1/1 \(100%\); excluded: 1 timeout'
        $text | Should -Match 'Skill context delivered: ~6\.0k tokens'
        $text | Should -Not -Match ([regex]::Escape($userHome))
    }

    It 'leaves n/a runs out of pass rates, lists them separately, and reports repeated deliveries' {
        $runs = Join-Path $TestDrive 'runs-na.jsonl'
        $base = @{ model = 'm'; reason = ''; tokens = $null; skillContextTokensApprox = 100; aiCredits = 1; durationMs = 1000 }
        @(
            @{ scenario = 's1'; configuration = 'winapp'; iteration = 1; status = 'pass'; skillsLoaded = @('a'); skillRepeatDeliveries = 2; skillRepeatContextTokensApprox = 900 }
            @{ scenario = 's1'; configuration = 'winapp'; iteration = 2; status = 'fail'; skillsLoaded = @(); skillRepeatDeliveries = 0; skillRepeatContextTokensApprox = 0 }
            @{ scenario = 's1'; configuration = 'winapp'; iteration = 3; status = 'n/a'; skillsLoaded = @(); skillRepeatDeliveries = 0; skillRepeatContextTokensApprox = 0 }
            @{ scenario = 's1'; configuration = 'winui'; iteration = 1; status = 'n/a'; skillsLoaded = @() }
        ) | ForEach-Object { $rec = $_ + $base; $rec | ConvertTo-Json -Compress -Depth 5 } | Set-Content $runs
        $out = Join-Path $TestDrive 'summary-na.md'
        Write-BenchmarkSummary -RunsPath $runs -SummaryPath $out -Header ([ordered]@{ Models = 'm' }) -ScenarioOrder 's1'
        $text = Get-Content -Raw $out

        $text | Should -Match 'Pass rate: 1/2 \(50%\); excluded: 2 n/a'
        $text | Should -Match 'Repeated skill deliveries: 2 in 1 of 3 measured runs \(~900 extra'
        $text | Should -Match '\| s1 \| winui \| 1 \|'
        $text | Should -Match '\| winapp \| m \| 1/2 \(50%\); excluded: 1 n/a \|.*\| 2 \(~900\) \|'
        $text | Should -Match '\| winui \| m \| -; excluded: 1 n/a \|.*\| n/a \|'
    }

    It 'shows repeat tokens as unknown or partial when their size was not measured, and counts runs without credits' {
        $runs = Join-Path $TestDrive 'runs-unknown.jsonl'
        $base = @{ scenario = 's1'; configuration = 'winapp'; model = 'm'; reason = ''; tokens = $null; skillContextTokensApprox = $null; durationMs = 1000; skillsLoaded = @('a') }
        @(
            @{ iteration = 1; status = 'pass'; aiCredits = 5; skillRepeatDeliveries = 1; skillRepeatContextTokensApprox = $null }
            @{ iteration = 2; status = 'timeout'; aiCredits = $null; skillRepeatDeliveries = 0; skillRepeatContextTokensApprox = 0 }
            @{ configuration = 'both'; iteration = 1; status = 'pass'; aiCredits = 5; skillRepeatDeliveries = 1; skillRepeatContextTokensApprox = 400 }
            @{ configuration = 'both'; iteration = 2; status = 'pass'; aiCredits = 5; skillRepeatDeliveries = 2; skillRepeatContextTokensApprox = $null }
        ) | ForEach-Object { $rec = $base.Clone(); foreach ($k in $_.Keys) { $rec[$k] = $_[$k] }; $rec | ConvertTo-Json -Compress -Depth 5 } | Set-Content $runs
        $out = Join-Path $TestDrive 'summary-unknown.md'
        Write-BenchmarkSummary -RunsPath $runs -SummaryPath $out -Header ([ordered]@{ Models = 'm' }) -ScenarioOrder 's1'
        $text = Get-Content -Raw $out

        $text | Should -Match 'AI credits: 15\.0 \(1 launched run had no credit count\)'
        $text | Should -Match '\| winapp \| m \|.*\| 1 \(unknown\) \|'
        $text | Should -Match '\| both \| m \|.*\| 3 \(~400, partial: 1 of 2 runs measured\) \|'
        $text | Should -Not -Match '\(~0\)'
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
        # A run where the expected skill is not installed becomes n/a.
        [ordered]@{ scenario = 'wpf'; configuration = 'winapp'; model = 'm'; iteration = 1; status = 'pass'; reason = ''; skillsLoaded = @()
            tokens = $null; skillContextTokensApprox = $null; aiCredits = $null; durationMs = 1000; expectationNotes = @(); preflight = @{ expectedSkills = @('winapp-frameworks') }
        } | ConvertTo-Json -Compress -Depth 5 | Add-Content (Join-Path $dir 'runs.jsonl')
        $original = Get-FileHash (Join-Path $dir 'runs.jsonl')
        $scenario = [pscustomobject]@{
            Id = 'wpf'; Configurations = @('winui', 'both')
            Expect = [pscustomobject]@{ SkillsAny = @('winui-wpf-migration'); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null }
        }

        $r = Invoke-Rescore -ResultsDir $dir -Scenarios @($scenario)

        $r.Runs | Should -Be 5
        $r.Changed | Should -Be 3
        $r.Transitions['pass -> n/a'] | Should -Be 1
        $r.Transitions['fail -> scenario_removed'] | Should -Be 1
        $rows = Get-Content $r.RunsPath | ConvertFrom-Json
        $rows.status | Should -Be @('fail', 'pass', 'timeout', 'scenario_removed', 'n/a')
        $rows[0].originalStatus | Should -Be 'pass'
        $rows[3].expectationNotes | Should -Contain 'scenario no longer defined; excluded'
        (Get-FileHash (Join-Path $dir 'runs.jsonl')).Hash | Should -Be $original.Hash
        Get-Content -Raw $r.SummaryPath | Should -Match 'Status changes\*\*: 3 of 5 runs \(pass -> fail 1, fail -> scenario_removed 1, pass -> n/a 1\)'
    }
}

Describe 'Get-CreditSpend' {
    It 'counts launched runs without a credit count at the measured mean and ignores runs that never launched' {
        $s = Get-CreditSpend @(
            [pscustomobject]@{ durationMs = 1000; aiCredits = 10 }
            [pscustomobject]@{ durationMs = 1000; aiCredits = 20 }
            [pscustomobject]@{ durationMs = 300000; aiCredits = $null }
            [pscustomobject]@{ durationMs = $null; aiCredits = $null }
        )
        $s.Measured | Should -Be 30
        $s.Unknown | Should -Be 1
        $s.Spent | Should -Be 45
    }

    It 'uses the default estimate when no run has a credit count yet' {
        $s = Get-CreditSpend @([pscustomobject]@{ durationMs = 300000; aiCredits = $null }) -DefaultEstimate 35
        $s.Spent | Should -Be 35
        (Get-CreditSpend @()).Spent | Should -Be 0
    }
}

Describe 'Filter validation' {
    It 'rejects a mistyped -Scenario before -Rescore and -Compare' {
        $run = Join-Path $PSScriptRoot '..\run.ps1'
        $dir = Join-Path $TestDrive 'r'
        New-Item -ItemType Directory -Path $dir | Out-Null
        '{"scenario":"wpf-to-winui","configuration":"both","model":"m","status":"timeout"}' | Set-Content (Join-Path $dir 'runs.jsonl')

        $out = & pwsh -NoProfile -File $run -Rescore $dir -Scenario 'wpf-to-winui-typo' 2>&1
        $LASTEXITCODE | Should -Not -Be 0
        ($out -join "`n") | Should -Match 'Unknown scenario id\(s\): wpf-to-winui-typo'

        $out = & pwsh -NoProfile -File $run -Compare $dir -Candidate $dir -Scenario 'wpf-to-winui-typo' 2>&1
        $LASTEXITCODE | Should -Not -Be 0
        ($out -join "`n") | Should -Match 'Unknown scenario\(s\) in the compared results: wpf-to-winui-typo'
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

    It 'ships XAML fixtures that parse as XML' {
        foreach ($f in Get-ChildItem -Path (Join-Path $PSScriptRoot '..\scenarios') -Recurse -Filter *.xaml -File) {
            { [xml](Get-Content -Raw $f.FullName) } | Should -Not -Throw -Because $f.FullName
        }
    }

    It 'does not credit signing-only runs in packaging scenarios' {
        $s = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        # Capability maps apply to a plugin's whole skill set, so install both plugins.
        $installed = @(Get-PluginSkillNames (Join-Path $PSScriptRoot '..\..\..\plugins\winapp')) + @(Get-PluginSkillNames (Join-Path $PSScriptRoot '..\..\..\plugins\winui\agent-plugin'))
        foreach ($id in 'explicit-winapp-cli-package', 'winui-release-msix', 'wpf-winappsdk-msix-trap', 'winforms-package-sign') {
            $x = $s | Where-Object Id -eq $id
            # Signing alone is at most acceptable (partial), never a pass.
            (Test-ScenarioExpectations -Expect $x.Expect -LoadedSkills 'winapp-signing' -InstalledSkills $installed).Status | Should -BeIn 'fail', 'partial' -Because $id
            (Test-ScenarioExpectations -Expect $x.Expect -LoadedSkills 'winapp-package', 'winapp-signing' -InstalledSkills $installed).Status | Should -Be 'pass' -Because $id
        }
    }

    It 'requires every scenario to include the both configuration' {
        $s = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        foreach ($x in $s) { $x.Configurations | Should -Contain 'both' -Because "scenario '$($x.Id)' must measure the setup users install" }

        $root = Join-Path $TestDrive 'scenarios'
        New-Item -ItemType Directory -Path (Join-Path $root 'only-winapp') -Force | Out-Null
        @{ id = 'only-winapp'; description = 'd'; prompt = 'p'; configurations = @('winapp'); expect = @{ skillsAny = @('winapp-setup') } } |
            ConvertTo-Json | Set-Content (Join-Path $root 'only-winapp\scenario.json')
        { Get-ScenarioDefinitions -ScenariosRoot $root } | Should -Throw "*must include the 'both' configuration*"
    }
}

Describe 'Get-ComparisonReport' {
    BeforeAll {
        function Write-Runs([string]$Dir, [object[]]$Runs) {
            New-Item -ItemType Directory -Path $Dir -Force | Out-Null
            $Runs | ForEach-Object {
                $r = [ordered]@{ scenario = 's1'; configuration = 'both'; model = 'm'; reason = ''; skillContextTokensApprox = 1000; aiCredits = 10
                    tokens = @{ input = 100000 }; preflight = @{ expectedSkills = @('winapp-signing', 'winui-design') } }
                foreach ($k in $_.Keys) { $r[$k] = $_[$k] }
                $r | ConvertTo-Json -Compress -Depth 5
            } | Set-Content (Join-Path $Dir 'runs.jsonl')
        }
        $scenario = [pscustomobject]@{
            Id = 's1'; Configurations = @('winui', 'both')
            Expect = [pscustomobject]@{ SkillsAny = @('winapp-signing'); SkillsAll = @(); SkillsForbid = @('winui-packaging'); MaxSkills = $null }
        }
        $base = Join-Path $TestDrive 'base'
        $cand = Join-Path $TestDrive 'cand'
        # Recorded statuses are deliberately stale; the report must re-evaluate them.
        Write-Runs $base @(
            @{ status = 'pass'; skillsLoaded = @() }
            @{ status = 'pass'; skillsLoaded = @('winapp-signing') }
            @{ status = 'timeout'; skillsLoaded = @() }
            @{ configuration = 'winui'; status = 'pass'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winui-design') } }
            @{ model = 'only-base'; status = 'pass'; skillsLoaded = @() }
            # Only the forbid applies here; the candidate below adds the expected skill.
            @{ model = 'm2'; status = 'pass'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winui-packaging') } }
            @{ model = 'm2'; configuration = 'winui'; status = 'pass'; skillsLoaded = @('winapp-signing') }
        )
        Write-Runs $cand @(
            @{ status = 'fail'; skillsLoaded = @('winapp-signing'); skillContextTokensApprox = 1500; aiCredits = 8; skillRepeatDeliveries = 1 }
            @{ status = 'pass'; skillsLoaded = @('winapp-signing'); skillContextTokensApprox = 1500; aiCredits = 8; skillRepeatDeliveries = 0 }
            @{ configuration = 'winui'; status = 'pass'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winui-design') } }
            @{ model = 'm2'; status = 'pass'; skillsLoaded = @('winapp-signing'); preflight = @{ expectedSkills = @('winapp-signing', 'winui-packaging') } }
            @{ model = 'm2'; configuration = 'winui'; status = 'pass'; skillsLoaded = @('winapp-signing') }
        )
        $report = Get-ComparisonReport -Baseline $base -Candidate $cand -Scenarios @($scenario)
    }

    It 'compares only cells present on both sides' {
        $report | Should -Match 'Compared cells\*\*: 4 .* 1 baseline-only and 0 candidate-only'
        $report | Should -Not -Match '\| only-base \|'
    }

    It 're-evaluates statuses and reports pass, context, input, credits, and repeat deltas per cell' {
        $report | Should -Match ([regex]::Escape('| m | s1 | both | 1/2 (50%); excluded: 1 timeout → 2/2 (100%) | +50 pp | 1.0k → 1.5k | +50% | 100.0k → 100.0k | = | 10.0 → 8.0 | -20% | - → 1 |'))
    }

    It 'shows n/a cells and leaves them out of pooled pass rates' {
        $report | Should -Match ([regex]::Escape('| m | s1 | winui | -; excluded: 1 n/a → -; excluded: 1 n/a | n/a |'))
        $report | Should -Match ([regex]::Escape('| m | 2 (1 not pooled) | 1/2 (50%); excluded: 1 timeout → 2/2 (100%) | +50 pp |'))
    }

    It 'flags cells where an expected skill is installed on one side only and leaves them out of pooled pass' {
        $report | Should -Match ([regex]::Escape('| m2 | s1 | both | 1/1 (100%) → 1/1 (100%) | check differs |'))
        $report | Should -Match ([regex]::Escape('| m2 | 2 (1 not pooled) | 1/1 (100%) → 1/1 (100%) | = |'))
    }

    It 'filters by model and rejects filter values that are not in the results' {
        $m2 = Get-ComparisonReport -Baseline $base -Candidate $cand -Scenarios @($scenario) -ModelFilter 'm2'
        $m2 | Should -Match '\| m2 \| s1 \| both \|'
        $m2 | Should -Not -Match '\| m \| s1 \|'
        { Get-ComparisonReport -Baseline $base -Candidate $cand -Scenarios @($scenario) -ModelFilter 'other' } | Should -Throw '*Unknown model(s) in the compared results: other*'
        { Get-ComparisonReport -Baseline $base -Candidate $cand -Scenarios @($scenario) -ScenarioFilter 's1-typo' } | Should -Throw '*Unknown scenario(s) in the compared results: s1-typo*'
    }

    It 'flags a candidate that removes one of several expected skills instead of reporting an improvement' {
        $multi = [pscustomobject]@{
            Id = 'pkg'; Configurations = @('both')
            Expect = [pscustomobject]@{ SkillsAny = @('winapp-package', 'winapp-manifest'); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null }
        }
        $b2 = Join-Path $TestDrive 'base-partial'
        $c2 = Join-Path $TestDrive 'cand-partial'
        Write-Runs $b2 @(@{ scenario = 'pkg'; status = 'fail'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winapp-package', 'winapp-manifest') } })
        Write-Runs $c2 @(@{ scenario = 'pkg'; status = 'pass'; skillsLoaded = @('winapp-package'); preflight = @{ expectedSkills = @('winapp-package') } })
        $partial = Get-ComparisonReport -Baseline $b2 -Candidate $c2 -Scenarios @($multi)
        $partial | Should -Match ([regex]::Escape('| m | pkg | both | 0/1 (0%) → 1/1 (100%) | check differs |'))
        $partial | Should -Match ([regex]::Escape('| m | 1 (1 not pooled) | - → - | n/a |'))
    }

    It 'flags a candidate that removes every expected skill instead of reporting n/a' {
        $only = [pscustomobject]@{
            Id = 'pkg'; Configurations = @('both')
            Expect = [pscustomobject]@{ SkillsAny = @('winapp-package'); SkillsAll = @(); SkillsForbid = @(); MaxSkills = $null }
        }
        $b3 = Join-Path $TestDrive 'base-all'
        $c3 = Join-Path $TestDrive 'cand-all'
        Write-Runs $b3 @(@{ scenario = 'pkg'; status = 'fail'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winapp-package') } })
        Write-Runs $c3 @(@{ scenario = 'pkg'; status = 'pass'; skillsLoaded = @(); preflight = @{ expectedSkills = @('winapp-setup') } })
        $all = Get-ComparisonReport -Baseline $b3 -Candidate $c3 -Scenarios @($only)
        $all | Should -Match ([regex]::Escape('| m | pkg | both | 0/1 (0%) → -; excluded: 1 n/a | check differs |'))
    }

    It 'flags a candidate that removes a skill matched by a forbidden wildcard' {
        $wild = [pscustomobject]@{
            Id = 'wpf'; Configurations = @('both')
            Expect = [pscustomobject]@{ SkillsAny = @('winapp-package'); SkillsAll = @(); SkillsForbid = @('winui-*'); MaxSkills = $null }
        }
        $b4 = Join-Path $TestDrive 'base-forbid'
        $c4 = Join-Path $TestDrive 'cand-forbid'
        Write-Runs $b4 @(@{ scenario = 'wpf'; status = 'fail'; skillsLoaded = @('winapp-package', 'winui-code-review'); preflight = @{ expectedSkills = @('winapp-package', 'winui-design', 'winui-code-review') } })
        Write-Runs $c4 @(@{ scenario = 'wpf'; status = 'pass'; skillsLoaded = @('winapp-package'); preflight = @{ expectedSkills = @('winapp-package', 'winui-design') } })
        Get-ComparisonReport -Baseline $b4 -Candidate $c4 -Scenarios @($wild) |
            Should -Match ([regex]::Escape('| m | wpf | both | 0/1 (0%) → 1/1 (100%) | check differs |'))
    }

    It 'excludes runs of scenarios that no longer exist instead of scoring their stale status' {
        $b5 = Join-Path $TestDrive 'base-removed'
        $c5 = Join-Path $TestDrive 'cand-removed'
        Write-Runs $b5 @(@{ scenario = 'gone'; status = 'fail'; skillsLoaded = @() })
        Write-Runs $c5 @(@{ scenario = 'gone'; status = 'pass'; skillsLoaded = @() })
        $removed = Get-ComparisonReport -Baseline $b5 -Candidate $c5 -Scenarios @($scenario)
        $removed | Should -Match ([regex]::Escape('| m | gone | both | -; excluded: 1 scenario_removed → -; excluded: 1 scenario_removed | n/a |'))
        $removed | Should -Match ([regex]::Escape('| m | 1 (1 not pooled) | - → - | n/a |'))
    }

    It 'writes tables whose separator rows match their headers' {
        $lines = $report -split "`n"
        for ($i = 0; $i -lt $lines.Count - 1; $i++) {
            if ($lines[$i + 1] -match '^\|---') {
                ($lines[$i + 1] -split '\|').Count | Should -Be ($lines[$i] -split '\|').Count -Because $lines[$i]
            }
        }
    }
}
