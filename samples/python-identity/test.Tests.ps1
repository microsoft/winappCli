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

Describe 'Python identity guide and console sample' {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:tempDir = $null
        $script:sampleDir = $PSScriptRoot
        $script:identityName = $null
        if ($script:skip) { return }

        function Invoke-SampleCommand {
            param([string]$Executable, [string[]]$Arguments, [string]$Directory = $script:tempDir)
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
            $outputTask = $process.StandardOutput.ReadToEndAsync()
            $errorTask = $process.StandardError.ReadToEndAsync()
            try {
                Wait-ProcessExitOrFail -Process $process -Timeout ([TimeSpan]::FromMinutes(15)) `
                    -Description "$Executable $($Arguments -join ' ')"
                $output = $outputTask.GetAwaiter().GetResult()
                $errors = $errorTask.GetAwaiter().GetResult()
                if ($process.ExitCode -ne 0) {
                    throw "Command failed ($($process.ExitCode)): $Executable $($Arguments -join ' ')`n$output`n$errors"
                }
                return $output
            } finally {
                $process.Dispose()
            }
        }

        $platform = (& $Python -I -c 'import sysconfig; print(sysconfig.get_platform().replace("-", "_"))').Trim()
        if ($LASTEXITCODE -ne 0 -or $platform -notin @('win_amd64', 'win_arm64')) {
            throw 'The sample requires Windows x64 or ARM64 CPython.'
        }
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
        $script:tempDir = New-TempTestDirectory -Prefix 'python-identity'
        $script:guideDir = Join-Path $tempDir 'guide-project'
        $script:existingDir = Join-Path $tempDir 'existing-sample'
        foreach ($directory in @($guideDir, $existingDir)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
            Copy-Item -LiteralPath (Join-Path $sampleDir 'app.py') -Destination $directory
        }
        [IO.File]::WriteAllText((Join-Path $guideDir 'pyproject.toml'), @'
[project]
name = "python-identity"
version = "0.0.0"
requires-python = ">=3.11,<3.15"
dependencies = []
'@)
        Copy-Item -LiteralPath (Join-Path $sampleDir 'pyproject.toml') -Destination $existingDir
        $tool = Join-Path $tempDir 'tool'
        Invoke-SampleCommand $Python @('-I', '-m', 'venv', $tool) | Out-Null
        $script:toolPython = Join-Path $tool 'Scripts\python.exe'
        Invoke-SampleCommand $toolPython @('-I', '-m', 'pip', 'install', '--disable-pip-version-check',
            '--no-input', $wheels[0].FullName) | Out-Null
        $script:cli = Join-Path $tool 'Scripts\winapp.exe'
        $script:identityName = "Winapp.PythonIdentitySample.$([guid]::NewGuid().ToString('N').Substring(0, 16)).debug"
        if (@(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count) {
            throw 'The unique test identity already exists; refusing to replace it.'
        }
    }

    AfterAll {
        if (-not $script:tempDir) { return }
        if (Test-Path -LiteralPath (Join-Path $script:guideDir '.winapp\python-identity.json')) {
            Invoke-SampleCommand $script:cli @('identity', 'clear', '--project', $script:guideDir) | Out-Null
        }
        if ($script:identityName -and
            @(Get-AppxPackage -Name $script:identityName | Where-Object Name -EQ $script:identityName).Count) {
            throw "Test identity is still registered. Files preserved at $script:tempDir."
        }
        if ($SkipCleanup) { Write-Host "Test files preserved: $script:tempDir" }
        else { Remove-TempTestDirectory -Path $script:tempDir }
    }

    Context 'Phase 1: From-scratch Python guide' {
        It 'Lets init create the bindings table before selecting the Package API' -Skip:$script:skip {
            Invoke-SampleCommand $cli @('init', '.', '--use-defaults', '--add-python-bindings') $guideDir |
                Should -Match 'Python bindings ready'
            $edit = @'
from pathlib import Path
import sys, tomlkit
p = Path(sys.argv[1])
d = tomlkit.parse(p.read_text(encoding="utf-8"))
t = d["tool"]["winapp"]["python-bindings"]
assert t["codegen-version"]
t["scope"] = "types"
t["types"] = ["Windows.ApplicationModel.Package"]
p.write_text(tomlkit.dumps(d), encoding="utf-8")
'@
            Invoke-SampleCommand $toolPython @('-I', '-c', $edit, (Join-Path $guideDir 'pyproject.toml')) | Out-Null
            Invoke-SampleCommand $cli @('generate') $guideDir | Should -Match 'Python bindings ready'
            [xml]$manifest = Get-Content -LiteralPath (Join-Path $guideDir 'Package.appxmanifest') -Raw
            $manifest.Package.Identity.Name = $identityName.Substring(0, $identityName.Length - '.debug'.Length)
            $manifest.Save((Join-Path $guideDir 'Package.appxmanifest'))
        }

        It 'Prints Not packaged through the ordinary project environment' -Skip:$script:skip {
            (Invoke-SampleCommand $cli @('run', '.\app.py') $guideDir).Trim() | Should -Be 'Not packaged'
        }

        It 'Prints the family name from the real projected API with development identity' -Skip:$script:skip {
            $output = (Invoke-SampleCommand $cli @('run', '--identity', '.\app.py') $guideDir).Trim()
            $package = @(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName)
            $package.Count | Should -Be 1
            $output | Should -Be "Package Family Name: $($package[0].PackageFamilyName)"
            $record = Get-Content -LiteralPath (Join-Path $guideDir '.winapp\python-identity.json') -Raw | ConvertFrom-Json
            $record.state | Should -Be 'ready'
            $record.entry | Should -Not -Be (Join-Path $guideDir '.venv\Scripts\python.exe')
        }

        It 'Clears only the development identity and preserves ordinary execution' -Skip:$script:skip {
            Invoke-SampleCommand $cli @('identity', 'clear') $guideDir | Out-Null
            @(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count | Should -Be 0
            Join-Path $guideDir '.winapp\python-identity' | Should -Not -Exist
            Join-Path $guideDir '.venv\Scripts\python.exe' | Should -Exist
            (Invoke-SampleCommand $cli @('run', '.\app.py') $guideDir).Trim() | Should -Be 'Not packaged'
        }
    }

    Context 'Phase 2: Existing sample and MSIX workflow' {
        It 'Checks the source and guide identity branches without changing the sample' -Skip:$script:skip {
            Invoke-SampleCommand $Python @('-I', '-B', '-m', 'unittest', 'discover',
                '-s', $sampleDir, '-p', 'test_app.py') | Out-Null
        }

        It 'Restores the existing source with its recorded generator version' -Skip:$script:skip {
            Copy-Item -LiteralPath (Join-Path $guideDir 'winapp.yaml') -Destination $existingDir
            Copy-Item -LiteralPath (Join-Path $guideDir 'Package.appxmanifest') -Destination $existingDir
            Copy-Item -LiteralPath (Join-Path $guideDir 'Assets') -Destination $existingDir -Recurse
            $configuration = Get-Content -LiteralPath (Join-Path $existingDir 'pyproject.toml') -Raw
            Invoke-SampleCommand $cli @('restore') $existingDir | Out-Null
            Get-Content -LiteralPath (Join-Path $existingDir 'pyproject.toml') -Raw | Should -Be $configuration
            (Invoke-SampleCommand $cli @('run', '.\app.py') $existingDir).Trim() | Should -Be 'Not packaged'
        }

        It 'Builds a console application directory with Python and dynamic bindings' -Skip:$script:skip {
            $script:appPython = Join-Path $existingDir '.venv\Scripts\python.exe'
            Invoke-SampleCommand $appPython @('-I', '-B', '-m', 'PyInstaller', '--clean', '--noconfirm',
                '--onedir', '--console', '--name', 'PythonIdentity', '--paths', '.\.winapp\python',
                '--collect-all', 'dynwinrt', '--collect-submodules', 'winapp_bindings', '.\app.py') $existingDir |
                Out-Null
            $script:dist = Join-Path $existingDir 'dist\PythonIdentity'
            Join-Path $dist 'PythonIdentity.exe' | Should -Exist
            (Invoke-SampleCommand (Join-Path $dist 'PythonIdentity.exe') @() $existingDir).Trim() |
                Should -Be 'Not packaged'
        }

        It 'Adds an installed console alias and creates a signed MSIX without trusting certificates' -Skip:$script:skip {
            Invoke-SampleCommand $cli @('manifest', 'add-alias', '--name', 'PythonIdentity.exe') $existingDir |
                Out-Null
            Invoke-SampleCommand $cli @('cert', 'generate', '--manifest', '.\Package.appxmanifest',
                '--output', '.\devcert.pfx') $existingDir | Out-Null
            $script:msix = Join-Path $existingDir 'dist\PythonIdentity.msix'
            Invoke-SampleCommand $cli @('package', '.\dist\PythonIdentity',
                '--manifest', '.\Package.appxmanifest', '--executable', 'PythonIdentity.exe',
                '--cert', '.\devcert.pfx', '--output', $msix) $existingDir | Out-Null
            $msix | Should -Exist
            $archive = [IO.Compression.ZipFile]::OpenRead($msix)
            try {
                @($archive.Entries | Where-Object FullName -EQ 'PythonIdentity.exe').Count | Should -Be 1
                @($archive.Entries | Where-Object FullName -EQ 'AppxSignature.p7x').Count | Should -Be 1
                $entry = $archive.GetEntry('AppxManifest.xml')
                $entry | Should -Not -BeNullOrEmpty
                $reader = [IO.StreamReader]::new($entry.Open())
                try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
                $namespace.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
                $alias = $document.SelectSingleNode('//uap5:ExecutionAlias', $namespace)
                $alias.Alias | Should -Be 'PythonIdentity.exe'
            } finally {
                $archive.Dispose()
            }
        }
    }
}
