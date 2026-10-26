#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    $script:validator = Join-Path $PSScriptRoot '..\Test-TrialFiles.ps1'
    $script:examples = Join-Path $PSScriptRoot '..\examples'
    $script:work = Join-Path ([IO.Path]::GetTempPath()) "trialfiles-$(Get-Random)"
    New-Item -ItemType Directory $script:work | Out-Null

    function Get-Example([string]$Benchmark) {
        $f = Get-ChildItem $script:examples -Filter *.json | Where-Object { $_.Name -like "*$Benchmark*" -or ($Benchmark -eq 'tasks' -and $_.Name -like 'winforms*') } | Select-Object -First 1
        Get-Content -Raw $f.FullName | ConvertFrom-Json -Depth 20
    }

    # Writes a mutated copy of an example and runs the validator on it in a separate process.
    function Invoke-Validator([scriptblock]$Mutate, [string]$Benchmark = 'tasks') {
        $trial = Get-Example $Benchmark
        & $Mutate $trial
        $file = Join-Path $script:work "$(Get-Random).json"
        $trial | ConvertTo-Json -Depth 20 | Set-Content $file -Encoding utf8NoBOM
        $out = & pwsh -NoProfile -File $script:validator -Path $file 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }
}

AfterAll { Remove-Item $script:work -Recurse -Force -ErrorAction SilentlyContinue }

Describe 'Test-TrialFiles.ps1' {
    It 'accepts the shipped examples' {
        $out = & pwsh -NoProfile -File $script:validator -Path $script:examples 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $out | Should -Match '2 of 2 trial file\(s\) valid'
    }

    It 'accepts a development trial (release is null)' {
        (Invoke-Validator { param($t) $t.release = $null }).ExitCode | Should -Be 0
    }

    It 'accepts an invalid-status trial with no result and passed null' {
        $r = Invoke-Validator { param($t)
            $t.status = 'invalid'; $t.passed = $null; $t | Add-Member invalidReason 'Sandbox failed to start'
            $t.PSObject.Properties.Remove('result')
        }
        $r.ExitCode | Should -Be 0
    }

    It 'rejects an unknown arm' {
        $r = Invoke-Validator { param($t) $t.arm = 'both' }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects a missing commit SHA' {
        (Invoke-Validator { param($t) $t.PSObject.Properties.Remove('commit') }).ExitCode | Should -Be 1
    }

    It 'rejects a short commit SHA' {
        (Invoke-Validator { param($t) $t.commit = '3f2a9c1' }).ExitCode | Should -Be 1
    }

    It 'rejects a dollar cost field' {
        $r = Invoke-Validator { param($t) $t.cost | Add-Member usd 1.5 }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects a tasks check without its required flag' {
        $r = Invoke-Validator { param($t) $t.result.checks[0].PSObject.Properties.Remove('required') }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects the old 0 / 0.5 / 1 score' {
        $r = Invoke-Validator { param($t) $t | Add-Member score 0.5 }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects a completed trial with passed null' {
        $r = Invoke-Validator { param($t) $t.passed = $null }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects tasks passed=true when a required check failed' {
        $r = Invoke-Validator { param($t) ($t.result.checks | Where-Object id -eq 'launches').passed = $false }
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'required checks say False'
    }

    It 'treats a failed optional tasks check as still passing' {
        # The example already has a failed optional check (msix-trusted) and passed = true.
        (Invoke-Validator { param($t) }).ExitCode | Should -Be 0
    }

    It 'rejects tasks with no required check' {
        $r = Invoke-Validator { param($t) foreach ($c in $t.result.checks) { $c.required = $false } }
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'no check is marked required'
    }

    It 'rejects apps passed=true when a gate failed' {
        $r = Invoke-Validator -Benchmark 'calc' { param($t) $t.result.gates[0].status = 'fail' }
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'gates say False'
    }

    It 'keeps apps passing when only judge results fail' {
        $r = Invoke-Validator -Benchmark 'calc' { param($t) $t.result.requirements | ForEach-Object { $_.status = 'fail' }; $t.result.designScore = 0 }
        $r.ExitCode | Should -Be 0
    }

    It 'rejects apps without a builds gate' {
        $r = Invoke-Validator -Benchmark 'calc' { param($t) $t.result.gates = @($t.result.gates | Where-Object id -ne 'builds') }
        $r.ExitCode | Should -Be 1
    }

    It 'rejects an apps design score above 10' {
        (Invoke-Validator -Benchmark 'calc' { param($t) $t.result.designScore = 11 }).ExitCode | Should -Be 1
    }

    It 'fails on a path that does not exist' {
        & pwsh -NoProfile -File $script:validator -Path (Join-Path $script:work 'nope.json') 2>&1 | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }
}
