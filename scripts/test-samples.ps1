<#
.SYNOPSIS
Local orchestrator to run sample & guide Pester tests.

.DESCRIPTION
Discovers and runs test.Tests.ps1 for each sample (or a specified subset)
using Invoke-Pester. Each test validates the corresponding guide workflow
from scratch and verifies the existing sample code still builds.

Requires Pester 5.x: Install-Module -Name Pester -Force -MinimumVersion 5.0

.PARAMETER Samples
One or more sample names to test. Defaults to all samples that have a test.Tests.ps1.

.PARAMETER WinappPath
Path to the winapp package passed to each test: an npm .tgz for existing
samples, or a Python platform wheel for python-winui and python-identity. A build artifacts directory
containing both package types can be used when running all samples.

.PARAMETER SkipCleanup
Passed through to each test - keep build artifacts for debugging.

.EXAMPLE
.\scripts\test-samples.ps1
Run all sample & guide tests.

.EXAMPLE
.\scripts\test-samples.ps1 -Samples dotnet-app,rust-app
Run only the dotnet-app and rust-app tests.
#>

[CmdletBinding()]
param(
    [string[]]$Samples,
    [string]$WinappPath,
    [switch]$SkipCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure Pester 5.x is available
$pester = Get-Module -Name Pester -ListAvailable | Where-Object { $_.Version.Major -ge 5 } | Select-Object -First 1
if (-not $pester) {
    Write-Error "Pester 5.x is required. Install with: Install-Module -Name Pester -Force -MinimumVersion 5.0"
    exit 1
}

$samplesRoot = Join-Path $PSScriptRoot "..\samples"

# Discover samples with test.Tests.ps1
$allTests = @(Get-ChildItem -Path $samplesRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "test.Tests.ps1") } |
    Select-Object -ExpandProperty Name)

if ($Samples) {
    # Support comma-separated values (e.g., -Samples "dotnet-app,rust-app")
    $Samples = @($Samples | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($s in $Samples) {
        if ($s -notin $allTests) {
            Write-Warning "Sample '$s' does not have a test.Tests.ps1 - skipping"
        }
    }
    $testList = @($Samples | Where-Object { $_ -in $allTests })
}else {
    $testList = $allTests
}

if (-not $testList) {
    Write-Host "No sample tests to run." -ForegroundColor Yellow
    exit 0
}

# Resolve WinappPath to absolute before passing to containers
if ($WinappPath) {
    if (-not [System.IO.Path]::IsPathRooted($WinappPath)) {
        $WinappPath = (Resolve-Path $WinappPath -ErrorAction Stop).Path
    }
}

# Build Pester containers for each sample
$containers = @()
foreach ($sample in $testList) {
    $testFile = Join-Path (Join-Path $samplesRoot $sample) "test.Tests.ps1"
    $data = @{}
    if ($WinappPath)  { $data['WinappPath']  = $WinappPath }
    if ($SkipCleanup) { $data['SkipCleanup'] = $true }
    $containers += New-PesterContainer -Path $testFile -Data $data
}

# Configure and run Pester
$config = New-PesterConfiguration
$config.Run.Container = $containers
$config.Run.Exit = $true
$config.Output.Verbosity = if ($VerbosePreference -eq 'Continue') { 'Detailed' } else { 'Normal' }

Invoke-Pester -Configuration $config
