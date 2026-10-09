#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\..\agents\lib\Benchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\UiBenchmark.psm1') -Force
    $script:UiRoot = Split-Path $PSScriptRoot
    $script:Scenarios = Get-UiScenarios -ScenariosRoot (Join-Path $script:UiRoot 'scenarios')

    function New-Scenario {
        # A valid calculator scenario definition; tests change one thing at a time.
        [ordered]@{
            id          = 'calc-x'
            app         = 'calculator'
            description = 'test'
            prompt      = 'Use Calculator to work out 1 + 1.'
            setup       = [ordered]@{ instances = @([ordered]@{ role = 'main'; target = $true }) }
            checks      = @([ordered]@{ type = 'value'; instance = 'main'; key = 'display'; equals = '2' })
        }
    }
}

Describe 'Scenario definitions' {
    It 'loads every checked-in scenario' {
        $ids = @($script:Scenarios.Id)
        $ids | Should -Contain 'calc-running'
        $ids | Should -Contain 'calc-not-running'
        $ids | Should -Contain 'calc-two-instances'
        $ids | Should -Contain 'calc-minimized-scientific'
        $ids | Should -Contain 'gallery-combobox'
        $ids | Should -Contain 'gallery-dialog-left-open'
        $ids | Should -Contain 'gallery-cold-start'
    }

    It 'gives every checked-in scenario exactly one target, or none when the app starts closed' {
        foreach ($s in $script:Scenarios) {
            $targets = @($s.Instances | Where-Object { $_.target })
            if ($s.Instances.Count) { $targets.Count | Should -Be 1 -Because $s.Id } else { $s.AllowedLaunches | Should -BeGreaterThan 0 -Because $s.Id }
        }
    }

    It 'accepts a valid definition' {
        Test-UiScenarioDefinition -Definition (New-Scenario) -FolderName 'calc-x' | Should -BeNullOrEmpty
    }

    It 'reports <name>' -ForEach @(
        @{ name = 'an id that differs from its folder'; change = { param($d) $d.id = 'calc-y' }; expect = "must match its folder" }
        @{ name = 'an unknown app'; change = { param($d) $d.app = 'notepad' }; expect = "app 'notepad' must be one of" }
        @{ name = 'an unknown field'; change = { param($d) $d.extra = 1 }; expect = "unknown field 'extra'" }
        @{ name = 'a missing prompt'; change = { param($d) $d.Remove('prompt') }; expect = "missing 'prompt'" }
        @{ name = 'two targets'; change = { param($d) $d.setup.instances += [ordered]@{ role = 'other'; target = $true } }; expect = 'exactly one setup instance must be the target' }
        @{ name = 'a duplicate role'; change = { param($d) $d.setup.instances += [ordered]@{ role = 'main' } }; expect = "duplicate instance role 'main'" }
        @{ name = 'a reserved role'; change = { param($d) $d.setup.instances[0].role = 'only' }; expect = "role 'only' is reserved" }
        @{ name = 'a gallery field on a calculator'; change = { param($d) $d.setup.instances[0].page = 'ComboBox' }; expect = 'applies to gallery scenarios only' }
        @{ name = 'an unknown mode'; change = { param($d) $d.setup.instances[0].mode = 'programmer' }; expect = 'mode must be one of' }
        @{ name = 'a value check on an unknown role'; change = { param($d) $d.checks[0].instance = 'ghost' }; expect = "instance 'ghost' is not a setup role" }
        @{ name = 'a value check on an unknown key'; change = { param($d) $d.checks[0].key = 'colors' }; expect = "key 'colors' must be one of" }
        @{ name = 'a value check with both equals and in'; change = { param($d) $d.checks[0].in = @('2') }; expect = 'needs exactly one of equals or in' }
        @{ name = 'an unknown check type'; change = { param($d) $d.checks += [ordered]@{ type = 'pixels' } }; expect = "unknown check type 'pixels'" }
        @{ name = 'a prompt that names winapp'; change = { param($d) $d.prompt = 'Use winapp to add 1 + 1 in Calculator.' }; expect = 'names the winapp CLI' }
        @{ name = 'a prompt that names a ui subcommand'; change = { param($d) $d.prompt = 'Run ui invoke on the 1 button.' }; expect = "names the command 'ui invoke'" }
        @{ name = 'a prompt that mentions UI Automation'; change = { param($d) $d.prompt = 'Use UI Automation to add 1 + 1.' }; expect = "mentions 'UI Automation'" }
    ) {
        $d = New-Scenario
        & $change $d
        (Test-UiScenarioDefinition -Definition $d -FolderName 'calc-x') -join "`n" | Should -Match ([regex]::Escape($expect))
    }

    It 'rejects a gallery prompt that contains an AutomationId the setup uses' {
        $d = [ordered]@{
            id = 'g'; app = 'gallery'; description = 'x'; prompt = 'Press ShowDialog, then cancel it.'
            setup = [ordered]@{ instances = @([ordered]@{ role = 'main'; target = $true; invoke = @('ShowDialog') }) }
            checks = @([ordered]@{ type = 'instanceCount'; equals = 1 })
        }
        (Test-UiScenarioDefinition -Definition $d) -join "`n" | Should -Match "AutomationId 'ShowDialog'"
    }

    It 'hashes everything but the description' {
        $a = New-Scenario
        $b = New-Scenario; $b.description = 'reworded'
        $c = New-Scenario; $c.prompt = 'Use Calculator to work out 2 + 2.'
        Get-ScenarioHash $a | Should -Be (Get-ScenarioHash $b)
        Get-ScenarioHash $a | Should -Not -Be (Get-ScenarioHash $c)
    }

    It 'lists every invalid scenario when loading a folder' {
        $root = Join-Path $TestDrive 'scenarios'
        foreach ($id in 'calc-a', 'calc-b') {
            New-Item -ItemType Directory -Force (Join-Path $root $id) | Out-Null
            $d = New-Scenario; $d.id = $id; $d.app = 'paint'
            $d | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root "$id\scenario.json")
        }
        { Get-UiScenarios -ScenariosRoot $root } | Should -Throw '*calc-a: app*calc-b: app*'
    }
}

Describe 'Method definitions' {
    It 'loads the checked-in winapp method with the plugin skills it installs' {
        $m = Get-UiMethod -NameOrPath 'winapp' -MethodsRoot (Join-Path $script:UiRoot 'methods')
        $m.CheckedIn | Should -BeTrue
        $m.Plugins.Count | Should -Be 1
        Test-Path (Join-Path $m.Plugins[0] 'plugin.json') | Should -BeTrue
        $m.ExpectedSkills | Should -Contain 'winapp-ui-automation'
        $m.Requires | Should -Be @('winapp')
        $m.HideCommands | Should -BeNullOrEmpty
    }

    It 'loads the checked-in none method, which hides winapp and installs nothing' {
        $m = Get-UiMethod -NameOrPath 'none' -MethodsRoot (Join-Path $script:UiRoot 'methods')
        $m.Plugins | Should -BeNullOrEmpty
        $m.ExpectedSkills | Should -BeNullOrEmpty
        $m.HideCommands | Should -Be @('winapp')
    }

    It 'loads a local method file and resolves its paths relative to the file' {
        $dir = Join-Path $TestDrive 'local'
        New-Item -ItemType Directory -Force (Join-Path $dir 'my-skill') | Out-Null
        Set-Content (Join-Path $dir 'my-skill\SKILL.md') "---`nname: my-tool-skill`ndescription: x`n---`nbody"
        @{
            name = 'my-tool'; displayName = 'My tool'; skills = @('my-skill'); path = @('bin')
            mcpServers = @{ tool = @{ command = 'tool.exe'; args = @('--stdio') } }
            allowTools = @('tool'); env = @{ TOOL_TOKEN = '${env:MY_TOKEN}' }
        } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'my-tool.method.json')
        $m = Get-UiMethod -NameOrPath (Join-Path $dir 'my-tool.method.json') -MethodsRoot (Join-Path $script:UiRoot 'methods')
        $m.Name | Should -Be 'my-tool'
        $m.CheckedIn | Should -BeFalse
        $m.ExpectedSkills | Should -Be @('my-tool-skill')
        $m.Path | Should -Be @([System.IO.Path]::GetFullPath((Join-Path $dir 'bin')))
        $m.McpServers.tool.command | Should -Be 'tool.exe'
    }

    It 'names the known methods when one is unknown' {
        { Get-UiMethod -NameOrPath 'nope' -MethodsRoot (Join-Path $script:UiRoot 'methods') } | Should -Throw '*none*winapp*'
    }

    It 'reports <name>' -ForEach @(
        @{ name = 'an unknown field'; def = @{ name = 'a'; tools = @('x') }; expect = "unknown field 'tools'" }
        @{ name = 'a bad name'; def = @{ name = 'My Tool' }; expect = 'name must be lowercase' }
        @{ name = 'a string where a list belongs'; def = @{ name = 'a'; allowTools = 'shell' }; expect = 'allowTools must be a list' }
        @{ name = 'PATH in env'; def = @{ name = 'a'; env = @{ PATH = 'x' } }; expect = 'env.PATH is managed by the harness' }
        @{ name = 'an MCP server without a command or url'; def = @{ name = 'a'; mcpServers = @{ s = @{ args = @() } } }; expect = 'mcpServers.s needs a command or a url' }
        @{ name = 'a command both hidden and required'; def = @{ name = 'a'; hideCommands = @('winapp'); requires = @('winapp') }; expect = "'winapp' is both required and hidden" }
        @{ name = 'a plugin folder that does not exist'; def = @{ name = 'a'; plugins = @('missing-plugin') }; expect = 'has no plugin.json or skills folder' }
    ) {
        (Test-UiMethodDefinition -Definition $def -BaseDirectory $TestDrive) -join "`n" | Should -Match ([regex]::Escape($expect))
    }

    It 'expands ${env:NAME} references and reports missing ones' {
        $r = Expand-MethodEnv -Env ([ordered]@{ A = 'Bearer ${env:TOK}'; B = '${env:NOPE}-x'; C = 'plain' }) -Source @{ TOK = 'secret' }
        $r.Values.A | Should -Be 'Bearer secret'
        $r.Values.B | Should -Be '-x'
        $r.Values.C | Should -Be 'plain'
        $r.Missing | Should -Be @('NOPE')
    }

    It 'prepends PATH entries whatever the key casing' {
        $envMap = [ordered]@{ PATH = 'C:\a;C:\b' }
        Set-EnvironmentPath -Environment $envMap -Prepend @('C:\shim', '') | Should -Be 'C:\shim;C:\a;C:\b'
        @($envMap.Keys) | Should -Be @('PATH')
    }

    It 'hides a command with a shim placed first on PATH' {
        $real = Join-Path $TestDrive 'real'; $shim = Join-Path $TestDrive 'shim'
        New-Item -ItemType Directory -Force $real | Out-Null
        Set-Content (Join-Path $real 'winapp.exe') 'x'
        Resolve-CommandOnPath -Name 'winapp' -PathValue $real | Should -Be (Join-Path $real 'winapp.exe')

        New-HiddenCommandShim -Directory $shim -Names @('winapp')
        Resolve-CommandOnPath -Name 'winapp' -PathValue "$shim;$real" -PathExt '.COM;.EXE;.BAT;.CMD' | Should -Be (Join-Path $shim 'winapp.cmd')
        $out = & cmd.exe /d /c (Join-Path $shim 'winapp.cmd') 2>&1
        $LASTEXITCODE | Should -Be 9009
        "$out" | Should -Match 'is not recognized'
    }

    It 'flags skills from outside the isolated home and expected skills that are missing' {
        $home_ = 'C:\iso\home'
        $listed = @(
            [pscustomobject]@{ name = 'winappcli:winapp-ui-automation'; source = 'plugin'; path = "$home_\installed\x\SKILL.md"; enabled = $true }
            [pscustomobject]@{ name = 'stray'; source = 'personal'; path = 'C:\Users\me\.copilot\skills\stray\SKILL.md'; enabled = $true }
            [pscustomobject]@{ name = 'builtin-thing'; source = 'builtin'; path = 'x' }
        )
        $r = Test-MethodSkillList -Expected @('winapp-ui-automation', 'winapp-setup') -Listed $listed -CopilotHome $home_
        $r.Unexpected | Should -Be @('stray (personal)')
        $r.Missing | Should -Be @('winapp-setup')
    }
}

Describe 'Scoring from recorded app state' {
    BeforeDiscovery {
        $cases = Get-Content -Raw (Join-Path $PSScriptRoot 'fixtures\scoring-cases.json') | ConvertFrom-Json
    }

    It '<name>' -ForEach $cases {
        $scenario = @($script:Scenarios | Where-Object Id -eq $_.scenario)[0]
        $r = Test-ScenarioState -Scenario $scenario -Before $_.before -After $_.after

        $r.Status | Should -Be $_.expect.status
        @($r.Failures).Count | Should -Be @($_.expect.failures).Count -Because ($r.Failures -join ' | ')
        for ($i = 0; $i -lt @($_.expect.failures).Count; $i++) {
            $r.Failures[$i] | Should -BeLike "$($_.expect.failures[$i])*"
        }
        @($r.Collateral.type) | Should -Be @($_.expect.collateral)
        if ($_.expect.PSObject.Properties['notes']) { @($r.Notes) | Should -Be @($_.expect.notes) }
    }

    It 'treats a pid with no recorded start time as the same process' {
        Test-SameProcess ([pscustomobject]@{ pid = 5 }) ([pscustomobject]@{ pid = 5; startTime = 't' }) | Should -BeTrue
        Test-SameProcess ([pscustomobject]@{ pid = 5; startTime = 'a' }) ([pscustomobject]@{ pid = 5; startTime = 'b' }) | Should -BeFalse
    }

    It 'compares values case-insensitively and booleans as text' {
        Format-StateValue ' Green ' | Should -Be 'green'
        Format-StateValue $false | Should -Be 'false'
        Format-StateValue $null | Should -BeNullOrEmpty
    }
}

Describe 'Resume and plan' {
    BeforeAll {
        $script:Method = [pscustomobject]@{ Name = 'winapp'; Hash = 'm1' }
        $script:Scenario = [pscustomobject]@{ Id = 'calc-running'; Hash = 's1' }
        $script:Runs = @(1..3 | ForEach-Object { [pscustomobject]@{ Method = $script:Method; Model = 'claude-sonnet-5.5'; Scenario = $script:Scenario; Iteration = $_ } })
        function New-Record([int]$Iteration, [string]$Status, [string]$ScenarioHash = 's1', [string]$MethodHash = 'm1', $Credits = 10, $TotalMs = 120000) {
            [pscustomobject]@{
                method = 'winapp'; model = 'claude-sonnet-5.5'; scenario = 'calc-running'; iteration = $Iteration; status = $Status
                scenarioHash = $ScenarioHash; methodHash = $MethodHash; durationMs = 60000; totalMs = $TotalMs; aiCredits = $Credits
            }
        }
    }

    It 'plans every cell when nothing ran yet, from the default estimates' {
        $p = Get-RunPlan -Runs $script:Runs -Records @() -CreditEstimate 15 -MinutesEstimate 2
        $p.Total | Should -Be 3
        $p.Done | Should -Be 0
        $p.Pending.Count | Should -Be 3
        $p.Credits | Should -Be 45
        $p.Minutes | Should -Be 6
    }

    It 'accepts null records (an empty runs.jsonl)' {
        $p = Get-RunPlan -Runs $script:Runs -Records @($null)
        $p.Pending.Count | Should -Be 3
    }

    It 'skips scored cells and reruns harness errors' {
        $records = @((New-Record 1 'pass'), (New-Record 2 'fail'), (New-Record 3 'error' -Credits $null))
        $p = Get-RunPlan -Runs $script:Runs -Records $records
        $p.Done | Should -Be 2
        @($p.Pending.Iteration) | Should -Be @(3)
    }

    It 'uses the latest record of a cell' {
        $records = @((New-Record 1 'error'), (New-Record 1 'pass'))
        (Get-RunPlan -Runs $script:Runs[0] -Records $records).Done | Should -Be 1
        $records = @((New-Record 1 'pass'), (New-Record 1 'error'))
        (Get-RunPlan -Runs $script:Runs[0] -Records $records).Done | Should -Be 0
    }

    It 'reruns cells whose scenario or method definition changed' {
        Test-CellDone (New-Record 1 'pass') 's1' 'm1' | Should -BeTrue
        Test-CellDone (New-Record 1 'pass' -ScenarioHash 'old') 's1' 'm1' | Should -BeFalse
        Test-CellDone (New-Record 1 'pass' -MethodHash 'old') 's1' 'm1' | Should -BeFalse
        Test-CellDone $null 's1' 'm1' | Should -BeFalse
    }

    It 'estimates the remaining cells from measured runs' {
        $records = @((New-Record 1 'pass' -Credits 8 -TotalMs 60000), (New-Record 2 'pass' -Credits 12 -TotalMs 180000))
        $p = Get-RunPlan -Runs $script:Runs -Records $records -CreditEstimate 99 -MinutesEstimate 99
        $p.CreditsPerRun | Should -Be 10
        $p.Credits | Should -Be 10
        $p.MinutesPerRun | Should -Be 2
    }

    It 'skips an unreadable line of runs.jsonl' {
        $file = Join-Path $TestDrive 'runs.jsonl'
        Set-Content $file @(((New-Record 1 'pass') | ConvertTo-Json -Compress), '{"method": "winapp", "trunc')
        @(Read-RunRecords $file -WarningAction SilentlyContinue).Count | Should -Be 1
    }
}

Describe 'Reports' {
    BeforeAll {
        function Write-Runs([string]$Dir, [object[]]$Records) {
            New-Item -ItemType Directory -Force $Dir | Out-Null
            Set-Content (Join-Path $Dir 'runs.jsonl') @($Records | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 5 })
        }
        $script:Base = @{ method = 'winapp'; model = 'claude-sonnet-5.5'; durationMs = 60000; modelTurns = 6; toolCalls = 9; toolTimeMs = 15000; apiDurationMs = 40000; tokens = @{ input = 1000; output = 200 }; aiCredits = 12 }
        function New-Run([string]$Scenario, [int]$Iteration, [string]$Status, [hashtable]$Extra = @{}) {
            $r = $script:Base.Clone(); $r.scenario = $Scenario; $r.iteration = $Iteration; $r.status = $Status
            foreach ($k in $Extra.Keys) { $r[$k] = $Extra[$k] }
            [pscustomobject]$r
        }
    }

    It 'writes pass rates, medians, and a note for each run that did not pass' {
        $dir = Join-Path $TestDrive 'a'
        Write-Runs $dir @(
            (New-Run 'calc-running' 1 'pass')
            (New-Run 'calc-running' 2 'fail' @{ failures = @("display of the 'main' instance is '0'; expected '579'"); collateral = @(@{ type = 'extra-instance'; detail = 'launched 1 new calculator instance(s)' }) })
            (New-Run 'calc-two-instances' 1 'pass' @{ notes = @('pid 4 was minimized; the harness restored it to read its state') })
        )
        $out = Join-Path $dir 'summary.md'
        Write-UiSummary -RunsPath (Join-Path $dir 'runs.jsonl') -SummaryPath $out -Header ([ordered]@{ Model = 'claude-sonnet-5.5' }) -ScenarioOrder @('calc-two-instances', 'calc-running')
        $md = Get-Content -Raw $out
        $md | Should -Match '\| winapp \| claude-sonnet-5\.5 \| 2/3 \(67%\) \| 60\.0s \| 6 \| 9 \| 15\.0s \| 40\.0s \| 1\.2k \| 36\.0 \| 1 \|'
        $md | Should -Match "\*\*calc-running\*\* · winapp · claude-sonnet-5\.5 · #2 · fail \(60\.0s\): display of the 'main' instance is '0'; expected '579'; \[extra-instance\] launched 1"
        $md | Should -Match '## Scoring notes'
        $md.IndexOf('| calc-two-instances |') | Should -BeLessThan $md.IndexOf('| calc-running |')
    }

    It 'compares two result folders per scenario and overall' {
        $a = Join-Path $TestDrive 'cmp-a'; $b = Join-Path $TestDrive 'cmp-b'
        Write-Runs $a @((New-Run 'calc-running' 1 'pass'), (New-Run 'calc-running' 2 'fail'))
        Write-Runs $b @((New-Run 'calc-running' 1 'pass' @{ durationMs = 30000 }), (New-Run 'calc-running' 2 'pass' @{ durationMs = 30000 }))
        $md = Get-UiComparison -ResultDirs @($a, $b)
        $md | Should -Match '\| calc-running \| winapp \| claude-sonnet-5\.5 \| cmp-a \| 50% \(1/2\) \| 60\.0s'
        $md | Should -Match '\| calc-running \| winapp \| claude-sonnet-5\.5 \| cmp-b \| 100% \(2/2\) \| 30\.0s'
        $md | Should -Match '\| \*\*all\*\* \| winapp'
    }

    It 'refuses to compare a folder without runs' {
        $empty = Join-Path $TestDrive 'empty'; New-Item -ItemType Directory -Force $empty | Out-Null
        { Get-UiComparison -ResultDirs @($empty) } | Should -Throw '*No runs.jsonl records*'
    }
}
