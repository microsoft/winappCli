# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
BeforeAll {
    $source = Get-Content "$PSScriptRoot\..\test-managed-devtools.ps1" -Raw
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
    $phase = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ManagedPhase'
    }, $true)
    . ([scriptblock]::Create($phase.Extent.Text))
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
}

Describe 'Managed validation phase diagnostics' {
    BeforeEach {
        $logs = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $logs | Out-Null
        $Framework = 'controlled-headless'
    }

    It 'records the exact owned executable, arguments, exit code and both streams' {
        Invoke-ManagedPhase success $pwsh @('-NoProfile', '-Command',
            '[Console]::Out.WriteLine("phase stdout"); [Console]::Error.WriteLine("phase stderr")') 10
        $receipt = Get-Content "$logs\success.json" -Raw | ConvertFrom-Json
        $receipt.executable | Should -Be $pwsh
        $receipt.arguments.Count | Should -Be 3
        $receipt.exitCode | Should -Be 0
        $receipt.pid | Should -BeGreaterThan 0
        Get-Content "$logs\success.stdout.log" | Should -Be 'phase stdout'
        Get-Content "$logs\success.stderr.log" | Should -Be 'phase stderr'
    }

    It 'fails on a nonzero phase and retains partial output' {
        { Invoke-ManagedPhase failure $pwsh @('-NoProfile', '-Command',
            '[Console]::Out.WriteLine("before failure"); exit 7') 10 } | Should -Throw '*exit 7*'
        (Get-Content "$logs\failure.json" -Raw | ConvertFrom-Json).exitCode | Should -Be 7
        Get-Content "$logs\failure.stdout.log" | Should -Be 'before failure'
    }

    It 'bounds a stalled phase, kills its owned tree and preserves its startup receipt' {
        $childScript = Join-Path $logs 'child.ps1'
        'Start-Sleep -Seconds 60' | Set-Content $childScript
        $parentScript = Join-Path $logs 'parent.ps1'
        @'
$child = Start-Process (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile', '-File', "$PSScriptRoot\child.ps1") -PassThru -NoNewWindow
[Console]::Out.WriteLine("child=$($child.Id)")
[Console]::Out.Flush()
Start-Sleep -Seconds 60
'@ | Set-Content $parentScript
        { Invoke-ManagedPhase stalled $pwsh @('-NoProfile', '-File', $parentScript) 3 } |
            Should -Throw '*exceeded 3s*'
        $receipt = Get-Content "$logs\stalled.json" -Raw | ConvertFrom-Json
        $receipt.timedOut | Should -BeTrue
        $receipt.elapsedSeconds | Should -BeLessThan 20
        Get-Process -Id $receipt.pid -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
        $childLine = Get-Content "$logs\stalled.stdout.log" | Where-Object { $_ -match '^child=\d+$' }
        $childLine | Should -Not -BeNullOrEmpty
        Get-Process -Id ([int]$childLine.Substring(6)) -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
    }
}

Describe 'Managed headless runner wiring' {
    It 'uses app-local runtime only for headless test builds, not the referenced hook or desktop gate' {
        [xml]$project = Get-Content "$PSScriptRoot\..\..\src\winapp-devtools\test\WinApp.DevTools.Managed.Tests\WinApp.DevTools.Managed.Tests.csproj"
        $group = @($project.Project.PropertyGroup | Where-Object Condition -eq "'`$(WinAppHeadlessTests)' == 'true'")
        $group.Count | Should -Be 1
        $group.WindowsAppSDKSelfContained | Should -Be 'true'
        $group.RuntimeIdentifier | Should -Be 'win-x64'
        $source | Should -Match '-p:WinAppHeadlessTests=true'
        $source | Should -Not -Match "'-p:WindowsAppSDKSelfContained=true'"
        Get-Content "$PSScriptRoot\..\test-binding-lifetime.ps1" -Raw | Should -Not -Match 'WinAppHeadlessTests'
    }

    It 'runs the exact output binary with checked runner options and preserves minimum coverage' {
        $source | Should -Match "Invoke-ManagedPhase restore dotnet"
        $source | Should -Match "Invoke-ManagedPhase build dotnet"
        $source | Should -Match "'--no-restore'"
        $source | Should -Match "Join-Path .output 'WinApp.DevTools.Managed.Tests.exe'"
        $source | Should -Match 'Invoke-ManagedPhase discovery \$executable'
        $source | Should -Match 'Invoke-ManagedPhase test \$executable'
        $source | Should -Match "'--timeout', '4m'"
        $source | Should -Match "'--diagnostic'"
        $source | Should -Match '\[Math\]::Max\(106,'
        $source | Should -Match ([regex]::Escape('[int]$counts.passed -ne [int]$counts.total'))
        $source | Should -Match ([regex]::Escape('[int]$counts.executed -ne [int]$counts.total'))
        $source | Should -Not -Match 'dotnet run|ignore-exit-code'
    }
}
