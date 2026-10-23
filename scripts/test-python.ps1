# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [string]$WinappPath = (Join-Path $PSScriptRoot '..\artifacts\cli\win-x64\winapp.exe'),
    [Parameter(Mandatory, ParameterSetName = 'Existing')]
    [string]$WheelPath,
    [string]$Python = 'python',
    [string]$NuGetConfigPath,
    [switch]$IncludeIdentity,
    [switch]$IncludeUv,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\python')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'src\winapp-python'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$temporary = [IO.Directory]::CreateTempSubdirectory('winapp-python-').FullName
$identityProject = $null
$identityName = $null
$savedCliPath = $env:WINAPP_CLI_PATH
$savedCacheDirectory = $env:WINAPP_CLI_CACHE_DIRECTORY
$savedUpdateCheck = $env:WINAPP_CLI_UPDATE_CHECK
$env:WINAPP_CLI_PATH = $null
$env:WINAPP_CLI_CACHE_DIRECTORY = Join-Path $temporary 'native-cache'
$env:WINAPP_CLI_UPDATE_CHECK = '0'

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    $previousTimeout = $env:PIP_DEFAULT_TIMEOUT
    $bootstrapPip = $Arguments.Count -ge 2 -and $Arguments[0] -eq '-m' -and $Arguments[1] -eq 'pip' `
        -and -not $env:PIP_DEFAULT_TIMEOUT -and -not $env:PIP_TIMEOUT
    try {
        if ($bootstrapPip) { $env:PIP_DEFAULT_TIMEOUT = '60' }
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Executable failed with exit code $LASTEXITCODE."
        }
    } finally {
        if ($bootstrapPip) { $env:PIP_DEFAULT_TIMEOUT = $previousTimeout }
    }
}

try {
    $environment = Join-Path $temporary 'venv'
    Invoke-Checked $Python @('-m', 'venv', $environment)
    $interpreter = Join-Path $environment 'Scripts\python.exe'
    $platform = (& $interpreter -c 'import sysconfig; print(sysconfig.get_platform().replace("-", "_"))').Trim()
    $architectures = @{ win_amd64 = 'x64'; win_arm64 = 'arm64' }
    if ($LASTEXITCODE -ne 0 -or -not $architectures.ContainsKey($platform)) {
        throw "Python wheel validation requires Windows x64 or ARM64 Python."
    }
    if ($PSCmdlet.ParameterSetName -eq 'Build') {
        $publishedExe = (Resolve-Path -LiteralPath $WinappPath).Path
        $cliRoot = Split-Path (Split-Path $publishedExe -Parent) -Parent
        & (Join-Path $PSScriptRoot 'package-python.ps1') -CliPath $cliRoot `
            -Architecture $architectures[$platform] -Python $interpreter -OutputDirectory $output
        $wheels = @(Get-ChildItem -LiteralPath $output -Filter "microsoft_winappcli-*-py3-none-$platform.whl" -File)
    } else {
        $wheels = @(Get-Item -LiteralPath (Resolve-Path -LiteralPath $WheelPath).Path)
    }
    if ($wheels.Count -ne 1) {
        throw "Expected one $platform microsoft-winappcli wheel. Remove older wrapper wheels and retry."
    }
    $verifyWheel = @'
from email.parser import Parser
from pathlib import Path
import sys, zipfile
wheel = Path(sys.argv[1])
assert wheel.name.startswith("microsoft_winappcli-"), wheel
assert wheel.name.endswith(f"-py3-none-{sys.argv[2]}.whl"), wheel
with zipfile.ZipFile(wheel) as archive:
    names = archive.namelist()
    assert "winappcli/_native/winapp.exe" in names
    assert "winappcli/_native/LICENSE" in names
    assert not any(name.startswith(("winapp_cli/", "winapp/")) for name in names)
    assert not any(name.lower().endswith(".pdb") for name in names)
    metadata = archive.read(next(name for name in names if name.endswith(".dist-info/WHEEL"))).decode()
    assert "Root-Is-Purelib: false" in metadata
    assert f"Tag: py3-none-{sys.argv[2]}" in metadata
    package_metadata = archive.read(next(name for name in names if name.endswith(".dist-info/METADATA"))).decode()
    assert Parser().parsestr(package_metadata)["Name"] == "microsoft-winappcli"
'@
    Invoke-Checked $interpreter @('-c', $verifyWheel, $wheels[0].FullName, $platform)
    Invoke-Checked $interpreter @('-m', 'pip', 'install', '--disable-pip-version-check', '--no-input', $wheels[0].FullName)
    $cli = (& $interpreter -c 'from winappcli.tools import find_native_cli; print(find_native_cli(None))').Trim()
    if ($LASTEXITCODE -ne 0 -or -not $cli.StartsWith($environment, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed Python wheel did not resolve its own bundled native CLI: $cli"
    }
    Invoke-Checked $interpreter @('-m', 'unittest', 'discover', '-s', (Join-Path $package 'tests'), '-v')
    # Both entries must see the same first-run state, without inheriting the machine's native cache.
    foreach ($cacheState in @('cold', 'warm')) {
        $env:WINAPP_CLI_CACHE_DIRECTORY = Join-Path $temporary 'console-native-cache'
        $scriptVersion = & (Join-Path $environment 'Scripts\winapp.exe') --version
        if ($LASTEXITCODE -ne 0 -or ($scriptVersion -join "`n") -notlike '*Python Package: microsoft-winappcli v*') {
            throw "The $cacheState-cache console entry did not report the renamed distribution: $scriptVersion"
        }
        $env:WINAPP_CLI_CACHE_DIRECTORY = Join-Path $temporary 'module-native-cache'
        $moduleVersion = & $interpreter -m winappcli --version
        if ($LASTEXITCODE -ne 0 -or ($moduleVersion -join "`n") -ne ($scriptVersion -join "`n")) {
            throw "The $cacheState-cache module entry did not match the console entry.`nConsole:`n$($scriptVersion -join "`n")`nModule:`n$($moduleVersion -join "`n")"
        }
    }
    Invoke-Checked $interpreter @('-I', '-c',
        'import importlib.util as u; assert u.find_spec("winappcli") is not None; assert u.find_spec("winapp_cli") is None; assert u.find_spec("winapp") is None')
    $nativeHelp = & $cli --help
    if ($LASTEXITCODE -ne 0) { throw "Native root help failed." }
    $pythonCallerHelp = & $cli --caller python --help
    if ($LASTEXITCODE -ne 0 -or ($pythonCallerHelp -join "`n") -ne ($nativeHelp -join "`n")) {
        throw "The native CLI still changes its help for the Python caller."
    }
    $selfReference = & $interpreter -m winappcli --winapp (Join-Path $environment 'Scripts\winapp.exe') --help 2>&1
    if ($LASTEXITCODE -eq 0 -or ($selfReference -join "`n") -notlike '*Refusing to use a Python wrapper*') {
        throw "The Python command was not rejected as its own native CLI: $selfReference"
    }
    Invoke-Checked (Join-Path $environment 'Scripts\winapp.exe') @('--help')
    Invoke-Checked (Join-Path $environment 'Scripts\winapp.exe') @('manifest', '--help')
    Invoke-Checked (Join-Path $environment 'Scripts\winapp.exe') @('tool', '--help')
    Invoke-Checked (Join-Path $environment 'Scripts\winapp.exe') @('--winapp', $cli, 'manifest', '--help')

    $detectionProject = Join-Path $temporary 'python-detection-project'
    New-Item -ItemType Directory -Path $detectionProject | Out-Null
    [IO.File]::WriteAllText((Join-Path $detectionProject 'pyproject.toml'),
        "[project]`nname = 'python-detection-project'`nversion = '0.0.0'`n")
    $detectedOutput = & $cli init $detectionProject --config-only --use-defaults 2>&1
    if ($LASTEXITCODE -ne 0 -or
        @($detectedOutput | Where-Object { $_ -like '*Possible Python project detected*' }).Count -ne 0 -or
        @($detectedOutput | Where-Object { $_ -like '*No known project type*' }).Count -ne 1 -or
        -not (Test-Path -LiteralPath (Join-Path $detectionProject 'winapp.yaml'))) {
        throw "Bundled native CLI did not initialize the explicit directory without Python detection: $detectedOutput"
    }

    $sdkProject = Join-Path $temporary 'sdk-project'
    New-Item -ItemType Directory -Path $sdkProject | Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'tests\fixtures\uri\winapp.yaml') -Destination $sdkProject
    if ($NuGetConfigPath) {
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $sdkProject 'nuget.config')
    }
    Invoke-Checked $interpreter @('-m', 'winappcli', 'restore', '--project', $sdkProject)
    Invoke-Checked $interpreter @('-m', 'winappcli', 'update', '--project', $sdkProject)
    Invoke-Checked $interpreter @('-c',
        'import importlib.metadata as m, sys; sys.exit(int(any(d.metadata["Name"].lower() == "dynwinrt-codegen" for d in m.distributions())))')
    if (Test-Path -LiteralPath (Join-Path $sdkProject '.winapp\python')) {
        throw "A project without Python configuration unexpectedly generated bindings."
    }

    $project = Join-Path $temporary 'uri project'
    New-Item -ItemType Directory -Path $project | Out-Null
    foreach ($name in @('app.py', 'pyproject.toml', 'winapp.yaml', 'requirements.txt', 'requirements-dev.txt')) {
        Copy-Item -LiteralPath (Join-Path $package "tests\fixtures\uri\$name") -Destination $project
    }
    if ($NuGetConfigPath) {
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $project 'nuget.config')
    }
    $appEnvironment = Join-Path $project '.venv'
    Invoke-Checked $Python @('-m', 'venv', '--without-pip', $appEnvironment)
    $appPython = Join-Path $appEnvironment 'Scripts\python.exe'
    Invoke-Checked $interpreter @('-m', 'winappcli', 'init', $project, '--config-only', '--use-defaults', '--add-python-bindings')
    Invoke-Checked $interpreter @('-m', 'winappcli', 'restore', '--project', $project)
    Invoke-Checked $interpreter @('-m', 'winappcli', 'generate', '--project', $project)
    Invoke-Checked $interpreter @('-c',
        'import importlib.util as u; assert u.find_spec("dynwinrt") is None; assert u.find_spec("myapp_winrt") is None')
    Invoke-Checked $appPython @('-c',
        'import importlib.util as u; assert u.find_spec("pip") is None; assert u.find_spec("winappcli") is None')
    foreach ($directory in @('include', 'lib')) {
        if (-not (Test-Path -LiteralPath (Join-Path $project ".winapp\$directory") -PathType Container)) {
            throw "Full native restore did not create the C++ $directory directory."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $project '.winapp\include\winrt\Windows.Foundation.h') -PathType Leaf)) {
        throw "Full native restore did not generate C++/WinRT headers."
    }
    $actual = & $appPython (Join-Path $project 'app.py')
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com') {
        throw "Editable bindings did not produce the expected Uri output: $actual"
    }

    $bindings = Join-Path $project '.winapp\python\myapp_winrt'
    Invoke-Checked $interpreter @('-m', 'pip', 'wheel', '--disable-pip-version-check', '--no-input', '--no-deps', $bindings, '-w', $output)
    $bindingWheels = @(Get-ChildItem -LiteralPath $output -Filter 'myapp_winrt-*.whl' -File)
    if ($bindingWheels.Count -ne 1) {
        throw "Expected one generated bindings wheel in $output."
    }

    $consumer = Join-Path $temporary 'consumer'
    Invoke-Checked $Python @('-m', 'venv', $consumer)
    $consumerPython = Join-Path $consumer 'Scripts\python.exe'
    Invoke-Checked $consumerPython @('-m', 'pip', 'install', '--disable-pip-version-check', '--no-input', '--only-binary=dynwinrt', $bindingWheels[0].FullName)
    Copy-Item -LiteralPath (Join-Path $project 'app.py') -Destination (Join-Path $consumer 'app.py')
    $actual = & $consumerPython (Join-Path $consumer 'app.py')
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com') {
        throw "Installed bindings wheel did not produce the expected Uri output: $actual"
    }

    $previousPins = Get-Content -LiteralPath (Join-Path $project 'winapp.yaml') -Raw
    $previousMarker = Get-Content -LiteralPath (Join-Path $bindings '.winapp-python.json') -Raw | ConvertFrom-Json
    Invoke-Checked $interpreter @('-m', 'winappcli', 'update', '--project', $project)
    Invoke-Checked $interpreter @('-c',
        'from pathlib import Path; from winappcli.metadata import load_inventory; import sys; load_inventory(Path(sys.argv[1]))',
        $project)
    $currentPins = Get-Content -LiteralPath (Join-Path $project 'winapp.yaml') -Raw
    $currentMarker = Get-Content -LiteralPath (Join-Path $bindings '.winapp-python.json') -Raw | ConvertFrom-Json
    if ($previousPins -ne $currentPins -and $previousMarker.inputs_digest -eq $currentMarker.inputs_digest) {
        throw "Updating SDK pins did not regenerate the Python bindings."
    }
    $actual = & $appPython (Join-Path $project 'app.py')
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com') {
        throw "Bindings did not run after full native update and restore: $actual"
    }

    $clone = Join-Path $temporary 'fresh requirements clone'
    New-Item -ItemType Directory -Path $clone | Out-Null
    foreach ($name in @('app.py', 'pyproject.toml', 'winapp.yaml', 'requirements.txt', 'requirements-dev.txt')) {
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $clone
    }
    if ($NuGetConfigPath) {
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $clone 'nuget.config')
    }
    [IO.File]::WriteAllText((Join-Path $clone 'venv.py'), "raise SystemExit('PROJECT_VENV_EXECUTED')`n")
    [IO.File]::WriteAllText((Join-Path $clone 'pip.py'), "raise SystemExit('PROJECT_PIP_EXECUTED')`n")
    [IO.File]::AppendAllText((Join-Path $clone 'requirements.txt'),
        "packaging==26.3 \`n    ; python_version >= '3.11'`n")
    $jsonStderr = Join-Path $temporary 'unsupported-json.stderr.log'
    $rejectedJson = & $interpreter -m winappcli --json restore --project $clone 2> $jsonStderr
    if ($LASTEXITCODE -ne 2 -or @($rejectedJson).Count -ne 0 -or
        (Get-Content -LiteralPath $jsonStderr -Raw) -notlike '*--json is not supported for Python workflows*' -or
        (Test-Path -LiteralPath (Join-Path $clone '.winapp'))) {
        throw "Python JSON mode did not fail before restoring the project: $rejectedJson"
    }
    $quietOutput = & $interpreter -m winappcli --quiet restore --project $clone
    if ($LASTEXITCODE -ne 0 -or @($quietOutput).Count -ne 0) {
        throw "Leading quiet mode did not restore Python bindings without status output: $quietOutput"
    }
    $clonePython = Join-Path $clone '.venv\Scripts\python.exe'
    Invoke-Checked $clonePython @('-c',
        'import importlib.metadata as m; assert m.version("packaging") == "26.3"')
    $actual = & $clonePython (Join-Path $clone 'app.py')
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com') {
        throw "Fresh requirements clone was not usable after restore: $actual"
    }
    $runStderr = Join-Path $temporary 'quiet-run.stderr.log'
    $actual = & $interpreter -m winappcli run -q --project $clone app.py 2> $runStderr
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com' -or
        -not [string]::IsNullOrEmpty((Get-Content -LiteralPath $runStderr -Raw))) {
        throw "Quiet Python run changed application output or printed wrapper status: $actual"
    }

    # Exercise default metadata plus an additional Windows type.
    $nonUiProject = Join-Path $temporary 'non-ui-project'
    New-Item -ItemType Directory -Path $nonUiProject | Out-Null
    foreach ($name in @('app.py', 'pyproject.toml', 'winapp.yaml')) {
        Copy-Item -LiteralPath (Join-Path $package "tests\fixtures\appsdk-nonui\$name") -Destination $nonUiProject
    }
    if ($NuGetConfigPath) {
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $nonUiProject 'nuget.config')
    }
    Invoke-Checked $Python @('-m', 'venv', '--without-pip', (Join-Path $nonUiProject '.venv'))
    $nonUiPython = Join-Path $nonUiProject '.venv\Scripts\python.exe'
    Invoke-Checked $interpreter @('-m', 'winappcli', 'init', $nonUiProject, '--config-only', '--use-defaults', '--add-python-bindings')
    Invoke-Checked $interpreter @('-m', 'winappcli', 'restore', '--project', $nonUiProject)
    $nonUiPackage = Join-Path $nonUiProject '.winapp\python\winapp_bindings'
    if (-not (Test-Path -LiteralPath (Join-Path $nonUiPackage 'pyproject.toml'))) {
        throw "Default init did not create an installable Python bindings package."
    }
    $marker = Get-Content -LiteralPath (Join-Path $nonUiPackage '.winapp-python.json') -Raw | ConvertFrom-Json
    if ($marker.scope -ne 'default') {
        throw "Default Python init did not select the default metadata policy."
    }
    $configuration = Join-Path $nonUiProject 'pyproject.toml'
    $configured = [IO.File]::ReadAllText($configuration).Replace('additional-winmds = []',
        'additional-winmds = [{namespace = "Windows.Storage", classes = ["ApplicationData"]}]')
    [IO.File]::WriteAllText($configuration, $configured)
    Invoke-Checked $interpreter @('-m', 'winappcli', 'generate', '--project', $nonUiProject)
    Invoke-Checked $nonUiPython @('-c',
        'from winapp_bindings.windows.storage import ApplicationData; from winapp_bindings.microsoft.windows.app_notifications import AppNotification')
    $again = & $interpreter -m winappcli restore --project $nonUiProject
    if ($LASTEXITCODE -ne 0 -or @($again | Where-Object { $_ -like '*Bindings current:*' }).Count -ne 1) {
        throw "Unchanged non-UI AppSDK metadata should skip code generation on restore: $again"
    }
    $actual = & $nonUiPython (Join-Path $nonUiProject 'app.py')
    if ($LASTEXITCODE -ne 0 -or ($actual -join "`n").Trim() -ne 'Host: example.com') {
        throw "Default non-UI bindings did not produce the expected Uri output: $actual"
    }

    $standardProject = Join-Path $temporary 'standard pyproject'
    New-Item -ItemType Directory -Path $standardProject | Out-Null
    [IO.File]::WriteAllText((Join-Path $standardProject 'pyproject.toml'), @'
[project]
name = "winapp-standard-example"
version = "0.0.0"
requires-python = ">=3.11,<3.15"
dependencies = ["packaging==26.3"]

[tool.winapp.python-bindings]
package = "myapp_winrt"
scope = "types"
types = ["Windows.Foundation.Uri"]
'@)
    foreach ($name in @('app.py', 'winapp.yaml')) {
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $standardProject
    }
    if ($NuGetConfigPath) {
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $standardProject 'nuget.config')
    }
    Invoke-Checked $interpreter @('-m', 'winappcli', 'init', $standardProject,
        '--use-defaults', '--config-only', '--add-python-bindings')
    if (Test-Path -LiteralPath (Join-Path $standardProject '.winapp\python')) {
        throw "Config-only initialization generated bindings before restore."
    }
    Invoke-Checked $interpreter @('-m', 'winappcli', 'restore', '--project', $standardProject)
    Invoke-Checked (Join-Path $standardProject '.venv\Scripts\python.exe') @('-c',
        'from myapp_winrt.windows.foundation import Uri; import dynwinrt, packaging')
    if (Test-Path -LiteralPath (Join-Path $standardProject 'requirements.txt')) {
        throw "Standard pyproject setup created a redundant requirements inventory."
    }

    $uvProject = $null
    if ($IncludeUv) {
        $uv = (Get-Command uv -ErrorAction Stop).Source
        $uvProject = Join-Path $temporary 'uv-project'
        New-Item -ItemType Directory -Path $uvProject | Out-Null
        [IO.File]::WriteAllText((Join-Path $uvProject 'pyproject.toml'), @'
[project]
name = "winapp-uv-example"
version = "0.0.0"
requires-python = ">=3.11,<3.15"
dependencies = ["packaging==26.3"]

[tool.uv]

[tool.winapp.python-bindings]
package = "myapp_winrt"
scope = "types"
types = ["Windows.Foundation.Uri"]
'@)
        foreach ($name in @('app.py', 'winapp.yaml')) {
            Copy-Item -LiteralPath (Join-Path $project $name) -Destination $uvProject
        }
        if ($NuGetConfigPath) {
            Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $uvProject 'nuget.config')
        }
        Invoke-Checked $Python @('-m', 'venv', '--without-pip', (Join-Path $uvProject '.venv'))
        $uvPython = Join-Path $uvProject '.venv\Scripts\python.exe'
        Invoke-Checked $interpreter @('-m', 'winappcli', 'init', $uvProject, '--config-only', '--use-defaults', '--add-python-bindings')
        Invoke-Checked $interpreter @('-m', 'winappcli', 'restore', '--project', $uvProject)
        Push-Location $uvProject
        try {
            Invoke-Checked $uv @('sync', '--locked')
        } finally {
            Pop-Location
        }
        Invoke-Checked $uvPython @('-c', 'from myapp_winrt.windows.foundation import Uri; import dynwinrt')
        $uvClone = Join-Path $temporary 'fresh uv clone'
        New-Item -ItemType Directory -Path $uvClone | Out-Null
        foreach ($name in @('app.py', 'pyproject.toml', 'winapp.yaml', 'uv.lock')) {
            Copy-Item -LiteralPath (Join-Path $uvProject $name) -Destination $uvClone
        }
        if ($NuGetConfigPath) {
            Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $uvClone 'nuget.config')
        }
        $quietOutput = & $interpreter -m winappcli restore --quiet --project $uvClone
        if ($LASTEXITCODE -ne 0 -or @($quietOutput).Count -ne 0) {
            throw "Trailing quiet mode did not restore uv Python bindings without status output: $quietOutput"
        }
        Invoke-Checked (Join-Path $uvClone '.venv\Scripts\python.exe') @('-c', 'from myapp_winrt.windows.foundation import Uri; import dynwinrt')
    }

    if ($IncludeIdentity) {
        $identityProject = Join-Path $temporary 'identity-project'
        New-Item -ItemType Directory -Path $identityProject | Out-Null
        Copy-Item -LiteralPath (Join-Path $package 'tests\fixtures\uri\identity_probe.py') -Destination $identityProject
        if ($NuGetConfigPath) {
            Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $identityProject 'nuget.config')
        }
        $identityName = 'Winapp.PythonPreview.' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '.debug'
        $originalName = $identityName.Substring(0, $identityName.Length - '.debug'.Length)
        if (@(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count -ne 0) {
            throw "The identity '$identityName' already exists; refusing to replace it."
        }
        Invoke-Checked $Python @('-m', 'venv', (Join-Path $identityProject '.venv'))
        $identityPython = Join-Path $identityProject '.venv\Scripts\python.exe'
        Invoke-Checked $identityPython @('-m', 'pip', 'install', '--disable-pip-version-check',
            '--no-input', $wheels[0].FullName, $bindingWheels[0].FullName)
        foreach ($name in @('pyproject.toml', 'winapp.yaml', 'requirements.txt', 'requirements-dev.txt')) {
            Copy-Item -LiteralPath (Join-Path $project $name) -Destination $identityProject
        }
        Invoke-Checked $interpreter @('-m', 'winappcli', 'restore',
            '--project', $identityProject)
        [IO.File]::AppendAllText((Join-Path $identityProject 'requirements.txt'),
            "packaging==26.3`n")
        Invoke-Checked $cli @('manifest', 'generate', $identityProject,
            '--package-name', $originalName, '--publisher-name', 'CN=WinappPythonPreview')
        [IO.File]::WriteAllText((Join-Path $identityProject 'venv.py'), "raise SystemExit('PROJECT_VENV_EXECUTED')`n")
        [IO.File]::WriteAllText((Join-Path $identityProject 'pip.py'), "raise SystemExit('PROJECT_PIP_EXECUTED')`n")
        $placeholderManifest = Join-Path $identityProject 'placeholder.appxmanifest'
        [xml]$placeholderDocument = Get-Content -LiteralPath (Join-Path $identityProject 'Package.appxmanifest') -Raw
        $placeholderDocument.Package.Identity.Name = $originalName + '.$targetnametoken$'
        $placeholderDocument.Save($placeholderManifest)
        $rejectedIdentity = & $interpreter -m winappcli run --identity --project $identityProject `
            --manifest $placeholderManifest identity_probe.py 2>&1
        if ($LASTEXITCODE -eq 0 -or ($rejectedIdentity -join "`n") -notlike '*literal Identity Name*' -or
            (Test-Path -LiteralPath (Join-Path $identityProject '.winapp\python-identity.json'))) {
            throw "Identity metadata placeholders were not rejected before preparation: $rejectedIdentity"
        }

        $plainOutput = & $identityPython (Join-Path $identityProject 'identity_probe.py')
        if ($LASTEXITCODE -ne 0) {
            throw "Ordinary Python did not run the identity probe."
        }
        $plain = @($plainOutput | Where-Object { $_ -like 'IDENTITY_JSON:*' } |
            ForEach-Object { $_.Substring('IDENTITY_JSON:'.Length) } | ConvertFrom-Json)
        if ($plain.Count -ne 1 -or $plain[0].code -ne 15700 -or $plain[0].host -ne 'example.com') {
            throw "Ordinary Python unexpectedly had application identity: $plainOutput"
        }

        $identityStderr = Join-Path $temporary 'identity.stderr.log'
        $identityPyproject = Join-Path $identityProject 'pyproject.toml'
        $identityPyprojectHash = (Get-FileHash -LiteralPath $identityPyproject -Algorithm SHA256).Hash
        $identifiedOutput = & $identityPython -m winappcli run --identity --project $identityProject `
            identity_probe.py -- --children from-ci 2> $identityStderr
        if ($LASTEXITCODE -ne 0) {
            throw "Private Python identity registration or launch failed: $identifiedOutput`n$(Get-Content -LiteralPath $identityStderr -Raw)"
        }
        $identified = @($identifiedOutput | Where-Object { $_ -like 'IDENTITY_JSON:*' } |
            ForEach-Object { $_.Substring('IDENTITY_JSON:'.Length) } | ConvertFrom-Json)
        if (@($identifiedOutput).Count -ne 1 -or $identified.Count -ne 1 -or $identified[0].code -ne 0 -or
            -not $identified[0].package.StartsWith("$identityName`_") -or
            $identified[0].python_prefix -ne (Join-Path $identityProject '.winapp\python-identity\venv') -or
            $identified[0].process_image -ne (Join-Path $identityProject '.winapp\python-identity\venv\runtime\python.exe') -or
            $identified[0].host -ne 'example.com' -or
            @($identified[0].arguments).Count -ne 1 -or $identified[0].arguments[0] -ne 'from-ci') {
            throw "Private Python did not have the expected own identity and venv: $identifiedOutput"
        }
        $expectedVersion = (& $identityPython -c 'import sys; print(sys.version.split()[0])').Trim()
        if ($identified[0].python_version -ne $expectedVersion) {
            throw "PBS identity did not match the project's exact Python version."
        }
        foreach ($child in $identified[0].children.PSObject.Properties.Value) {
            if ($child.code -ne 0 -or $child.package -ne $identified[0].package -or
                $child.python_version -ne $expectedVersion -or $child.process_image -ne $identified[0].process_image -or
                $child.python_prefix -ne $identified[0].python_prefix -or $child.host -ne 'example.com') {
                throw "PBS child did not preserve identity and dependencies: $($child | ConvertTo-Json -Compress)"
            }
        }
        $record = Get-Content -LiteralPath (Join-Path $identityProject '.winapp\python-identity.json') -Raw | ConvertFrom-Json
        if ($record.interpreter.version -ne $expectedVersion -or $record.state -ne 'ready') {
            throw "PBS ownership record does not contain the selected interpreter and ready state."
        }
        if ((Get-FileHash -LiteralPath $identityPyproject -Algorithm SHA256).Hash -ne $identityPyprojectHash) {
            throw "Identity preparation changed pyproject.toml."
        }
        $again = & $identityPython -m winappcli run --identity --project $identityProject `
            identity_probe.py -- from-ci 2> $identityStderr
        $reuseDiagnostics = @(Get-Content -LiteralPath $identityStderr)
        if ($LASTEXITCODE -ne 0 -or
            @($reuseDiagnostics | Where-Object { $_ -eq 'Reusing development identity' }).Count -ne 1) {
            throw "Unchanged PBS identity was not reused: $again`n$($reuseDiagnostics -join "`n")"
        }
        $reused = @($again | Where-Object { $_ -like 'IDENTITY_JSON:*' } |
            ForEach-Object { $_.Substring('IDENTITY_JSON:'.Length) } | ConvertFrom-Json)
        $reusedRecord = Get-Content -LiteralPath (Join-Path $identityProject '.winapp\python-identity.json') -Raw | ConvertFrom-Json
        if (@($again).Count -ne 1 -or $reused.Count -ne 1 -or $reused[0].code -ne 0 -or
            $reused[0].package -ne $identified[0].package -or
            $reused[0].python_prefix -ne $identified[0].python_prefix -or
            $reused[0].process_image -ne $identified[0].process_image -or
            $reused[0].python_version -ne $expectedVersion -or $reused[0].host -ne 'example.com' -or
            @($reused[0].arguments).Count -ne 1 -or $reused[0].arguments[0] -ne 'from-ci' -or
            $reusedRecord.owner -ne $record.owner -or $reusedRecord.state -ne 'ready') {
            throw "Reused PBS identity changed ownership or did not preserve script output: $again"
        }
        $quietIdentity = & $identityPython -m winappcli -q run --identity --project $identityProject `
            identity_probe.py -- from-ci 2> $identityStderr
        $quietRecord = Get-Content -LiteralPath (Join-Path $identityProject '.winapp\python-identity.json') -Raw | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or ($quietIdentity -join "`n") -ne ($again -join "`n") -or
            -not [string]::IsNullOrEmpty((Get-Content -LiteralPath $identityStderr -Raw)) -or
            $quietRecord.owner -ne $record.owner) {
            throw "Quiet identity execution changed application output or printed wrapper status: $quietIdentity"
        }
        [IO.File]::AppendAllText((Join-Path $identityProject 'requirements.txt'), "# changed input`n")
        $rebuiltOutput = & $identityPython -m winappcli run --identity --project $identityProject `
            identity_probe.py -- --children 2> $identityStderr
        $rebuildDiagnostics = @(Get-Content -LiteralPath $identityStderr)
        if ($LASTEXITCODE -ne 0 -or
            @($rebuildDiagnostics | Where-Object { $_ -eq 'Rebuilding development identity' }).Count -ne 1) {
            throw "Changed PBS dependency inputs did not automatically rebuild the identity: $rebuiltOutput`n$($rebuildDiagnostics -join "`n")"
        }
        $rebuilt = @($rebuiltOutput | Where-Object { $_ -like 'IDENTITY_JSON:*' } |
            ForEach-Object { $_.Substring('IDENTITY_JSON:'.Length) } | ConvertFrom-Json)
        $newRecord = Get-Content -LiteralPath (Join-Path $identityProject '.winapp\python-identity.json') -Raw | ConvertFrom-Json
        if (@($rebuiltOutput).Count -ne 1 -or $newRecord.owner -eq $record.owner -or $newRecord.state -ne 'ready' -or
            $rebuilt.Count -ne 1 -or $rebuilt[0].code -ne 0 -or
            $rebuilt[0].package -ne $identified[0].package -or
            $rebuilt[0].python_version -ne $expectedVersion -or $rebuilt[0].host -ne 'example.com') {
            throw "Automatically rebuilt PBS identity has the wrong ownership, version or package: $rebuiltOutput"
        }
        foreach ($child in $rebuilt[0].children.PSObject.Properties.Value) {
            if ($child.code -ne 0 -or $child.package -ne $rebuilt[0].package -or
                $child.python_version -ne $expectedVersion -or $child.process_image -ne $rebuilt[0].process_image -or
                $child.python_prefix -ne $rebuilt[0].python_prefix -or $child.host -ne 'example.com') {
                throw "Automatically rebuilt PBS child lost identity or dependencies: $($child | ConvertTo-Json -Compress)"
            }
        }
        Invoke-Checked $identityPython @('-m', 'winappcli', 'identity', 'clear',
            '--project', $identityProject)
        if (@(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count -ne 0) {
            throw "The Python identity test package remained registered after cleanup."
        }
        if (-not (Test-Path -LiteralPath $identityPython) -or
            (Test-Path -LiteralPath (Join-Path $identityProject '.winapp\python-identity'))) {
            throw "PBS cleanup removed the ordinary venv or left the private runtime."
        }
        if ((Get-FileHash -LiteralPath $identityPyproject -Algorithm SHA256).Hash -ne $identityPyprojectHash) {
            throw "Identity reuse, rebuild or cleanup changed pyproject.toml."
        }
        $identityName = $null

        if ($uvProject) {
            $identityProject = $uvProject
            $identityName = 'Winapp.PythonUvPreview.' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '.debug'
            $originalName = $identityName.Substring(0, $identityName.Length - '.debug'.Length)
            Copy-Item -LiteralPath (Join-Path $package 'tests\fixtures\uri\identity_probe.py') -Destination $uvProject
            Invoke-Checked $cli @('manifest', 'generate', $uvProject,
                '--package-name', $originalName, '--publisher-name', 'CN=WinappPythonPreview')
            $uvPyproject = Join-Path $uvProject 'pyproject.toml'
            $uvPyprojectHash = (Get-FileHash -LiteralPath $uvPyproject -Algorithm SHA256).Hash
            $uvIdentity = & $interpreter -m winappcli run --identity --project $uvProject identity_probe.py -- --children
            if ($LASTEXITCODE -ne 0) {
                throw "uv identity dependency synchronization failed: $uvIdentity"
            }
            $identified = @($uvIdentity | Where-Object { $_ -like 'IDENTITY_JSON:*' } |
                ForEach-Object { $_.Substring('IDENTITY_JSON:'.Length) } | ConvertFrom-Json)
            if ($identified.Count -ne 1 -or $identified[0].code -ne 0 -or $identified[0].host -ne 'example.com') {
                throw "uv identity did not have package identity and generated bindings: $uvIdentity"
            }
            Invoke-Checked $interpreter @('-m', 'winappcli', 'identity', 'clear', '--project', $uvProject)
            if (@(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count -ne 0) {
                throw "uv identity remained registered after cleanup."
            }
            if ((Get-FileHash -LiteralPath $uvPyproject -Algorithm SHA256).Hash -ne $uvPyprojectHash) {
                throw "uv identity preparation or cleanup changed pyproject.toml."
            }
            $identityName = $null
        }
    }

    Write-Host "Python wrapper, project dependency restore, additive bindings, wheels, and optional uv/identity succeeded."
} finally {
    $env:WINAPP_CLI_PATH = $savedCliPath
    $env:WINAPP_CLI_CACHE_DIRECTORY = $savedCacheDirectory
    $env:WINAPP_CLI_UPDATE_CHECK = $savedUpdateCheck
    if ($identityName) {
        $remaining = @(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName)
        if ($remaining.Count -gt 0) {
            $owned = Join-Path $identityProject '.winapp\python-identity.json'
            if ((Test-Path -LiteralPath $owned) -and (Test-Path -LiteralPath $interpreter)) {
                & $interpreter -m winappcli identity clear --project $identityProject
            }
            if (@(Get-AppxPackage -Name $identityName | Where-Object Name -EQ $identityName).Count -gt 0) {
                throw "Could not unregister the Python test identity. The test files were preserved at '$identityProject' for recovery."
            }
        }
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
