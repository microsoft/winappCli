# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

<#
.SYNOPSIS
Build Windows Python wheels, optionally publishing the native CLI first.
.PARAMETER BuildCli
Publish the native CLI before packaging. Defaults to the selected Python architecture.
Cannot be combined with CliPath.
.PARAMETER CliPath
Directory containing win-x64 and win-arm64 native CLI publishes.
.PARAMETER Architecture
Without BuildCli, package both platform wheels by default. Select All, x64 or arm64 explicitly.
.PARAMETER Version
Shared native/npm build version. Defaults to version.json plus the repository build number.
Prerelease versions such as 0.7.2-prerelease.37 become 0.7.2.dev37 for Python.
.PARAMETER Stable
Use the base version from version.json without a prerelease suffix.
.EXAMPLE
.\scripts\package-python.ps1 -Architecture x64
.EXAMPLE
.\scripts\package-python.ps1 -BuildCli
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Stable,
    [switch]$BuildCli,
    [string]$CliPath = (Join-Path $PSScriptRoot '..\artifacts\cli'),
    [ValidateSet('All', 'x64', 'arm64')]
    [string]$Architecture = 'All',
    [string]$Python = 'python',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\python')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'src\winapp-python'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$platforms = @{ x64 = 'win_amd64'; arm64 = 'win_arm64' }

Push-Location $root
try {
    $buildNumber = & (Join-Path $PSScriptRoot 'get-build-number.ps1')
    if ($LASTEXITCODE -ne 0 -or "$buildNumber" -notmatch '^\d+$') {
        throw 'Failed to calculate the repository build number.'
    }
    if ([string]::IsNullOrEmpty($Version)) {
        $baseVersion = (Get-Content -LiteralPath (Join-Path $root 'version.json') -Raw | ConvertFrom-Json).version
        if ($Stable) {
            $Version = $baseVersion
        } else {
            $label = & (Join-Path $PSScriptRoot 'get-prerelease-label.ps1')
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($label)) {
                throw 'Failed to calculate the repository prerelease label.'
            }
            $Version = "$baseVersion-$label.$buildNumber"
        }
    }
} finally {
    Pop-Location
}
$match = [regex]::Match($Version, '^(?<base>\d+\.\d+\.\d+)(?:-(?<label>[A-Za-z0-9-]+)\.(?<build>\d+))?$')
if (-not $match.Success -or ($Stable -and $match.Groups['label'].Success)) {
    throw "Unsupported shared build version '$Version'. Use major.minor.patch or major.minor.patch-label.build."
}
$baseVersion = $match.Groups['base'].Value
$pythonVersion = $baseVersion
if ($match.Groups['build'].Success) {
    $buildNumber = $match.Groups['build'].Value
    $pythonVersion = "$baseVersion.dev$buildNumber"
}
$assemblyVersion = "$baseVersion.$buildNumber"
Write-Host "[PYTHON] Shared version: $Version; Python version: $pythonVersion"

if ($BuildCli) {
    if ($PSBoundParameters.ContainsKey('CliPath')) {
        throw 'Use -BuildCli to publish the CLI or -CliPath to package existing publishes, not both.'
    }
    $probe = 'import platform, sys, sysconfig; assert sys.platform == "win32" and platform.python_implementation() == "CPython" and (3, 11) <= sys.version_info[:2] < (3, 15), "Use Windows CPython 3.11-3.14"; print(sysconfig.get_platform().replace("-", "_"))'
    $pythonPlatform = & $Python -I -c $probe
    if ($LASTEXITCODE -ne 0) {
        throw 'Local wheel builds require Windows CPython 3.11-3.14, x64 or ARM64.'
    }
    $detectedArchitecture = switch ($pythonPlatform) {
        'win_amd64' { 'x64' }
        'win_arm64' { 'arm64' }
        default { throw "Unsupported build Python platform: $pythonPlatform. Use Windows x64 or ARM64 CPython." }
    }
    if (-not $PSBoundParameters.ContainsKey('Architecture')) {
        $Architecture = $detectedArchitecture
    }
}

$architectures = if ($Architecture -eq 'All') { @('x64', 'arm64') } else { @($Architecture) }
$temporary = [IO.Directory]::CreateTempSubdirectory('winapp-python-package-').FullName
$originalPath = $env:PATH
$originalPlatform = $env:Platform
try {
    if ($BuildCli) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            # Nested Visual Studio environment scripts invoke vswhere by name.
            $env:PATH = "$(Split-Path $vswhere -Parent);$env:PATH"
        }
        $env:Platform = $null
        $cli = Join-Path $temporary 'cli'
        $cliProject = Join-Path $root 'src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj'
        foreach ($arch in $architectures) {
            Write-Host "[PYTHON] Publishing native CLI for $arch..."
            & dotnet publish $cliProject -c Release -r "win-$arch" --self-contained `
                -o (Join-Path $cli "win-$arch") `
                /p:Version=$assemblyVersion /p:AssemblyVersion=$assemblyVersion `
                /p:FileVersion=$assemblyVersion /p:InformationalVersion=$Version `
                /p:IncludeSourceRevisionInInformationalVersion=false
            if ($LASTEXITCODE -ne 0) {
                throw "Native CLI publish failed for $arch. Check the build output and Visual Studio C++ tools for that architecture."
            }
        }
    } else {
        $cli = (Resolve-Path -LiteralPath $CliPath).Path
    }

    foreach ($arch in $architectures) {
        $executable = Join-Path $cli "win-$arch\winapp.exe"
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Native CLI publish is missing: $executable. Use -BuildCli or build it before packaging Python wheels."
        }
        $probe = 'import sys; from pathlib import Path; sys.path.insert(0, sys.argv[1]); from winappcli.native import native_platform; actual=native_platform(Path(sys.argv[2])); assert actual==sys.argv[3], f"Expected {sys.argv[3]}, found {actual}"'
        & $Python -I -c $probe (Join-Path $package 'src') $executable $platforms[$arch]
        if ($LASTEXITCODE -ne 0) {
            throw "Native CLI publish has the wrong architecture or an invalid executable: $executable"
        }
        $nativeVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
        if ($nativeVersion -ne $Version) {
            throw "Native CLI version '$nativeVersion' does not match shared build version '$Version'. Rebuild matching publishes or pass their shared -Version."
        }
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($arch in $architectures) {
        $stage = Join-Path $temporary $arch
        $source = Join-Path $stage 'src'
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        foreach ($name in @('pyproject.toml', 'setup.py', 'README.md', 'LICENSE')) {
            Copy-Item -LiteralPath (Join-Path $package $name) -Destination $stage
        }
        $setVersion = @'
from pathlib import Path
import re, sys, tomllib
path = Path(sys.argv[1])
lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
project = False
changed = 0
for index, line in enumerate(lines):
    if line.lstrip().startswith("["):
        project = line.strip() == "[project]"
    elif project and re.match(r"^version\s*=", line):
        lines[index] = f'version = "{sys.argv[2]}"\n'
        changed += 1
text = "".join(lines)
if changed != 1 or tomllib.loads(text)["project"]["version"] != sys.argv[2]:
    raise ValueError("Expected one static [project].version in staged pyproject.toml")
path.write_text(text, encoding="utf-8")
'@
        & $Python -I -c $setVersion (Join-Path $stage 'pyproject.toml') $pythonVersion
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to set the Python version in staged package metadata.'
        }
        $packageSource = Join-Path $source 'winappcli'
        New-Item -ItemType Directory -Path $packageSource -Force | Out-Null
        foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $package 'src\winappcli') -Force) {
            if ($entry.Name -in @('_native', '__pycache__')) { continue }
            Copy-Item -LiteralPath $entry.FullName -Destination $packageSource -Recurse
        }
        $native = Join-Path $source 'winappcli\_native'
        New-Item -ItemType Directory -Path $native -Force | Out-Null
        $published = Join-Path $cli "win-$arch"
        foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
            if ($file.Extension -eq '.pdb') { continue }
            $relative = [IO.Path]::GetRelativePath($published, $file.FullName)
            $destination = Join-Path $native $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $native 'LICENSE')
        Write-Host "[PYTHON] Building $($platforms[$arch]) wheel with bundled native CLI..."
        & $Python -m pip wheel --no-deps --no-input --disable-pip-version-check $stage -w $output
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build the $arch Python wheel."
        }
    }
    Write-Host "[PYTHON] Wheels ready: $output"
} finally {
    $env:PATH = $originalPath
    $env:Platform = $originalPlatform
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
