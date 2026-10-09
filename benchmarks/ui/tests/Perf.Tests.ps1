#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\..\agents\lib\Benchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\UiBenchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\Perf.psm1') -Force
    $script:UiRoot = Split-Path $PSScriptRoot
    $script:AdaptersRoot = Join-Path $script:UiRoot 'adapters'
    $script:Scenarios = Get-UiScenarios -ScenariosRoot (Join-Path $script:UiRoot 'scenarios')

    function New-Adapter {
        [ordered]@{
            name       = 'fake'
            command    = 'fake.exe'
            operations = [ordered]@{
                'version' = @('--version')
                'invoke'  = @('click', '{selector}', '--hwnd', '{hwnd}', @('--action', '{action}'))
            }
        }
    }

    function New-PerfConfig {
        [ordered]@{
            repeat         = 5
            warmup         = 1
            sequenceRepeat = 2
            timeoutSeconds = 30
            operations     = @(
                [ordered]@{ id = 'startup'; op = 'version' }
                [ordered]@{ id = 'press'; op = 'invoke'; scenario = 'calc-running'; selector = 'num1Button' }
            )
            sequences      = @(
                [ordered]@{ id = 'seq'; scenario = 'calc-running'; steps = @([ordered]@{ op = 'invoke'; selector = 'num1Button' }) }
            )
        }
    }

    function Write-Results {
        # Writes a perf-results.json the way perf.ps1 does and returns its folder.
        param([string]$Name, [double[]]$OpMs, [object[]]$Sequences = @(), [int]$Errors = 0)
        $dir = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $results = [ordered]@{
            started      = '2025-01-01T00:00:00Z'
            finished     = '2025-01-01T00:05:00Z'
            environment  = [ordered]@{ os = 'Windows 11'; architecture = 'Arm64'; cpuCount = 12; cpu = 'Test CPU'; repoCommit = 'abc1234'; repoDirty = $false }
            adapters     = @([ordered]@{ name = 'winapp'; displayName = 'winapp CLI'; version = '1.0'; commandPath = 'C:\winapp.exe' })
            settings     = [ordered]@{ repeat = $OpMs.Count; warmup = 1; sequenceRepeat = @($Sequences).Count }
            stoppedEarly = $null
            operations   = @([ordered]@{ id = 'invoke'; adapter = 'winapp'; stats = (Get-PerfStats $OpMs); errors = $Errors; firstError = $(if ($Errors) { 'element not found' } else { $null }) })
            sequences    = @($Sequences)
        }
        $results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $dir 'perf-results.json') -Encoding utf8NoBOM
        return $dir
    }

    function New-SequenceRun {
        param([int]$Iteration, [string]$Status = 'pass', [double[]]$StepMs = @(100, 200), [string]$Reason = '')
        [ordered]@{
            id = 'calc-add'; adapter = 'winapp'; iteration = $Iteration; status = $Status; reason = $Reason
            commandMs = ($StepMs | Measure-Object -Sum).Sum
            steps = @(for ($i = 0; $i -lt $StepMs.Count; $i++) { [ordered]@{ label = "step$i"; ms = $StepMs[$i] } })
        }
    }
}

Describe 'Expand-AdapterArgs' {
    It 'fills placeholders in plain arguments' {
        $out = Expand-AdapterArgs -Template @('ui', 'invoke', '{selector}', '-w', '{hwnd}') -Values @{ selector = 'num1Button'; hwnd = 123 }
        $out | Should -Be @('ui', 'invoke', 'num1Button', '-w', '123')
    }

    It 'keeps an optional group when all of its placeholders have values' {
        $out = Expand-AdapterArgs -Template @('x', @('--type', '{type}')) -Values @{ type = 'Button' }
        $out | Should -Be @('x', '--type', 'Button')
    }

    It 'drops an optional group when a placeholder has no value' {
        $out = Expand-AdapterArgs -Template @('x', @('--type', '{type}'), @('-t', '{timeout}')) -Values @{ type = ''; timeout = $null }
        $out | Should -Be @('x')
    }

    It 'fills placeholders embedded in a longer argument' {
        $out = Expand-AdapterArgs -Template @('--out={output}') -Values @{ output = 'C:\a.png' }
        $out | Should -Be @('--out=C:\a.png')
    }

    It 'throws when a required placeholder has no value' {
        { Expand-AdapterArgs -Template @('ui', '{selector}') -Values @{} } | Should -Throw '*needs a value for {selector}*'
    }

    It 'returns an empty array for an empty template' {
        $out = Expand-AdapterArgs -Template @() -Values @{}
        @($out).Count | Should -Be 0
    }
}

Describe 'Adapter definitions' {
    It 'accepts a minimal valid adapter' {
        @(Test-PerfAdapterDefinition -Definition (New-Adapter)) | Should -BeNullOrEmpty
    }

    It 'reports <because>' -ForEach @(
        @{ because = 'a missing command'; change = { param($d) $d.Remove('command') }; error = "missing 'command'" }
        @{ because = 'a bad name'; change = { param($d) $d.name = 'Bad Name' }; error = "name 'Bad Name' must be*" }
        @{ because = 'an unknown field'; change = { param($d) $d.extra = 1 }; error = "unknown field 'extra'" }
        @{ because = 'an unknown operation'; change = { param($d) $d.operations.teleport = @('x') }; error = "unknown operation 'teleport'*" }
        @{ because = 'an unknown placeholder'; change = { param($d) $d.operations.version = @('{nope}') }; error = "operation 'version': unknown placeholder '{nope}'*" }
        @{ because = 'a non-string argument'; change = { param($d) $d.operations.version = @(5) }; error = "operation 'version': arguments must be strings*" }
        @{ because = 'a non-object env'; change = { param($d) $d.env = 'x' }; error = 'env must be an object*' }
        @{ because = 'empty operations'; change = { param($d) $d.operations = [ordered]@{} }; error = 'operations must not be empty' }
    ) {
        $d = New-Adapter
        & $change $d
        @(Test-PerfAdapterDefinition -Definition $d) | Should -BeLike $error
    }

    It 'loads the checked-in winapp adapter with every operation' {
        $a = Read-PerfAdapter -NameOrPath 'winapp' -AdaptersRoot $script:AdaptersRoot
        $a.Name | Should -Be 'winapp'
        $a.CheckedIn | Should -BeTrue
        $a.CommandIsPath | Should -BeFalse
        foreach ($op in 'version', 'list-windows', 'search', 'inspect', 'get-property', 'get-value', 'invoke', 'set-value', 'wait-for', 'focus', 'screenshot') {
            $a.Operations.Contains($op) | Should -BeTrue -Because $op
        }
    }

    It 'loads a local adapter by path and resolves a relative command against its folder' {
        $d = New-Adapter
        $d.command = '.\bin\tool.exe'
        $file = Join-Path $TestDrive 'local.adapter.json'
        $d | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $file
        $a = Read-PerfAdapter -NameOrPath $file -AdaptersRoot $script:AdaptersRoot
        $a.CheckedIn | Should -BeFalse
        $a.CommandIsPath | Should -BeTrue
        $a.Command | Should -Be (Join-Path $TestDrive 'bin\tool.exe')
    }

    It 'lists the checked-in adapters when one is not found' {
        { Read-PerfAdapter -NameOrPath 'missing' -AdaptersRoot $script:AdaptersRoot } | Should -Throw '*Checked-in adapters: winapp*'
    }

    It 'rejects an invalid adapter file with every problem' {
        $file = Join-Path $TestDrive 'bad.json'
        '{ "name": "bad", "command": "x", "operations": { "teleport": [] }, "extra": 1 }' | Set-Content -LiteralPath $file
        { Read-PerfAdapter -NameOrPath $file -AdaptersRoot $script:AdaptersRoot } | Should -Throw "*unknown field 'extra'*"
    }
}

Describe 'Get-AdapterInvocation' {
    BeforeAll { $script:Winapp = Read-PerfAdapter -NameOrPath 'winapp' -AdaptersRoot $script:AdaptersRoot }

    It 'builds a winapp invoke command line with an action' {
        $inv = Get-AdapterInvocation -Adapter $script:Winapp -Operation 'invoke' -Values @{ selector = 'Combo1'; hwnd = 123; action = 'expand' }
        $inv.Arguments | Should -Be @('ui', 'invoke', 'Combo1', '-w', '123', '--action', 'expand')
    }

    It 'leaves out optional arguments that the step does not set' {
        $inv = Get-AdapterInvocation -Adapter $script:Winapp -Operation 'wait-for' -Values @{ selector = 'Combo1'; hwnd = 9 }
        $inv.Arguments | Should -Be @('ui', 'wait-for', 'Combo1', '-w', '9')
    }

    It 'makes inspect selector optional' {
        (Get-AdapterInvocation -Adapter $script:Winapp -Operation 'inspect' -Values @{ hwnd = 9 }).Arguments | Should -Be @('ui', 'inspect', '-w', '9')
    }

    It 'puts prefix arguments first and can use the adapter folder' {
        $a = [pscustomobject]@{ PrefixArgs = @('{adapterDir}\tool.ps1'); Operations = @{ version = @('--version') }; Directory = 'C:\adapters' }
        (Get-AdapterInvocation -Adapter $a -Operation 'version').Arguments | Should -Be @('C:\adapters\tool.ps1', '--version')
    }

    It 'returns nothing for an operation the adapter does not implement' {
        $a = [pscustomobject]@{ PrefixArgs = @(); Operations = @{ version = @('--version') }; Directory = 'C:\' }
        Get-AdapterInvocation -Adapter $a -Operation 'screenshot' | Should -BeNullOrEmpty
    }
}

Describe 'perf.json definitions' {
    It 'accepts a minimal valid config' {
        @(Test-PerfConfigDefinition -Definition (New-PerfConfig) -Scenarios $script:Scenarios) | Should -BeNullOrEmpty
    }

    It 'reports <because>' -ForEach @(
        @{ because = 'a non-positive repeat'; change = { param($d) $d.repeat = 0 }; error = 'repeat must be a positive integer' }
        @{ because = 'a negative warm-up'; change = { param($d) $d.warmup = -1 }; error = 'warmup must be a non-negative integer' }
        @{ because = 'an unknown scenario'; change = { param($d) $d.operations[1].scenario = 'nope' }; error = "operation 'press': unknown scenario 'nope'" }
        @{ because = 'a scenario that starts no app'; change = { param($d) $d.operations[1].scenario = 'calc-not-running' }; error = "operation 'press': scenario 'calc-not-running' starts no app*" }
        @{ because = 'an unknown instance'; change = { param($d) $d.operations[1].instance = 'ghost' }; error = "operation 'press': scenario 'calc-running' has no instance 'ghost'" }
        @{ because = 'a missing selector'; change = { param($d) $d.operations[1].Remove('selector') }; error = "operation 'press': op 'invoke' needs 'selector'" }
        @{ because = 'a missing scenario'; change = { param($d) $d.operations[1].Remove('scenario') }; error = "operation 'press' needs a scenario*" }
        @{ because = 'a duplicate id'; change = { param($d) $d.operations[1].id = 'startup' }; error = "duplicate operation id 'startup'" }
        @{ because = 'an unknown op'; change = { param($d) $d.operations[1].op = 'teleport' }; error = "operation 'press': unknown op 'teleport'*" }
        @{ because = 'a bad timeout'; change = { param($d) $d.operations[1].timeout = 'soon' }; error = "operation 'press': timeout must be*" }
        @{ because = 'a sequence without steps'; change = { param($d) $d.sequences[0].steps = @() }; error = "sequence 'seq' has no steps" }
        @{ because = 'a bad sequence step'; change = { param($d) $d.sequences[0].steps[0].Remove('selector') }; error = "sequence 'seq' step 1: op 'invoke' needs 'selector'" }
        @{ because = 'a step instance the scenario lacks'; change = { param($d) $d.sequences[0].steps[0].instance = 'ghost' }; error = "sequence 'seq' step 1: scenario 'calc-running' has no instance 'ghost'" }
    ) {
        $d = New-PerfConfig
        & $change $d
        @(Test-PerfConfigDefinition -Definition $d -Scenarios $script:Scenarios) | Should -BeLike $error
    }

    It 'loads the checked-in perf.json against the checked-in scenarios' {
        $c = Read-PerfConfig -Path (Join-Path $script:UiRoot 'perf.json') -Scenarios $script:Scenarios
        $c.Repeat | Should -BeGreaterThan 0
        @($c.Operations).Count | Should -BeGreaterThan 0
        @($c.Sequences).Count | Should -BeGreaterThan 0
        ($c.Sequences | Where-Object Id -eq 'calc-add').Steps[1].Params.selector | Should -Be 'num1Button'
    }

    It 'uses only operations the winapp adapter implements' {
        $c = Read-PerfConfig -Path (Join-Path $script:UiRoot 'perf.json') -Scenarios $script:Scenarios
        $a = Read-PerfAdapter -NameOrPath 'winapp' -AdaptersRoot $script:AdaptersRoot
        $plan = Get-PerfPlan -Config $c -Adapters @($a) -Operations $c.Operations -Sequences $c.Sequences
        $plan.Skipped | Should -BeNullOrEmpty
    }
}

Describe 'Get-PerfPlan' {
    It 'counts warm-up and repeats per operation, and skips what an adapter lacks' {
        $config = [pscustomobject]@{ Warmup = 1; Repeat = 4; SequenceRepeat = 2 }
        $full = [pscustomobject]@{ Name = 'full'; Operations = @{ version = 1; invoke = 1; inspect = 1 } }
        $thin = [pscustomobject]@{ Name = 'thin'; Operations = @{ version = 1 } }
        $ops = @(
            [pscustomobject]@{ Id = 'startup'; Op = 'version'; Scenario = '' }
            [pscustomobject]@{ Id = 'press'; Op = 'invoke'; Scenario = 'calc-running' }
        )
        $seqs = @([pscustomobject]@{ Id = 'add'; Steps = @([pscustomobject]@{ Op = 'inspect' }, [pscustomobject]@{ Op = 'invoke' }, [pscustomobject]@{ Op = 'invoke' }) })
        $plan = Get-PerfPlan -Config $config -Adapters @($full, $thin) -Operations $ops -Sequences $seqs -SecondsPerCall 1 -SecondsPerSetup 10
        $plan.OperationCalls | Should -Be 15        # startup x2 adapters, press x1 adapter, 5 calls each
        $plan.SequenceRuns | Should -Be 2
        $plan.SequenceCalls | Should -Be 6
        $plan.Skipped | Should -Be @('thin: press', 'thin: sequence add (no inspect, invoke)')
        $plan.Minutes | Should -Be 1                # (15 + 6) s + (1 target + 2 runs) x 10 s = 51 s
    }
}

Describe 'Statistics' {
    It 'computes median, nearest-rank p95, min, and max' {
        $s = Get-PerfStats @(1..20)
        $s.count | Should -Be 20
        $s.median | Should -Be 10.5
        $s.p95 | Should -Be 19
        $s.min | Should -Be 1
        $s.max | Should -Be 20
    }

    It 'uses the top sample as p95 for small samples' {
        (Get-PerfStats @(5, 1, 3)).p95 | Should -Be 5
    }

    It 'returns empty stats without samples' {
        $s = Get-PerfStats @()
        $s.count | Should -Be 0
        $s.median | Should -BeNullOrEmpty
        Format-PerfMs $s.median | Should -Be 'n/a'
    }

    It 'summarizes sequence runs' {
        $runs = @(New-SequenceRun 1) + @(New-SequenceRun 2 -StepMs 100, 400) + @(New-SequenceRun 3 -Status fail -Reason 'display reads 0')
        $runs = $runs | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $s = Get-SequenceStats $runs
        $s.Runs | Should -Be 3
        $s.Passed | Should -Be 2
        $s.Total.median | Should -Be 300
        $s.Calls | Should -Be 2
    }
}

Describe 'Perf reports' {
    It 'writes a summary with operations, per-step medians, and failures' {
        $seqs = @((New-SequenceRun 1), (New-SequenceRun 2 -StepMs 300, 200), (New-SequenceRun 3 -Status fail -Reason "display: 'main' reads '0'"))
        $dir = Write-Results -Name 'one' -OpMs @(100, 120, 140) -Sequences $seqs -Errors 2
        $results = Read-PerfResults $dir
        $md = Join-Path $dir 'summary.md'
        Write-PerfSummary -Results $results -Path $md
        $text = Get-Content -Raw -LiteralPath $md
        $text | Should -Match '\| invoke \| winapp \| 3 \| 2 \| 120 ms \| 140 ms \| 140 ms \|'
        $text | Should -Match '\| calc-add \| winapp \| 2/3 \| 2 \| 300 ms \|'
        $text | Should -Match 'step0 100 ms → step1 200 ms'
        $text | Should -Match "first: element not found"
        $text | Should -Match "sequence \*\*calc-add\*\* · winapp · #3 · fail: display: 'main' reads '0'"
        $text | Should -Match 'Windows 11, Arm64, 12 CPUs'
    }

    It 'says None when nothing failed' {
        $dir = Write-Results -Name 'clean' -OpMs @(100) -Sequences @(New-SequenceRun 1)
        Write-PerfSummary -Results (Read-PerfResults $dir) -Path (Join-Path $dir 'summary.md')
        Get-Content -Raw (Join-Path $dir 'summary.md') | Should -Match "## Failures\r?\n\r?\nNone\."
    }

    It 'compares medians relative to the first result folder' {
        $before = Write-Results -Name 'before' -OpMs @(200, 200, 200) -Sequences @((New-SequenceRun 1 -StepMs 500, 500))
        $after = Write-Results -Name 'after' -OpMs @(100, 100, 100) -Sequences @((New-SequenceRun 1 -StepMs 250, 250))
        $md = Get-PerfComparison -ResultDirs $before, $after
        $md | Should -Match '`before` vs `after`'
        $md | Should -Match '\| invoke \| winapp \| 200 ms \| 100 ms \| -50% \|'
        $md | Should -Match '\| calc-add \| winapp \| 1/1, 1,000 ms \| 1/1, 500 ms \| -50% \|'
    }

    It 'shows n/a for a sequence missing from one folder' {
        $before = Write-Results -Name 'b2' -OpMs @(100) -Sequences @((New-SequenceRun 1))
        $after = Write-Results -Name 'a2' -OpMs @(100)
        Get-PerfComparison -ResultDirs $before, $after | Should -Match '\| calc-add \| winapp \| 1/1, 300 ms \| n/a \|'
    }

    It 'fails clearly when a folder has no results' {
        { Read-PerfResults (Join-Path $TestDrive 'nothing') } | Should -Throw '*No perf-results.json*'
    }
}
