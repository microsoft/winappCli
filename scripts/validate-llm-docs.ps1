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
$FreshSchema = & $CliPath --cli-schema 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    Write-Error $FreshSchema
    exit 1
}

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

# For llm-context.md, we regenerate and compare
# Create temp file for comparison
$TempContextPath = [System.IO.Path]::GetTempFileName()
try {
    # Run the generate script to a temp location
    $GenerateScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
    $TempDocsPath = [System.IO.Path]::GetTempPath()
    
    # We need to generate to temp and compare
    # For simplicity, just check if git shows changes after regeneration
    Push-Location $ProjectRoot
    try {
        # Regenerate docs
        & $GenerateScript -CliPath $CliPath | Out-Null
        
        # Check git status for the doc files
        $ChangedFiles = git diff --name-only HEAD -- "docs/cli-schema.json" "docs/llm-context.md" 2>$null
        
        if ($ChangedFiles) {
            Write-Host "::error::LLM documentation is out of sync with CLI schema!" -ForegroundColor Red
            Write-Host "Changed files:" -ForegroundColor Yellow
            $ChangedFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
            Write-Host ""
            Write-Host "Run 'scripts/generate-llm-docs.ps1' locally and commit the changes." -ForegroundColor Yellow
            
            # Restore the original files
            git checkout -- "docs/cli-schema.json" "docs/llm-context.md" 2>$null
            
            if ($FailOnDrift) {
                exit 1
            }
        } else {
            Write-Host "[VALIDATE] docs/llm-context.md is up-to-date" -ForegroundColor Green
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path $TempContextPath) {
        Remove-Item $TempContextPath -Force
    }
}

Write-Host "[VALIDATE] LLM documentation is up-to-date!" -ForegroundColor Green
exit 0
