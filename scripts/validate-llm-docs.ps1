#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate plugin examples against the built CLI and check plugin manifest versions
.DESCRIPTION
    This script extracts a fresh CLI schema into ignored artifacts and verifies
    that every plugin manifest version (winapp and WinUI; see plugin-version-manifests.ps1)
    matches version.json. Plugin
    skills are hand-authored and are not generated or drift-checked.

    It also runs scripts/validate-plugin-package.ps1, which enforces Agent Plugins
    1.0 conformance for plugins/winapp and checks the skills of every plugin.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER FailOnDrift
    Exit with error code 1 if documentation is out of sync (default: true)
#>

param(
    [string]$CliPath = "",
    [switch]$FailOnDrift = $true
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent
if (-not $CliPath) {
    $CliPath = Join-Path $ProjectRoot "artifacts\cli\win-x64\winapp.exe"
}

$SchemaDirectory = Join-Path $ProjectRoot "artifacts\docs"
$SchemaPath = Join-Path $SchemaDirectory "cli-schema.json"
$BaseVersion = (Get-Content (Join-Path $ProjectRoot "version.json") | ConvertFrom-Json).version
$HasDrift = $false

if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[VALIDATE] Checking CLI schema and plugin manifests..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray

$PluginPackageScript = Join-Path $PSScriptRoot "validate-plugin-package.ps1"

& (Join-Path $PSScriptRoot "generate-llm-docs.ps1") -CliPath $CliPath -OutputPath $SchemaDirectory -CalledFromBuildScript
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to generate the current CLI schema"
    exit 1
}

. (Join-Path $PSScriptRoot "plugin-version-manifests.ps1")
$ManifestPaths = Get-VersionedPluginManifests -ProjectRoot $ProjectRoot

foreach ($manifestPath in $ManifestPaths) {
    if (-not (Test-Path $manifestPath)) {
        Write-Host "::error::required plugin manifest not found: $manifestPath" -ForegroundColor Red
        $HasDrift = $true
        continue
    }

    $manifestText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.UTF8Encoding]::new($false))
    try {
        $null = $manifestText | ConvertFrom-Json -Depth 100
    }
    catch {
        Write-Host "::error::invalid JSON in plugin manifest: $manifestPath" -ForegroundColor Red
        $HasDrift = $true
        continue
    }

    $versions = [regex]::Matches($manifestText, '"version"\s*:\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }
    if (-not $versions -or @($versions | Where-Object { $_ -ne $BaseVersion }).Count -gt 0) {
        Write-Host "::error::plugin manifest versions in $manifestPath must all equal $BaseVersion" -ForegroundColor Red
        $HasDrift = $true
    }
}

if (Test-Path $PluginPackageScript) {
    Write-Host ""
    # Let the child signal failure via its exit code; -FailOnDrift decides whether it is fatal.
    & $PluginPackageScript -CliSchemaPath $SchemaPath
    if ($LASTEXITCODE -ne 0) {
        $HasDrift = $true
    }
}
else {
    Write-Host "::error::required script not found: $PluginPackageScript" -ForegroundColor Red
    $HasDrift = $true
}

if ($HasDrift) {
    Write-Host ""
    Write-Host "Fix the reported plugin errors; run 'scripts/build-cli.ps1' to synchronize manifest versions." -ForegroundColor Yellow
    if ($FailOnDrift) {
        exit 1
    }
}
else {
    Write-Host "[VALIDATE] Plugin examples match the current CLI and manifest versions are up-to-date!" -ForegroundColor Green
}

exit 0
