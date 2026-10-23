# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

param(
    [string]$WinappPath,
    [string]$Python = 'python',
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = -not $IsWindows -or $null -eq (Get-Command $Python -ErrorAction SilentlyContinue)
}

Describe 'Unpackaged Python WinUI sample' {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:tempDir = $null
        $script:runs = @()
        $script:sampleDir = $PSScriptRoot
        if ($script:skip) { return }
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes

        function New-SampleProcess {
            param([string]$Executable, [string[]]$Arguments, [string]$Directory)
            $info = [Diagnostics.ProcessStartInfo]::new($Executable)
            $info.UseShellExecute = $false
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
            $info.WorkingDirectory = $Directory
            $info.Environment.Remove('WINAPP_CLI_PATH') | Out-Null
            foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($info)
            [pscustomobject]@{
                Process = $process
                Output = $process.StandardOutput.ReadToEndAsync()
                Errors = $process.StandardError.ReadToEndAsync()
                Window = $null
            }
        }

        function Invoke-SampleCommand {
            param([string]$Executable, [string[]]$Arguments, [string]$Directory = $script:tempDir)
            $run = New-SampleProcess $Executable $Arguments $Directory
            try {
                Wait-ProcessExitOrFail -Process $run.Process -Timeout ([TimeSpan]::FromMinutes(15)) `
                    -Description "$Executable $($Arguments -join ' ')"
                $output = $run.Output.GetAwaiter().GetResult()
                $errors = $run.Errors.GetAwaiter().GetResult()
                if ($run.Process.ExitCode -ne 0) {
                    throw "Command failed ($($run.Process.ExitCode)): $Executable $($Arguments -join ' ')`n$output`n$errors"
                }
                return $output
            } finally {
                $run.Process.Dispose()
            }
        }

        function Get-Control {
            param($Run, [string]$Id)
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
            $element = $Run.Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if (-not $element) { throw "WinUI control missing: $Id" }
            return $element
        }

        function Assert-ControlText {
            param($Run, [string]$Id, [string]$Expected)
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $actual = (Get-Control $Run $Id).Current.Name
                if ($actual -eq $Expected) { return }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            $actual | Should -Be $Expected
        }

        function Start-SampleWindow {
            param([string]$Directory)
            $run = New-SampleProcess $script:cli @('run', '--project', $Directory, 'app.py') $Directory
            $script:runs += $run
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty, 'Python WinUI counter')
            $deadline = [DateTime]::UtcNow.AddMinutes(3)
            do {
                if ($run.Process.HasExited) {
                    throw "Sample exited before opening WinUI.`n$($run.Output.GetAwaiter().GetResult())`n$($run.Errors.GetAwaiter().GetResult())"
                }
                $ids = @(Get-ProcessTree -RootProcessId $run.Process.Id |
                    ForEach-Object { $_.Process.ProcessId })
                $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                    [Windows.Automation.TreeScope]::Children, $condition)
                foreach ($window in $windows) {
                    if ($window.Current.ProcessId -in $ids) {
                        $run.Window = $window
                        $ready = [Windows.Automation.PropertyCondition]::new(
                            [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Increment')
                        if ($window.FindFirst([Windows.Automation.TreeScope]::Descendants, $ready)) { return $run }
                    }
                }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            throw 'Sample did not create its WinUI controls.'
        }

        function Invoke-Control {
            param($Run, [string]$Id)
            (Get-Control $Run $Id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        }

        function Get-CounterBounds {
            param($Run)
            $bounds = @{}
            foreach ($id in @('Increment', 'Count', 'Decrement')) {
                $bounds[$id] = (Get-Control $Run $id).Current.BoundingRectangle
                $bounds[$id].Width | Should -BeGreaterThan 0
                $bounds[$id].Height | Should -BeGreaterThan 0
            }
            $bounds['Increment'].Right | Should -BeLessThan $bounds['Count'].Left
            $bounds['Count'].Right | Should -BeLessThan $bounds['Decrement'].Left
            return $bounds
        }

        function Close-SampleWindow {
            param($Run)
            $Run.Window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
            Wait-ProcessExitOrFail -Process $Run.Process -Timeout ([TimeSpan]::FromSeconds(15)) `
                -Description 'Python sample after closing WinUI'
            $Run.Process.ExitCode | Should -Be 0 -Because $Run.Errors.GetAwaiter().GetResult()
        }

        $script:platform = (& $Python -I -c 'import sysconfig; print(sysconfig.get_platform().replace("-", "_"))').Trim()
        if ($LASTEXITCODE -ne 0 -or $platform -notin @('win_amd64', 'win_arm64')) {
            throw 'The sample requires Windows x64 or ARM64 CPython.'
        }
        $script:architecture = if ($platform -eq 'win_arm64') { 'arm64' } else { 'x64' }
        $packagePath = if ($WinappPath) { $WinappPath } else { Join-Path $PSScriptRoot '..\..\artifacts\python' }
        $packagePath = (Resolve-Path -LiteralPath $packagePath).Path
        if (Test-Path -LiteralPath (Join-Path $packagePath 'python') -PathType Container) {
            $packagePath = Join-Path $packagePath 'python'
        }
        $wheels = if (Test-Path -LiteralPath $packagePath -PathType Container) {
            @(Get-ChildItem -LiteralPath $packagePath -Filter "microsoft_winappcli-*-py3-none-$platform.whl" -File)
        } else {
            @(Get-Item -LiteralPath $packagePath | Where-Object Name -Like "microsoft_winappcli-*-py3-none-$platform.whl")
        }
        if ($wheels.Count -ne 1) { throw "Expected one microsoft-winappcli $platform wheel." }
        $script:tempDir = New-TempTestDirectory -Prefix 'python-winui'
        $script:appDir = Join-Path $tempDir 'project'
        $script:cloneDir = Join-Path $tempDir 'restored-copy'
        foreach ($directory in @($appDir, $cloneDir)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
            foreach ($name in @('app.py', 'runtime.py', 'counter.py', 'window.py', 'pyproject.toml')) {
                Copy-Item -LiteralPath (Join-Path $sampleDir $name) -Destination $directory
            }
        }
        $tool = Join-Path $tempDir 'tool'
        Invoke-SampleCommand $Python @('-I', '-m', 'venv', $tool) | Out-Null
        $toolPython = Join-Path $tool 'Scripts\python.exe'
        Invoke-SampleCommand $toolPython @('-I', '-m', 'pip', 'install', '--disable-pip-version-check',
            '--no-input', $wheels[0].FullName) | Out-Null
        $script:cli = Join-Path $tool 'Scripts\winapp.exe'
    }

    AfterAll {
        if (-not $script:tempDir) { return }
        foreach ($run in $script:runs) {
            if (-not $run.Process.HasExited) {
                foreach ($node in (Get-ProcessTree -RootProcessId $run.Process.Id | Sort-Object Depth -Descending)) {
                    $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($node.Process.ProcessId)"
                    if ($current -and $current.CreationDate -eq $node.Process.CreationDate) {
                        Stop-Process -Id $node.Process.ProcessId -Force
                    }
                }
                $run.Process.WaitForExit(10000) | Out-Null
            }
            $run.Process.Dispose()
        }
        if ($SkipCleanup) { Write-Host "Test files preserved: $script:tempDir" }
        else { Remove-TempTestDirectory -Path $script:tempDir }
    }

    Context 'Phase 1: WinUI walkthrough' {
        It 'Initializes SDKs and GUI-only bindings' -Skip:$script:skip {
            Invoke-SampleCommand $script:cli @('init', '--use-defaults', '--add-python-bindings') $appDir |
                Should -Match 'Python bindings ready'
            foreach ($name in @('winapp.yaml', '.venv\Scripts\python.exe',
                '.winapp\python\winapp_bindings\pyproject.toml')) {
                Join-Path $appDir $name | Should -Exist
            }
        }

        It 'Bootstraps ordinary Python and resets the in-memory counter on each run' -Skip:$script:skip {
            $run = Start-SampleWindow $appDir
            Assert-ControlText $run 'Mode' 'Bootstrap - memory only'
            Assert-ControlText $run 'Count' '0'
            (Get-Control $run 'Decrement').Current.IsEnabled | Should -BeFalse
            $bounds = Get-CounterBounds $run
            foreach ($step in 1..9) { Invoke-Control $run 'Increment' }
            Assert-ControlText $run 'Count' '9'
            Invoke-Control $run 'Increment'
            Assert-ControlText $run 'Count' '10'
            $after = Get-CounterBounds $run
            foreach ($id in @('Increment', 'Count', 'Decrement')) {
                foreach ($property in @('Left', 'Top', 'Width', 'Height')) {
                    [Math]::Abs($after[$id].$property - $bounds[$id].$property) | Should -BeLessOrEqual 0.5
                }
            }
            Invoke-Control $run 'Decrement'
            Assert-ControlText $run 'Count' '9'
            $bootstrap = @(Get-Process -Id $run.Window.Current.ProcessId | Select-Object -ExpandProperty Modules |
                Where-Object ModuleName -EQ 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
            $bootstrap.Count | Should -Be 1
            $bootstrap[0].FileName | Should -Be (
                Join-Path $appDir ".winapp\bin\$architecture\Microsoft.WindowsAppRuntime.Bootstrap.dll")
            Close-SampleWindow $run
            $again = Start-SampleWindow $appDir
            Assert-ControlText $again 'Count' '0'
            Close-SampleWindow $again
            Join-Path $appDir '.winapp\python-identity.json' | Should -Not -Exist
            Join-Path $appDir '.winapp\python-identity' | Should -Not -Exist
        }
    }

    Context 'Phase 2: Existing sample and fresh clone' {
        It 'Restores the pinned source and runs without initialization' -Skip:$script:skip {
            foreach ($name in @('pyproject.toml', 'winapp.yaml')) {
                Copy-Item -LiteralPath (Join-Path $appDir $name) -Destination $cloneDir -Force
            }
            $pins = Get-Content -LiteralPath (Join-Path $cloneDir 'winapp.yaml') -Raw
            Invoke-SampleCommand $script:cli @('restore', '--project', $cloneDir) | Out-Null
            Get-Content -LiteralPath (Join-Path $cloneDir 'winapp.yaml') -Raw | Should -Be $pins
            $run = Start-SampleWindow $cloneDir
            Assert-ControlText $run 'Count' '0'
            Close-SampleWindow $run
        }

        It 'Checks memory-counter and Bootstrap helpers' -Skip:$script:skip {
            Invoke-SampleCommand $Python @('-I', '-B', '-m', 'unittest', 'discover',
                '-s', $sampleDir, '-p', 'test_app.py') | Out-Null
        }
    }
}
