#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:packageScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'package-python.ps1'

    function Invoke-FixturePython {
        $state = $packageTestState
        $arguments = @($args)
        if ($arguments[1] -eq '-c' -and $arguments[2] -match 'sysconfig.get_platform') {
            $global:LASTEXITCODE = $state.PythonProbeExit
            return $state.PythonPlatform
        }
        if ($arguments[1] -eq '-c' -and $arguments[2] -match 'native_platform') {
            $state.Probes.Add([pscustomobject]@{
                Executable = $arguments[4]
                Platform = $arguments[5]
            })
            $global:LASTEXITCODE = $state.NativeProbeExit
            return
        }
        if ($arguments[1] -eq '-c' -and $arguments[2] -match 'tomllib') {
            $state.Versions.Add($arguments[4])
            if ($state.VersionStageExit -eq 0) {
                $metadata = Get-Content -LiteralPath $arguments[3] -Raw
                $metadata = $metadata -replace '(?m)^version\s*=.*$', "version = `"$($arguments[4])`""
                Set-Content -LiteralPath $arguments[3] -Value $metadata
            }
            $global:LASTEXITCODE = $state.VersionStageExit
            return
        }
        if ($arguments[0] -eq '-m' -and $arguments[1] -eq 'pip' -and $arguments[2] -eq 'wheel') {
            $stage = $arguments[[array]::IndexOf($arguments, '-w') - 1]
            $native = Join-Path $stage 'src\winapp\_native'
            $state.Wheels.Add([pscustomobject]@{
                Stage = $stage
                Executable = Get-Content -LiteralPath (Join-Path $native 'winapp.exe') -Raw
                License = Test-Path -LiteralPath (Join-Path $native 'LICENSE')
                Pdbs = @(Get-ChildItem -LiteralPath $native -Recurse -Filter '*.pdb')
                Version = [regex]::Match((Get-Content -LiteralPath (Join-Path $stage 'pyproject.toml') -Raw),
                    '(?m)^version = "([^"]+)"').Groups[1].Value
                Name = [regex]::Match((Get-Content -LiteralPath (Join-Path $stage 'pyproject.toml') -Raw),
                    '(?m)^name = "([^"]+)"').Groups[1].Value
            })
            $global:LASTEXITCODE = $state.WheelExit
            return
        }
        throw "Unexpected Python fixture invocation: $arguments"
    }

    function dotnet {
        $state = $packageTestState
        $arguments = @($args)
        $runtime = $arguments[[array]::IndexOf($arguments, '-r') + 1]
        $output = $arguments[[array]::IndexOf($arguments, '-o') + 1]
        $state.Publishes.Add([pscustomobject]@{
            Arguments = $arguments
            Runtime = $runtime
            Output = $output
        })
        if ($state.FailedRuntime -eq $runtime) {
            $global:LASTEXITCODE = 17
            return
        }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $output 'winapp.exe') -Value "fresh $runtime"
        Set-Content -LiteralPath (Join-Path $output 'winapp.pdb') -Value 'symbols'
        $global:LASTEXITCODE = 0
    }
}

Describe 'Local Python wheel builds' {
    BeforeEach {
        $script:fixture = Join-Path $TestDrive "repository with spaces $([guid]::NewGuid().ToString('N'))"
        $script:copiedScript = Join-Path $fixture 'scripts\package-python.ps1'
        $package = Join-Path $fixture 'src\winapp-python'
        New-Item -ItemType Directory -Path (Split-Path $copiedScript -Parent),
            (Join-Path $package 'src\winapp'), (Join-Path $fixture 'artifacts') -Force | Out-Null
        Copy-Item -LiteralPath $script:packageScript -Destination $copiedScript
        Set-Content -LiteralPath (Join-Path $fixture 'version.json') -Value '{"version":"0.7.2"}'
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\get-build-number.ps1') -Value '$global:LASTEXITCODE = 0; 37'
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\get-prerelease-label.ps1') -Value '$global:LASTEXITCODE = 0; "prerelease"'
        foreach ($name in @('pyproject.toml', 'setup.py', 'README.md', 'LICENSE')) {
            Set-Content -LiteralPath (Join-Path $package $name) -Value 'fixture package'
        }
        Set-Content -LiteralPath (Join-Path $package 'pyproject.toml') -Value @'
[project]
name = "microsoft-winappcli"
version = "0.1.0.dev0"
'@
        Set-Content -LiteralPath (Join-Path $package 'src\winapp\__init__.py') -Value ''
        Set-Content -LiteralPath (Join-Path $fixture 'LICENSE') -Value 'fixture license'
        Set-Content -LiteralPath (Join-Path $fixture 'artifacts\keep.txt') -Value 'unrelated artifact'
        foreach ($arch in @('x64', 'arm64')) {
            $directory = Join-Path $fixture "artifacts\cli\win-$arch"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $directory 'winapp.exe') -Value "existing win-$arch"
        }
        $script:publishes = [Collections.Generic.List[object]]::new()
        $script:probes = [Collections.Generic.List[object]]::new()
        $script:wheels = [Collections.Generic.List[object]]::new()
        $script:packageTestState = [pscustomobject]@{
            PythonPlatform = 'win_amd64'
            PythonProbeExit = 0
            NativeProbeExit = 0
            NativeVersion = '0.7.2-prerelease.37'
            VersionStageExit = 0
            WheelExit = 0
            FailedRuntime = $null
            Publishes = $publishes
            Probes = $probes
            Wheels = $wheels
            Versions = [Collections.Generic.List[string]]::new()
        }
        $script:options = @{
            Python = 'Invoke-FixturePython'
            OutputDirectory = Join-Path $fixture 'wheels'
        }
        Mock Get-Item {
            [pscustomobject]@{ VersionInfo = @{ ProductVersion = $packageTestState.NativeVersion } }
        } -ParameterFilter { $LiteralPath -like '*winapp.exe' }
    }

    It 'publishes and packages only the selected x64 Python architecture by default' {
        & $copiedScript -BuildCli @options

        $publishes.Count | Should -Be 1
        $publishes[0].Runtime | Should -Be 'win-x64'
        $publishes[0].Arguments | Should -Contain '--self-contained'
        $publishes[0].Arguments | Should -Contain 'Release'
        $probes[0].Platform | Should -Be 'win_amd64'
        $wheels.Count | Should -Be 1
        $wheels[0].Executable.Trim() | Should -Be 'fresh win-x64'
        $wheels[0].License | Should -BeTrue
        $wheels[0].Pdbs.Count | Should -Be 0
        $wheels[0].Version | Should -Be '0.7.2.dev37'
        $wheels[0].Name | Should -Be 'microsoft-winappcli'
        $publishes[0].Arguments | Should -Contain '/p:InformationalVersion=0.7.2-prerelease.37'
        $publishes[0].Arguments | Should -Contain '/p:AssemblyVersion=0.7.2.37'
        Get-Content -LiteralPath (Join-Path $fixture 'src\winapp-python\pyproject.toml') -Raw |
            Should -Match 'version = "0.1.0.dev0"'
        $publishes[0].Output | Should -Not -Exist
        $wheels[0].Stage | Should -Not -Exist
        Get-Content -LiteralPath (Join-Path $fixture 'artifacts\cli\win-x64\winapp.exe') |
            Should -Be 'existing win-x64'
        Get-Content -LiteralPath (Join-Path $fixture 'artifacts\keep.txt') | Should -Be 'unrelated artifact'
    }

    It 'selects ARM64 when the chosen Python is ARM64' {
        $packageTestState.PythonPlatform = 'win_arm64'

        & $copiedScript -BuildCli @options

        $publishes.Count | Should -Be 1
        $publishes[0].Runtime | Should -Be 'win-arm64'
        $probes[0].Platform | Should -Be 'win_arm64'
    }

    It 'honors an explicit target architecture' {
        & $copiedScript -BuildCli -Architecture arm64 @options

        $publishes.Count | Should -Be 1
        $publishes[0].Runtime | Should -Be 'win-arm64'
    }

    It 'builds both architectures only when explicitly requested' {
        & $copiedScript -BuildCli -Architecture All @options

        @($publishes.Runtime) | Should -Be @('win-x64', 'win-arm64')
        @($probes.Platform) | Should -Be @('win_amd64', 'win_arm64')
        $wheels.Count | Should -Be 2
    }

    It 'preserves package-only behavior without publishing the CLI' {
        & $copiedScript @options

        $publishes.Count | Should -Be 0
        $wheels.Count | Should -Be 2
        @($wheels.Executable | ForEach-Object Trim) | Should -Be @('existing win-x64', 'existing win-arm64')
    }

    It 'uses the repository base version for stable native and Python packages' {
        $packageTestState.NativeVersion = '0.7.2'

        & $copiedScript -BuildCli -Stable @options

        $wheels[0].Version | Should -Be '0.7.2'
        $publishes[0].Arguments | Should -Contain '/p:InformationalVersion=0.7.2'
    }

    It 'accepts a shared prerelease version and maps only its format for Python' {
        $packageTestState.NativeVersion = '0.8.1-feature-name.123'

        & $copiedScript -BuildCli -Version '0.8.1-feature-name.123' @options

        $wheels[0].Version | Should -Be '0.8.1.dev123'
        $publishes[0].Arguments | Should -Contain '/p:InformationalVersion=0.8.1-feature-name.123'
        $publishes[0].Arguments | Should -Contain '/p:AssemblyVersion=0.8.1.123'
    }

    It 'takes version changes from version.json rather than the source pyproject placeholder' {
        Set-Content -LiteralPath (Join-Path $fixture 'version.json') -Value '{"version":"0.9.0"}'
        $packageTestState.NativeVersion = '0.9.0-prerelease.37'

        & $copiedScript -BuildCli @options

        $wheels[0].Version | Should -Be '0.9.0.dev37'
    }

    It 'rejects malformed and conflicting shared build versions before publishing' {
        foreach ($value in @('latest', '0.7', '0.7.2.dev37', '0.7.2-preview')) {
            { & $copiedScript -BuildCli -Version $value @options } |
                Should -Throw '*Unsupported shared build version*'
        }
        { & $copiedScript -BuildCli -Version '0.7.2-prerelease.37' -Stable @options } |
            Should -Throw '*Unsupported shared build version*'
        $publishes.Count | Should -Be 0
        $wheels.Count | Should -Be 0
    }

    It 'does not bundle stale native artifacts under a new Python package version' {
        $packageTestState.NativeVersion = '0.7.1'

        { & $copiedScript @options } | Should -Throw '*does not match shared build version*'

        $wheels.Count | Should -Be 0
    }

    It 'reports staging version failures without modifying source metadata or packaging' {
        $packageTestState.VersionStageExit = 13

        { & $copiedScript -BuildCli @options } | Should -Throw '*Failed to set the Python version*'

        $wheels.Count | Should -Be 0
        Get-Content -LiteralPath (Join-Path $fixture 'src\winapp-python\pyproject.toml') -Raw |
            Should -Match 'version = "0.1.0.dev0"'
    }

    It 'rejects a local build combined with an existing-publish override' {
        { & $copiedScript -BuildCli -CliPath (Join-Path $fixture 'artifacts\cli') @options } |
            Should -Throw '*not both*'

        $publishes.Count | Should -Be 0
        $wheels.Count | Should -Be 0
    }

    It 'rejects unsupported Python before native publishing' {
        $packageTestState.PythonPlatform = 'win32'

        { & $copiedScript -BuildCli @options } | Should -Throw '*Unsupported build Python platform*'

        $publishes.Count | Should -Be 0
    }

    It 'surfaces Python probe failures before native publishing' {
        $packageTestState.PythonProbeExit = 13

        { & $copiedScript -BuildCli @options } | Should -Throw '*Windows CPython*'

        $publishes.Count | Should -Be 0
    }

    It 'does not package stale binaries after a native publish fails' {
        $packageTestState.FailedRuntime = 'win-x64'

        { & $copiedScript -BuildCli @options } | Should -Throw '*Native CLI publish failed for x64*'

        $wheels.Count | Should -Be 0
        Get-Content -LiteralPath (Join-Path $fixture 'artifacts\cli\win-x64\winapp.exe') |
            Should -Be 'existing win-x64'
        Split-Path $publishes[0].Output -Parent | Should -Not -Exist
    }

    It 'refuses to package a native architecture mismatch' {
        $packageTestState.NativeProbeExit = 19

        { & $copiedScript -BuildCli @options } | Should -Throw '*wrong architecture*'

        $wheels.Count | Should -Be 0
        $publishes[0].Output | Should -Not -Exist
    }

    It 'cleans its staging directory when wheel creation fails' {
        $packageTestState.WheelExit = 23

        { & $copiedScript -BuildCli @options } | Should -Throw '*Failed to build the x64 Python wheel*'

        $wheels[0].Stage | Should -Not -Exist
        $publishes[0].Output | Should -Not -Exist
    }

    It 'restores the caller environment after native publishing fails' {
        $packageTestState.FailedRuntime = 'win-x64'
        $originalPath = $env:PATH
        $originalPlatform = $env:Platform
        try {
            $env:Platform = 'x64'
            { & $copiedScript -BuildCli @options } | Should -Throw '*Native CLI publish failed*'

            $env:PATH | Should -BeExactly $originalPath
            $env:Platform | Should -BeExactly 'x64'
        } finally {
            $env:PATH = $originalPath
            $env:Platform = $originalPlatform
        }
    }
}
