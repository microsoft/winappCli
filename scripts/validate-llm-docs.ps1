#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that LLM documentation is up-to-date with CLI schema
.DESCRIPTION
    This script checks if the generated LLM documentation (docs/cli-schema.json and 
    docs/llm-context.md) matches what the CLI would generate. Use this locally before
    committing changes, or in CI to catch drift.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER FailOnDrift
    Exit with error code 1 if documentation is out of sync (default: true)
.EXAMPLE
    .\scripts\validate-llm-docs.ps1
.EXAMPLE
    .\scripts\validate-llm-docs.ps1 -FailOnDrift:$false
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
$ContextPath = Join-Path $ProjectRoot "docs\llm-context.md"

# Verify CLI exists
if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[VALIDATE] Checking LLM documentation..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray

# Check if doc files exist
if (-not (Test-Path $SchemaPath)) {
    Write-Host "::error::docs/cli-schema.json not found. Run 'scripts/generate-llm-docs.ps1' first." -ForegroundColor Red
    if ($FailOnDrift) { exit 1 }
    exit 0
}

if (-not (Test-Path $ContextPath)) {
    Write-Host "::error::docs/llm-context.md not found. Run 'scripts/generate-llm-docs.ps1' first." -ForegroundColor Red
    if ($FailOnDrift) { exit 1 }
    exit 0
}

# Generate fresh schema and compare
Write-Host "[VALIDATE] Generating fresh schema from CLI..." -ForegroundColor Blue
$FreshSchemaLines = & $CliPath --cli-schema
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    exit 1
}

# Join array lines into single string (CLI outputs pretty-printed JSON with newlines)
$FreshSchema = $FreshSchemaLines -join "`n"

$CommittedSchema = Get-Content $SchemaPath -Raw

# Normalize line endings for comparison
$FreshSchemaNormalized = $FreshSchema -replace "`r`n", "`n"
$CommittedSchemaNormalized = $CommittedSchema -replace "`r`n", "`n"

$SchemaDrift = $FreshSchemaNormalized -ne $CommittedSchemaNormalized

if ($SchemaDrift) {
    Write-Host "::error::docs/cli-schema.json is out of sync with CLI!" -ForegroundColor Red
    Write-Host ""
    Write-Host "The committed schema doesn't match what the CLI generates." -ForegroundColor Yellow
    Write-Host "Run 'scripts/generate-llm-docs.ps1' locally and commit the changes." -ForegroundColor Yellow
    Write-Host ""
    
    if ($FailOnDrift) {
        exit 1
    }
} else {
    Write-Host "[VALIDATE] docs/cli-schema.json is up-to-date" -ForegroundColor Green
}

# For llm-context.md, regenerate to a temp location and compare
Write-Host "[VALIDATE] Checking llm-context.md..." -ForegroundColor Blue
$TempDocsPath = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-llm-docs-validate"
if (Test-Path $TempDocsPath) {
    Remove-Item $TempDocsPath -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDocsPath -Force | Out-Null

try {
    # Generate to temp location
    $GenerateScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
    & $GenerateScript -CliPath $CliPath -DocsPath $TempDocsPath | Out-Null
    
    # Compare llm-context.md with line ending normalization
    $FreshContext = Get-Content (Join-Path $TempDocsPath "llm-context.md") -Raw
    $CommittedContext = Get-Content $ContextPath -Raw
    
    $FreshContextNormalized = $FreshContext -replace "`r`n", "`n"
    $CommittedContextNormalized = $CommittedContext -replace "`r`n", "`n"
    
    if ($FreshContextNormalized -ne $CommittedContextNormalized) {
        Write-Host "::error::docs/llm-context.md is out of sync with CLI schema!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Run 'scripts/generate-llm-docs.ps1' locally and commit the changes." -ForegroundColor Yellow
        
        if ($FailOnDrift) {
            exit 1
        }
    } else {
        Write-Host "[VALIDATE] docs/llm-context.md is up-to-date" -ForegroundColor Green
    }
}
finally {
    if (Test-Path $TempDocsPath) {
        Remove-Item $TempDocsPath -Recurse -Force
    }
}

Write-Host "[VALIDATE] LLM documentation is up-to-date!" -ForegroundColor Green
exit 0
