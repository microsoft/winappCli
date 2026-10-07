#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate the CLI schema from the CLI binary
.DESCRIPTION
    This script writes artifacts/docs/cli-schema.json from the CLI's --cli-schema output.
    Plugin skills are hand-authored directly under plugins/winapp/skills and are
    not generated.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER OutputPath
    Schema output folder (default: artifacts/docs). An explicit path skips plugin version synchronization.
.EXAMPLE
    .\scripts\generate-llm-docs.ps1
.EXAMPLE
    .\scripts\generate-llm-docs.ps1 -CliPath ".\bin\Debug\winapp.exe"
#>

param(
    [string]$CliPath = "",
    [string]$OutputPath = "",
    [switch]$CalledFromBuildScript = $false
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent
$DefaultOutputPath = Join-Path $ProjectRoot "artifacts\docs"
$UsingDefaultPaths = (-not $CliPath -and -not $OutputPath)
$SyncPluginVersions = (-not $OutputPath)

if (-not $CliPath) {
    $CliPath = Join-Path $ProjectRoot "artifacts\cli\win-x64\winapp.exe"
}

if (-not $OutputPath) {
    $OutputPath = $DefaultOutputPath
}

if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$SchemaOutputPath = Join-Path $OutputPath "cli-schema.json"

Write-Host "[DOCS] Generating CLI schema..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray
Write-Host "Output path: $OutputPath" -ForegroundColor Gray

$prevEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $SchemaJsonLines = & $CliPath --cli-schema
}
finally {
    [Console]::OutputEncoding = $prevEncoding
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    exit 1
}

$SchemaJson = ($SchemaJsonLines -join "`n").TrimEnd() + "`n"
try {
    $schema = $SchemaJson | ConvertFrom-Json -Depth 100 -ErrorAction Stop
    if ($schema.name -ne 'winapp' -or $schema.subcommands -isnot [System.Management.Automation.PSCustomObject] -or
            @($schema.subcommands.PSObject.Properties).Count -eq 0) {
        throw "Expected the winapp command tree with at least one subcommand."
    }
}
catch {
    Write-Error "CLI returned invalid schema JSON: $($_.Exception.Message)"
    exit 1
}

[System.IO.File]::WriteAllText($SchemaOutputPath, $SchemaJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "[DOCS] Saved: $SchemaOutputPath" -ForegroundColor Green

# Validation uses an explicit output path so it does not update tracked manifests.
if ($SyncPluginVersions) {
    $PluginVersion = (Get-Content (Join-Path $ProjectRoot "version.json") | ConvertFrom-Json).version
    $ManifestPaths = @(
        (Join-Path $ProjectRoot "plugin.json"),
        (Join-Path $ProjectRoot "plugins\winapp\plugin.json"),
        (Join-Path $ProjectRoot "plugins\winapp\.claude-plugin\plugin.json"),
        (Join-Path $ProjectRoot ".github\plugin\marketplace.json"),
        (Join-Path $ProjectRoot ".claude-plugin\marketplace.json")
    )

    foreach ($manifestPath in $ManifestPaths) {
        if (-not (Test-Path $manifestPath)) {
            Write-Error "Required plugin manifest not found: $manifestPath"
            exit 1
        }

        $manifestText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.UTF8Encoding]::new($false))
        $manifestText = [regex]::Replace($manifestText, '("version"\s*:\s*")[^"]*(")', "`${1}$PluginVersion`${2}")
        $manifestText = ($manifestText -replace "`r`n", "`n").TrimEnd() + "`n"
        [System.IO.File]::WriteAllText($manifestPath, $manifestText, [System.Text.UTF8Encoding]::new($false))
    }

    Write-Host "[DOCS] Synced plugin manifest versions to $PluginVersion" -ForegroundColor Gray
}

Write-Host "[DOCS] Documentation generated successfully!" -ForegroundColor Green

if (-not $CalledFromBuildScript -and $UsingDefaultPaths) {
    Write-Host ""
    Write-Host "[DOCS] Warning: Running generate-llm-docs.ps1 directly may use stale CLI binaries." -ForegroundColor Yellow
    Write-Host "[DOCS] Run 'scripts/build-cli.ps1' to rebuild the CLI before regenerating docs." -ForegroundColor Yellow
}
