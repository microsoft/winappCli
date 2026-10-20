# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

<#
.SYNOPSIS
Build a local WinAppCLI accessibility bundle with its managed runtime and skills.
.DESCRIPTION
Uses an already-published native CLI. The accessibility worker is published
self-contained unless WorkerDirectory points to an existing worker publish.
The output directory and ZIP must not already exist.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$CliDirectory,
    [string]$WorkerDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
if (-not $CliDirectory) { $CliDirectory = Join-Path $repo "artifacts\cli\$Runtime" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts\accessloop-local\$Runtime" }
$cli = [IO.Path]::GetFullPath($CliDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$zip = "$output.zip"
if (-not (Test-Path -LiteralPath (Join-Path $cli 'winapp.exe') -PathType Leaf)) {
    throw "Publish the native CLI first. Missing: $cli\winapp.exe"
}
if (Test-Path -LiteralPath $output) { throw "Choose a new output directory: $output already exists." }
if (Test-Path -LiteralPath $zip) { throw "Choose a new output directory: $zip already exists." }
if ($output.StartsWith($cli.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The bundle cannot be created inside the CLI source directory.'
}
if ($WorkerDirectory) {
    $WorkerDirectory = [IO.Path]::GetFullPath($WorkerDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $WorkerDirectory 'winapp-accessibility-worker.exe') -PathType Leaf)) {
        throw 'WorkerDirectory must contain a self-contained accessibility worker publish.'
    }
    if ($output.StartsWith($WorkerDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The bundle cannot be created inside the worker source directory.'
    }
}

New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath $cli -Force | Where-Object { $_.Extension -ne '.pdb' } |
    Copy-Item -Destination $output -Recurse
$worker = Join-Path $output 'accessibility'
if ($WorkerDirectory) {
    New-Item -ItemType Directory -Path $worker | Out-Null
    Get-ChildItem -LiteralPath $WorkerDirectory -Force | Copy-Item -Destination $worker -Recurse
}
else {
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\winapp-CLI\WinApp.Accessibility.Worker') -Filter '*.csproj' -File)
    if ($projects.Count -ne 1) { throw 'Expected one accessibility worker project.' }
    & dotnet publish $projects[0].FullName -c Release -r $Runtime --self-contained true `
        -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -o $worker -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Accessibility worker publish failed with exit code $LASTEXITCODE." }
}
foreach ($name in @('winapp-accessibility-worker.exe', 'winapp-accessibility-worker.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $worker $name) -PathType Leaf)) {
        throw "The self-contained worker is missing $name."
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $worker 'winapp-accessibility-worker.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
    throw 'The worker still requires an externally installed .NET runtime.'
}

function Copy-SourceTree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source links are not included in a local demo bundle: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('bin', 'obj', '.git')) {
                Copy-SourceTree $item.FullName (Join-Path $Destination $item.Name)
            }
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $Destination $item.Name)
        }
    }
}
$examples = Join-Path $output 'examples'
New-Item -ItemType Directory -Path $examples | Out-Null
Copy-SourceTree (Join-Path $repo 'samples\accessloop\StateDemo') (Join-Path $examples 'StateDemo')
Copy-Item -LiteralPath (Join-Path $repo 'samples\accessloop\loop-demo.json') -Destination $examples
Copy-SourceTree (Join-Path $repo 'plugins\winapp') (Join-Path $output 'plugin')
$projectSkills = Join-Path $output '.github\skills'
New-Item -ItemType Directory -Path $projectSkills -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'plugins\winapp\skills\winapp-accessloop') -Destination $projectSkills -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'samples\accessloop\local-bundle.md') -Destination (Join-Path $output 'README.md')

$native = Join-Path $output 'winapp.exe'
& $native ui audit --help
if ($LASTEXITCODE -ne 0) { throw 'The native CLI does not expose ui audit.' }
& (Join-Path $worker 'winapp-accessibility-worker.exe') --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The bundled accessibility worker did not start.' }
$inventory = @(Get-ChildItem -LiteralPath $output -Recurse -File -Force | ForEach-Object {
    [pscustomobject]@{
        path=[IO.Path]::GetRelativePath($output, $_.FullName)
        bytes=$_.Length
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
[pscustomobject]@{
    schemaVersion=1; runtime=$Runtime; createdAtUtc=[DateTime]::UtcNow.ToString('O')
    nativeCli='winapp.exe'; accessibilityWorker='accessibility\winapp-accessibility-worker.exe'
    distribution='local-demo'; nativeAotCli=$true; selfContainedManagedWorker=$true
    capabilities='ui audit with automatic standard-tab navigation, --current-page audits, configured workflows, recorded replay, and supported source repair. No arbitrary-control traversal or arbitrary-app autonomous planning.'
    files=$inventory
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'bundle.json') -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($output, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
[pscustomobject]@{ directory=$output; zip=$zip; zipBytes=(Get-Item -LiteralPath $zip).Length; runtime=$Runtime } | ConvertTo-Json
