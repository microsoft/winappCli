#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that the generated CLI schema and plugin manifest versions are current
.DESCRIPTION
    This script compares docs/cli-schema.json with the CLI's --cli-schema output
    and verifies that every plugin manifest version matches version.json. Plugin
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

$SchemaPath = Join-Path $ProjectRoot "docs\cli-schema.json"
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

if (-not (Test-Path $SchemaPath)) {
    Write-Host "::error::docs/cli-schema.json not found. Run 'scripts/build-cli.ps1' to regenerate it." -ForegroundColor Red
    $HasDrift = $true
}
else {
    $prevEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $FreshSchemaLines = & $CliPath --cli-schema
    }
    finally {
        [Console]::OutputEncoding = $prevEncoding
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to extract CLI schema"
        exit 1
    }

    $FreshSchema = (($FreshSchemaLines -join "`n") -replace "`r`n", "`n")
    $CommittedSchema = ([System.IO.File]::ReadAllText($SchemaPath, [System.Text.UTF8Encoding]::new($false))) -replace "`r`n", "`n"

    try {
        $FreshObj = $FreshSchema | ConvertFrom-Json -Depth 100
    }
    catch {
        Write-Error "CLI returned invalid schema JSON: $($_.Exception.Message)"
        exit 1
    }

    $CommittedObj = $null
    try {
        $CommittedObj = $CommittedSchema | ConvertFrom-Json -Depth 100
    }
    catch {
        Write-Host "::error::docs/cli-schema.json contains invalid JSON: $($_.Exception.Message)" -ForegroundColor Red
        $HasDrift = $true
    }

    if ($CommittedObj) {
        $FreshObj.version = $BaseVersion
        $FreshNormalized = $FreshObj | ConvertTo-Json -Depth 100 -Compress
        $CommittedNormalized = $CommittedObj | ConvertTo-Json -Depth 100 -Compress

        if ($FreshNormalized -ne $CommittedNormalized) {
            Write-Host "::error::docs/cli-schema.json is out of sync with CLI!" -ForegroundColor Red
            $HasDrift = $true
        }
        else {
            Write-Host "[VALIDATE] docs/cli-schema.json is up-to-date" -ForegroundColor Green
        }
    }
}

$ManifestPaths = @(
    (Join-Path $ProjectRoot "plugin.json"),
    (Join-Path $ProjectRoot "plugins\winapp\plugin.json"),
    (Join-Path $ProjectRoot "plugins\winapp\.claude-plugin\plugin.json"),
    (Join-Path $ProjectRoot ".github\plugin\marketplace.json"),
    (Join-Path $ProjectRoot ".claude-plugin\marketplace.json")
)

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
    & $PluginPackageScript
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
    Write-Host "Run 'scripts/build-cli.ps1' locally, then commit the regenerated schema and manifests." -ForegroundColor Yellow
    if ($FailOnDrift) {
        exit 1
    }
}
else {
    Write-Host "[VALIDATE] CLI schema and plugin manifests are up-to-date!" -ForegroundColor Green
}

exit 0
