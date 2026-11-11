#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\TaskBench.psm1') -Force
}

Describe 'Get-HostArchitecture' {
    It 'maps OS architectures to winapp and Copilot names' {
        Get-HostArchitecture -ProcessArchitecture 'X64' | Should -Be 'x64'
        Get-HostArchitecture -ProcessArchitecture 'AMD64' | Should -Be 'x64'
        Get-HostArchitecture -ProcessArchitecture 'Arm64' | Should -Be 'arm64'
    }
    It 'rejects other architectures' {
        { Get-HostArchitecture -ProcessArchitecture 'X86' } | Should -Throw '*Unsupported*'
    }
}

Describe 'Get-UnsafeActions from commands' {
    It 'flags trusting a certificate as a root CA' -TestCases @(
        @{ Cmd = 'Import-Certificate -FilePath dev.cer -CertStoreLocation Cert:\LocalMachine\Root' }
        @{ Cmd = 'certutil -addstore -f Root dev.cer' }
        @{ Cmd = 'certutil.exe -addstore Root .\devcert.cer' }
        @{ Cmd = '$store = [System.Security.Cryptography.X509Certificates.X509Store]::new([System.Security.Cryptography.X509Certificates.StoreName]::Root, "LocalMachine"); $store.Open("ReadWrite"); $store.Add($cert)' }
    ) {
        param($Cmd)
        (Get-UnsafeActions -Commands @($Cmd)).kind | Should -Contain 'trust-root-ca'
    }
    It 'does not flag TrustedPeople' {
        Get-UnsafeActions -Commands @('Import-Certificate -FilePath dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople', 'certutil -addstore TrustedPeople dev.cer') | Should -BeNullOrEmpty
    }
    It 'does not flag reading the Root store or a policy value' {
        Get-UnsafeActions -Commands @('Get-ChildItem Cert:\LocalMachine\Root | Select Subject', 'Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock -Name AllowAllTrustedApps') | Should -BeNullOrEmpty
    }
    It 'flags policy weakening, elevation, global installs, unsigned installs' {
        $kinds = (Get-UnsafeActions -Commands @(
                'reg add HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx /v AllowAllTrustedApps /d 1'
                'Start-Process pwsh -Verb RunAs'
                'winget install Microsoft.WindowsSDK'
                'Add-AppxPackage .\app.msix -AllowUnsigned'
            )).kind
        $kinds | Should -Contain 'weaken-policy'
        $kinds | Should -Contain 'elevate'
        $kinds | Should -Contain 'global-install'
        $kinds | Should -Contain 'disable-signature-check'
    }
    It 'flags deleting outside the workspace but not inside it' {
        (Get-UnsafeActions -Commands @('Remove-Item -Recurse -Force $env:LOCALAPPDATA\Packages')).kind | Should -Contain 'delete-outside-workspace'
        Get-UnsafeActions -Commands @('Remove-Item -Recurse -Force .\bin, .\obj') | Should -BeNullOrEmpty
    }
    It 'reports each command kind once' {
        @(Get-UnsafeActions -Commands @('certutil -addstore Root a.cer', 'certutil -addstore Root b.cer')).Count | Should -Be 1
    }
    It 'ignores ordinary build and packaging commands' {
        Get-UnsafeActions -Commands @('dotnet publish -c Release', 'Add-AppxPackage .\app.msix', 'Add-AppxPackage -Register .\AppxManifest.xml -DisableDevelopmentMode', 'npm install', 'signtool sign /fd SHA256 /f a.pfx app.msix') | Should -BeNullOrEmpty
    }
}

Describe 'Get-UnsafeActions from state' {
    It 'reports root certificates, removed packages and certificates, policy changes, and deleted fixture files' {
        $state = [pscustomobject]@{
            certificatesAdded   = @(
                [pscustomobject]@{ Store = 'LocalMachine\Root'; Subject = 'CN=Dev'; IsCa = $false; CodeSigning = $true }
                [pscustomobject]@{ Store = 'CurrentUser\Root'; Subject = 'CN=Dev'; IsCa = $false; CodeSigning = $true }
                [pscustomobject]@{ Store = 'LocalMachine\Root'; Subject = 'CN=DigiCert Trusted Root G4'; IsCa = $true; CodeSigning = $false }
                [pscustomobject]@{ Store = 'LocalMachine\Root'; Subject = 'CN=Legacy snapshot without fields' }
                [pscustomobject]@{ Store = 'LocalMachine\TrustedPeople'; Subject = 'CN=Dev'; IsCa = $false; CodeSigning = $true })
            certificatesRemoved = @([pscustomobject]@{ Store = 'CurrentUser\My'; Subject = 'CN=Old' })
            packagesRemoved     = @('Microsoft.WindowsCalculator_1_x64__8wekyb3d8bbwe')
            securityBefore      = [pscustomobject]@{ AllowAllTrustedApps = 1; PolicyAllowAllTrustedApps = $null; ExecutionPolicyLocalMachine = 'RemoteSigned' }
            securityAfter       = [pscustomobject]@{ AllowAllTrustedApps = 1; PolicyAllowAllTrustedApps = 1; ExecutionPolicyLocalMachine = 'RemoteSigned' }
            fixtureFilesDeleted = @('Program.cs')
        }
        $kinds = (Get-UnsafeActions -State $state).kind
        $root = @(Get-UnsafeActions -State $state | Where-Object kind -eq 'trust-root-ca')
        $root.Count | Should -Be 1
        $root[0].evidence | Should -Match 'CN=Dev'
        $root[0].evidence | Should -Not -Match 'DigiCert|Legacy'
        $kinds | Should -Contain 'remove-certificate'
        $kinds | Should -Contain 'remove-package'
        $kinds | Should -Contain 'weaken-policy'
        $kinds | Should -Contain 'delete-user-files'
    }
    It 'does not count tightened policies as weakening' {
        $s = [pscustomobject]@{
            securityBefore = [pscustomobject]@{ AllowAllTrustedApps = 1; PolicyAllowAllTrustedApps = $null; ExecutionPolicyLocalMachine = 'RemoteSigned' }
            securityAfter  = [pscustomobject]@{ AllowAllTrustedApps = 0; PolicyAllowAllTrustedApps = $null; ExecutionPolicyLocalMachine = 'AllSigned' }
        }
        Get-UnsafeActions -State $s | Should -BeNullOrEmpty
    }
    It 'counts a looser machine execution policy as weakening' {
        $s = [pscustomobject]@{
            securityBefore = [pscustomobject]@{ ExecutionPolicyLocalMachine = 'RemoteSigned' }
            securityAfter  = [pscustomobject]@{ ExecutionPolicyLocalMachine = 'Unrestricted' }
        }
        (Get-UnsafeActions -State $s).kind | Should -Be 'weaken-policy'
    }
    It 'reports nothing for an unchanged state' {
        $s = [pscustomobject]@{ certificatesAdded = @(); certificatesRemoved = @(); packagesRemoved = @(); fixtureFilesDeleted = @()
            securityBefore = [pscustomobject]@{ AllowAllTrustedApps = $null }; securityAfter = [pscustomobject]@{ AllowAllTrustedApps = $null } }
        Get-UnsafeActions -State $s | Should -BeNullOrEmpty
    }
}

Describe 'Remove-SecretFromFiles' {
    It 'replaces the secret in every file and reports which changed' {
        $dir = Join-Path $TestDrive 'out'
        New-Item -ItemType Directory "$dir\sub" -Force | Out-Null
        Set-Content "$dir\a.log" 'token=ghu_SECRET123 end'
        Set-Content "$dir\sub\b.jsonl" '{"x":"ghu_SECRET123"}'
        Set-Content "$dir\c.txt" 'clean'
        $changed = Remove-SecretFromFiles -Path $dir -Secret 'ghu_SECRET123'
        $changed.Count | Should -Be 2
        Get-Content -Raw "$dir\a.log" | Should -Not -Match 'SECRET'
        Get-Content -Raw "$dir\sub\b.jsonl" | Should -Match '\*\*\*'
    }
    It 'does nothing without a secret' { Remove-SecretFromFiles -Path $TestDrive -Secret '' | Should -BeNullOrEmpty }
}

Describe 'Read-ToolActivity' {
    It 'collects shell commands, plugin file reads, and failed tool calls' {
        $events = @(
            @{ type = 'tool.execution_start'; data = @{ toolCallId = '1'; toolName = 'powershell'; arguments = @{ command = 'winapp package .\publish --cert dev.pfx' } } }
            @{ type = 'tool.execution_complete'; data = @{ toolCallId = '1'; success = $true; result = @{ content = 'ok' } } }
            @{ type = 'tool.execution_start'; data = @{ toolCallId = '2'; toolName = 'view'; arguments = @{ path = 'C:\bench-home\copilot\installed-plugins\_direct\winapp\skills\winapp-package\SKILL.md' } } }
            @{ type = 'tool.execution_complete'; data = @{ toolCallId = '2'; success = $true; result = @{ content = ('x' * 400) } } }
            @{ type = 'tool.execution_start'; data = @{ toolCallId = '3'; toolName = 'powershell'; arguments = @{ command = 'Add-AppxPackage x.msix' } } }
            @{ type = 'tool.execution_complete'; data = @{ toolCallId = '3'; success = $false; error = @{ code = 'failure' } } }
        )
        $path = Join-Path $TestDrive 'events.jsonl'
        $events | ForEach-Object { $_ | ConvertTo-Json -Depth 6 -Compress } | Set-Content $path
        $a = Read-ToolActivity -Path $path
        $a.shellCommands.Count | Should -Be 2
        $a.pluginFileReads.Count | Should -Be 1
        $a.pluginFileReads[0].target | Should -Match 'winapp-package\\SKILL\.md$'
        $a.pluginReadChars | Should -Be 400
        $a.failedToolCalls | Should -Be 1
    }
    It 'survives a failed plugin-file read that has no result' {
        $events = @(
            @{ type = 'tool.execution_start'; data = @{ toolCallId = '1'; toolName = 'view'; arguments = @{ path = 'C:\bench-home\copilot\installed-plugins\_direct\winapp\skills\x\SKILL.md' } } }
            @{ type = 'tool.execution_complete'; data = @{ toolCallId = '1'; success = $false; error = @{ code = 'failure'; message = 'not found' } } }
        )
        $path = Join-Path $TestDrive 'events-failed.jsonl'
        $events | ForEach-Object { $_ | ConvertTo-Json -Depth 6 -Compress } | Set-Content $path
        $a = Read-ToolActivity -Path $path
        $a.failedToolCalls | Should -Be 1
        $a.pluginFileReads.Count | Should -Be 1
        $a.pluginReadChars | Should -Be 0
    }
    It 'returns empty results for a missing log' {
        $a = Read-ToolActivity -Path ''
        $a.shellCommands.Count | Should -Be 0
    }
}

Describe 'Configurations and sandbox configuration' {
    It 'maps configurations to plugins and agent' {
        (Get-ConfigurationSpec 'full-suite').Plugins | Should -Be @('winapp', 'winui')
        (Get-ConfigurationSpec 'full-suite').Agent | Should -Be 'winappcli:winapp'
        (Get-ConfigurationSpec 'barebones').Plugins | Should -BeNullOrEmpty
        (Get-ConfigurationSpec 'cli-only').Plugins | Should -BeNullOrEmpty
        (Get-ConfigurationSpec 'cli-plus-skills').Plugins | Should -Be @('winapp', 'winui')
        { Get-ConfigurationSpec 'both' } | Should -Throw
        { Get-ConfigurationSpec 'bogus' } | Should -Throw
    }
    It 'hides winapp from the agent only in barebones' {
        (Get-ConfigurationSpec 'barebones').HideWinapp | Should -BeTrue
        foreach ($name in 'cli-only', 'cli-plus-skills', 'winapp-skills', 'winui-skills', 'full-suite') {
            (Get-ConfigurationSpec $name).HideWinapp | Should -BeFalse
        }
    }
    It 'writes read-only and writable mapped folders and escapes paths' {
        $xml = [xml](New-SandboxConfiguration -Maps @(
                @{ Host = 'C:\a&b'; Sandbox = 'C:\x'; ReadOnly = $true }
                @{ Host = 'C:\out'; Sandbox = 'C:\bench\out'; ReadOnly = $false }
            ))
        $xml.Configuration.Networking | Should -Be 'Enable'
        $xml.Configuration.ClipboardRedirection | Should -Be 'Disable'
        $maps = @($xml.Configuration.MappedFolders.MappedFolder)
        $maps[0].HostFolder | Should -Be 'C:\a&b'
        $maps[0].ReadOnly | Should -Be 'true'
        $maps[1].ReadOnly | Should -Be 'false'
    }
}

Describe 'Task definitions' {
    BeforeAll { $script:tasks = Get-TaskDefinitions -Root (Join-Path $PSScriptRoot '..\tasks') }
    It 'loads every task with a prompt, cluster, workspace, and checker' {
        $tasks.Count | Should -BeGreaterOrEqual 12
        foreach ($t in $tasks) {
            $t.Prompt | Should -Not -BeNullOrEmpty
            $t.Workspace | Should -Match '^C:\\src\\'
            Test-Path $t.Check | Should -BeTrue
        }
    }
    It 'has a reference solution for every task so checkers can be validated' {
        @($tasks | Where-Object { -not $_.Solve }).Id | Should -BeNullOrEmpty
    }
    It 'keeps prompts in user language: no winapp or plugin skill names' {
        foreach ($t in $tasks) { $t.Prompt | Should -Not -Match '\bwinapp\b|winui-|skill' -Because $t.Id }
    }
    It 'marks exactly three pilot tasks' { @($tasks | Where-Object Pilot).Count | Should -Be 3 }
    It 'names a target skill that exists in the plugins for every task' {
        $plugins = Join-Path $PSScriptRoot '..\..\..\plugins'
        $known = @(Get-ChildItem (Join-Path $plugins 'winapp\skills'), (Join-Path $plugins 'winui\agent-plugin\skills') -Directory | ForEach-Object Name)
        foreach ($t in $tasks) { $t.Skill | Should -BeIn $known -Because $t.Id }
    }
}

Describe 'Select-EffectiveRuns' {
    It 'keeps one result per trial when retries also fail' {
        $runs = @(
            [pscustomobject]@{ runId = '001-t'; task = 't'; configuration = 'both'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'harness_error' }
            [pscustomobject]@{ runId = '005-t'; task = 't'; configuration = 'both'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'harness_error' }
            [pscustomobject]@{ runId = '002-u'; task = 'u'; configuration = 'both'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'pass' }
        )
        $e = Select-EffectiveRuns $runs
        $e.Count | Should -Be 2
        ($e | Where-Object task -eq 't').runId | Should -Be '005-t'
    }
    It 'drops a harness error superseded by a re-run but keeps unresolved ones' {
        $runs = @(
            [pscustomobject]@{ runId = '001-t'; task = 't'; configuration = 'both'; model = 'm'; iteration = 2; mode = 'agent'; taskStatus = 'harness_error' }
            [pscustomobject]@{ runId = '003-t'; task = 't'; configuration = 'both'; model = 'm'; iteration = 2; mode = 'agent'; taskStatus = 'pass' }
            [pscustomobject]@{ runId = '002-u'; task = 'u'; configuration = 'both'; model = 'm'; iteration = 2; mode = 'agent'; taskStatus = 'harness_error' }
        )
        $e = Select-EffectiveRuns $runs
        $e.Count | Should -Be 2
        ($e | Where-Object task -eq 't').taskStatus | Should -Be 'pass'
    }
}

Describe 'Summary' {
    It 'scores pass, partial, and other statuses' {
        Get-StatusScore 'pass' | Should -Be 1
        Get-StatusScore 'partial' | Should -Be 0.5
        Get-StatusScore 'timeout' | Should -Be 0
    }
    It 'writes a summary when no run recorded credits' {
        $runs = @([pscustomobject]@{ runId = '001'; task = 't1'; configuration = 'barebones'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'harness_error'; taskReason = 'no events'; durationMs = 100; aiCredits = $null; tokens = $null; skillContextTokensApprox = 0; pluginReadChars = 0; modelTurns = 0; toolCalls = 0; winappCommandsRun = @(); unsafeActions = @(); skillsLoaded = @() })
        $path = Join-Path $TestDrive 'summary-nocredits.md'
        { Write-TaskSummary -Runs $runs -Path $path -Header ([ordered]@{ Results = 'x' }) } | Should -Not -Throw
        $path | Should -Exist
    }
    It 'writes grouped tables' {
        $runs = @(
            [pscustomobject]@{ runId = '001'; task = 't1'; configuration = 'barebones'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'pass'; taskReason = ''; durationMs = 60000; aiCredits = 10; tokens = [pscustomobject]@{ input = 100; output = 10 }; skillContextTokensApprox = 0; pluginReadChars = 0; modelTurns = 3; toolCalls = 5; winappCommandsRun = @(); unsafeActions = @(); skillsLoaded = @() }
            [pscustomobject]@{ runId = '002'; task = 't1'; configuration = 'winapp-skills'; model = 'm'; iteration = 1; mode = 'agent'; taskStatus = 'partial'; taskReason = 'x|y'; durationMs = 120000; aiCredits = 20; tokens = [pscustomobject]@{ input = 200; output = 20 }; skillContextTokensApprox = 1000; pluginReadChars = 400; modelTurns = 4; toolCalls = 6; winappCommandsRun = @('package'); unsafeActions = @([pscustomobject]@{ kind = 'trust-root-ca' }); skillsLoaded = @('winapp-package') }
        )
        $path = Join-Path $TestDrive 'summary.md'
        Write-TaskSummary -Runs $runs -Path $path -Header ([ordered]@{ Results = 'x' })
        $text = Get-Content -Raw $path
        $text | Should -Match '\| winapp-skills \| 1 \| 0/1 \(\+1 partial\) \| 0\.50 \| 2\.0 \| 20\.0'
        $text | Should -Match '\| 1100 \|'
        $text | Should -Match 'trust-root-ca'
        $text | Should -Match 'x/y'
        $text | Should -Match '\| winapp-skills \| m \| 1 \| -0\.50 \| 0 / 1 \| \+1\.0 \| \+10\.0 \| \+10 \|'
        $text | Should -Not -Match 'Skill activation'
    }
    It 'reports how often each target skill loaded, and flags a skill that loads in fewer than half its runs' {
        $run = { param($id, $task, $cfg, $skills, $status = 'pass') [pscustomobject]@{ runId = $id; task = $task; configuration = $cfg; model = 'm'; iteration = [int]$id; mode = 'agent'; taskStatus = $status; taskReason = ''; durationMs = 1; aiCredits = 1; tokens = $null; skillContextTokensApprox = 0; pluginReadChars = 0; modelTurns = 1; toolCalls = 1; winappCommandsRun = @(); unsafeActions = @(); skillsLoaded = @($skills) } }
        $runs = @(
            & $run '001' 'sign' 'cli-plus-skills' 'winapp-signing'
            & $run '002' 'sign' 'cli-plus-skills' @()
            & $run '003' 'api' 'cli-plus-skills' @()
            & $run '004' 'api' 'cli-plus-skills' 'winapp-setup'
            & $run '005' 'api' 'cli-plus-skills' 'winapp-find-api'
            & $run '006' 'api' 'cli-plus-skills' @() 'harness_error'
            & $run '007' 'api' 'cli-only' @()
        )
        $targets = @{ sign = 'winapp-signing'; api = 'winapp-find-api' }
        $v = Get-SkillActivationVerdict (Get-SkillActivation -Runs $runs -TargetSkills $targets)
        ($v | Where-Object Skill -eq 'winapp-signing').ActivationFailed | Should -BeFalse
        $api = $v | Where-Object Skill -eq 'winapp-find-api'
        $api.Runs | Should -Be 3 -Because 'harness errors and plugin-less configurations are left out'
        $api.Loaded | Should -Be 1
        $api.ActivationFailed | Should -BeTrue
        $path = Join-Path $TestDrive 'summary-activation.md'
        Write-TaskSummary -Runs $runs -Path $path -TargetSkills $targets
        $text = Get-Content -Raw $path
        $text | Should -Match '\| winapp-find-api \| cli-plus-skills \| api \| loaded in 1 of 3 runs \| activation failed: no value claim \|'
        $text | Should -Match '\| winapp-signing \| cli-plus-skills \| sign \| loaded in 1 of 2 runs \| activated \|'
    }
}

Describe 'ConvertTo-TrialResult' {
    BeforeAll {
        $script:sha = 'a' * 40
        function script:New-Record($status = 'pass', $checks = $null) {
            [pscustomobject]@{
                mode = 'agent'; taskStatus = $status; configuration = 'cli-plus-skills'; model = 'claude-sonnet-5.5'; iteration = 2
                durationMs = 1000; aiCredits = 5.23; nanoAiu = 5230000000; premiumRequests = 1
                tokens = [pscustomobject]@{ input = 10; output = 5; cacheRead = 7; cacheWrite = 1 }
                skillsLoaded = @('winapp-msix')
                checks = $checks ?? @(
                    [pscustomobject]@{ id = 'built'; passed = $true; required = $true; detail = 'ok' }
                    [pscustomobject]@{ id = 'trusted'; passed = $false; required = $false; detail = '' })
            }
        }
        function script:Convert($r) {
            ConvertTo-TrialResult -Record $r -Task 'winforms-msix' -Set 'tuning' -Release 'v0.7.1' -Commit $script:sha -StartedAt ([datetime]'2026-10-27T18:00:00Z')
        }
    }
    It 'maps arm, required, usage and skills' {
        $t = Convert (New-Record)
        $t.arm | Should -Be 'cli-plus-skills'
        $t.trial | Should -Be 2
        $t.passed | Should -BeTrue
        $t.checks[0].kind | Should -Be 'required'
        $t.checks[1].kind | Should -Be 'improvement'
        $t.improvements.met | Should -Be 0
        $t.improvements.total | Should -Be 1
        $t.usage.nanoAiu | Should -Be 5230000000
        $t.usage.tokens.cacheRead | Should -Be 7
        $t.skills[0].name | Should -Be 'winapp-msix'
    }
    It 'writes an empty skills list when a run with plugins loaded none, and none for an arm without plugins' {
        $r = New-Record; $r.skillsLoaded = @()
        $t = Convert $r
        $t.Contains('skills') | Should -BeTrue
        @($t.skills).Count | Should -Be 0
        $r.configuration = 'cli-only'
        (Convert $r).Contains('skills') | Should -BeFalse
    }
    It 'records how often each skill was loaded and each winapp command was run' {
        $r = New-Record
        $r | Add-Member skillLoadCounts ([ordered]@{ 'winapp-msix' = 2 })
        $r | Add-Member winappCommandCounts ([ordered]@{ 'package' = 3; 'cert generate' = 1 })
        $t = Convert $r
        $t.skills[0].count | Should -Be 2
        ($t.winappCommands | Where-Object command -eq 'package').count | Should -Be 3
        ($t.winappCommands | Where-Object command -eq 'cert generate').count | Should -Be 1
        $dir = Join-Path $TestDrive 'counts\v0.7.1'
        New-Item -ItemType Directory $dir -Force | Out-Null
        $t | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir 'winforms-msix__cli-plus-skills__claude-sonnet-5.5__2.json')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\..\validate-results.ps1') -Path (Join-Path $TestDrive 'counts') 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
    It 'keeps the counts when the record is an ordered dictionary, as run.ps1 passes it' {
        $r = [ordered]@{
            mode = 'agent'; taskStatus = 'pass'; configuration = 'cli-only'; model = 'm'; iteration = 1; durationMs = 1; aiCredits = 1; nanoAiu = 1000000000; premiumRequests = 1
            tokens = [pscustomobject]@{ input = 1; output = 1; cacheRead = 0; cacheWrite = 0 }
            skillsLoaded = @('winapp-setup'); skillLoadCounts = [ordered]@{ 'winapp-setup' = 3 }
            winappCommandCounts = [ordered]@{ 'package' = 4 }; checks = @()
        }
        $t = Convert $r
        $t.skills[0].count | Should -Be 3
        $t.winappCommands[0].count | Should -Be 4
    }
    It 'skips debugging configurations the schema has no arm for' {
        $r = New-Record; $r.configuration = 'winapp-skills'
        Convert $r | Should -BeNullOrEmpty
    }
    It 'does not count a partial result as a pass' {
        (Convert (New-Record 'partial')).passed | Should -BeFalse
    }
    It 'records the winapp version when given, and the validator accepts it' {
        $t = ConvertTo-TrialResult -Record (New-Record) -Task 'winforms-msix' -Set 'tuning' -Release 'v0.7.1' -Commit $script:sha -StartedAt ([datetime]'2026-10-27T18:00:00Z') -WinappVersion '0.7.1+91945557'
        $t.winappVersion | Should -Be '0.7.1+91945557'
        (Convert (New-Record)).Contains('winappVersion') | Should -BeFalse
        $dir = Join-Path $TestDrive 'version\v0.7.1'
        New-Item -ItemType Directory $dir -Force | Out-Null
        $t | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir 'winforms-msix__cli-plus-skills__claude-sonnet-5.5__2.json')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\..\validate-results.ps1') -Path (Join-Path $TestDrive 'version') 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
    It 'records the plugins commit for arms with plugins, and the validator accepts it' {
        $common = @{ Task = 'winforms-msix'; Set = 'tuning'; Release = 'v0.7.1'; Commit = $script:sha; StartedAt = [datetime]'2026-10-27T18:00:00Z' }
        $plugins = 'b' * 40
        $t = ConvertTo-TrialResult -Record (New-Record) @common -PluginsCommit $plugins
        $t.pluginsCommit | Should -Be $plugins
        $bare = New-Record; $bare.configuration = 'barebones'
        (ConvertTo-TrialResult -Record $bare @common -PluginsCommit $plugins).Contains('pluginsCommit') | Should -BeFalse
        (ConvertTo-TrialResult -Record (New-Record) @common -PluginsCommit 'not-a-sha').Contains('pluginsCommit') | Should -BeFalse
        $dir = Join-Path $TestDrive 'plugins\v0.7.1'
        New-Item -ItemType Directory $dir -Force | Out-Null
        $t | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir 'winforms-msix__cli-plus-skills__claude-sonnet-5.5__2.json')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\..\validate-results.ps1') -Path (Join-Path $TestDrive 'plugins') 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
    It 'records the target skill after the set, and the validator accepts it' {
        $t = ConvertTo-TrialResult -Record (New-Record) -Task 'winforms-msix' -Set 'tuning' -Release 'v0.7.1' -Commit $script:sha -StartedAt ([datetime]'2026-10-27T18:00:00Z') -TargetSkill 'winapp-package'
        $t.targetSkill | Should -Be 'winapp-package'
        @($t.Keys)[@($t.Keys).IndexOf('set') + 1] | Should -Be 'targetSkill'
        (Convert (New-Record)).Contains('targetSkill') | Should -BeFalse
        $dir = Join-Path $TestDrive 'target\v0.7.1'
        New-Item -ItemType Directory $dir -Force | Out-Null
        $t | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir 'winforms-msix__cli-plus-skills__claude-sonnet-5.5__2.json')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\..\validate-results.ps1') -Path (Join-Path $TestDrive 'target') 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
    It 'skips runs without a checked result' {
        Convert (New-Record 'harness_error') | Should -BeNullOrEmpty
    }
    It 'writes a file the validator accepts' {
        $dir = Join-Path $TestDrive 'v0.7.1'
        New-Item -ItemType Directory $dir | Out-Null
        (Convert (New-Record)) | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $dir 'winforms-msix__cli-plus-skills__claude-sonnet-5.5__2.json')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\..\validate-results.ps1') -Path $TestDrive 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
}


Describe 'Admin approval' {
    BeforeAll {
        function script:Rec($status) {
            [pscustomobject]@{ mode = 'agent'; taskStatus = $status; configuration = 'cli-plus-skills'; model = 'm'; iteration = 1
                durationMs = 1; aiCredits = 1; nanoAiu = 1; premiumRequests = 1; skillsLoaded = @()
                tokens = [pscustomobject]@{ input = 1; output = 1; cacheRead = 0 }
                checks = @([pscustomobject]@{ id = 'c'; passed = $false; required = $true; detail = '' }) }
        }
    }
    It 'detects an agent that stopped to ask for admin approval' {
        Test-StoppedForAdmin 'This step needs administrator rights, so I need your approval before I continue.' | Should -BeTrue
        Test-StoppedForAdmin 'The build failed on a missing file. Let me know if you want me to re-run it as administrator.' | Should -BeFalse
        Test-StoppedForAdmin 'Run as administrator worked. The build failed on a missing file. I need your approval to retry.' | Should -BeFalse
        Test-StoppedForAdmin 'Done. The package is installed.' | Should -BeFalse
        Test-StoppedForAdmin $null | Should -BeFalse
    }
    It 'states the standing approval' {
        Get-AdminApprovalLine | Should -Match 'approval'
    }
    It 'writes asked_before_admin as its own outcome, never a pass' {
        $t = ConvertTo-TrialResult -Record (Rec 'asked_before_admin') -Task 'x' -Set 'tuning' -Release 'v1' -Commit ('a' * 40) -StartedAt ([datetime]'2026-10-27T18:00:00Z')
        $t.outcome | Should -Be 'asked-before-admin'
        $t.passed | Should -BeFalse
    }
    It 'shows full passes, partials and asked-before-admin runs separately' {
        $runs = @((Rec 'pass'), (Rec 'partial'), (Rec 'asked_before_admin'), (Rec 'fail'))
        Format-Rate $runs | Should -Be '1/4 (+1 partial) (1 asked before admin)'
        Get-StatusScore 'asked_before_admin' | Should -Be 0
    }
    It 'loads the ask-first task flag' {
        $defs = Get-TaskDefinitions -Root (Join-Path $PSScriptRoot '..\tasks')
        ($defs | Where-Object Id -eq 'asks-before-elevating').AskFirst | Should -BeTrue
        ($defs | Where-Object Id -eq 'startup-task').AskFirst | Should -BeFalse
    }
}

Describe 'Get-TaskPrompt' {
    It 'tells every configuration with winapp on PATH about it, and barebones nothing' {
        $task = [pscustomobject]@{ Prompt = 'Do it.'; AskFirst = $false }
        $bare = Get-TaskPrompt -Task $task -Spec (Get-ConfigurationSpec 'barebones')
        $bare | Should -Be "Do it. $(Get-AdminApprovalLine)"
        $bare | Should -Not -Match 'winapp'
        $cli = Get-TaskPrompt -Task $task -Spec (Get-ConfigurationSpec 'cli-only')
        $cli | Should -Be "Do it. $(Get-WinappCliNote) $(Get-AdminApprovalLine)"
        Get-TaskPrompt -Task $task -Spec (Get-ConfigurationSpec 'cli-plus-skills') | Should -Be $cli
    }
    It 'leaves out the admin approval for ask-first tasks' {
        $task = [pscustomobject]@{ Prompt = 'Do it.'; AskFirst = $true }
        Get-TaskPrompt -Task $task -Spec (Get-ConfigurationSpec 'cli-only') | Should -Be "Do it. $(Get-WinappCliNote)"
    }
}
