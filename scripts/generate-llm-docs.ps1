#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate LLM-friendly documentation from CLI schema
.DESCRIPTION
    This script generates docs/cli-schema.json and docs/llm-context.md from the CLI's
    --cli-schema output. Run after building the CLI to keep documentation in sync.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER DocsPath
    Path to the docs folder (default: docs)
.EXAMPLE
    .\scripts\generate-llm-docs.ps1
.EXAMPLE
    .\scripts\generate-llm-docs.ps1 -CliPath ".\bin\Debug\winapp.exe"
#>

param(
    [string]$CliPath = "",
    [string]$DocsPath = ""
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent

if (-not $CliPath) {
    $CliPath = Join-Path $ProjectRoot "artifacts\cli\win-x64\winapp.exe"
}

if (-not $DocsPath) {
    $DocsPath = Join-Path $ProjectRoot "docs"
}

$SchemaOutputPath = Join-Path $DocsPath "cli-schema.json"
$LlmContextPath = Join-Path $DocsPath "llm-context.md"

# Verify CLI exists
if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[DOCS] Generating LLM documentation..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray
Write-Host "Docs path: $DocsPath" -ForegroundColor Gray

# Step 1: Generate CLI schema JSON
Write-Host "[DOCS] Extracting CLI schema..." -ForegroundColor Blue
$SchemaJson = & $CliPath --cli-schema 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    Write-Error $SchemaJson
    exit 1
}

# Save schema JSON
$SchemaJson | Set-Content $SchemaOutputPath -Encoding UTF8 -NoNewline
Write-Host "[DOCS] Saved: $SchemaOutputPath" -ForegroundColor Green

# Parse schema for markdown generation
$Schema = $SchemaJson | ConvertFrom-Json

# Step 2: Generate llm-context.md
Write-Host "[DOCS] Generating llm-context.md..." -ForegroundColor Blue

$LlmContext = @"
# WinApp CLI Context for LLMs

> Auto-generated from CLI v$($Schema.version) (schema version $($Schema.schemaVersion))
> 
> This file provides structured context about the winapp CLI for AI assistants and LLMs.
> For the raw JSON schema, see [cli-schema.json](cli-schema.json).

## Overview

$($Schema.description)

**Installation:**
- WinGet: ``winget install Microsoft.WinAppCli --source winget``
- npm: ``npm install -g @microsoft/winappcli`` (for electron projects)

## Command Reference

"@

# Function to format a command and its subcommands
function Format-Command {
    param(
        [string]$Name,
        [PSObject]$Command,
        [int]$Depth = 0
    )
    
    $indent = "  " * $Depth
    $headingLevel = [Math]::Min($Depth + 3, 6)
    $heading = "#" * $headingLevel
    
    $output = @()
    $output += ""
    $output += "$heading ``winapp $Name``"
    $output += ""
    $output += "$($Command.description)"
    
    # Aliases
    if ($Command.aliases -and $Command.aliases.Count -gt 0) {
        $aliasStr = ($Command.aliases | ForEach-Object { "``$_``" }) -join ", "
        $output += ""
        $output += "**Aliases:** $aliasStr"
    }
    
    # Arguments
    if ($Command.arguments) {
        $output += ""
        $output += "**Arguments:**"
        $sortedArgs = $Command.arguments.PSObject.Properties | Sort-Object { $_.Value.order }
        foreach ($arg in $sortedArgs) {
            $argName = $arg.Name
            $argDetails = $arg.Value
            $required = if ($argDetails.arity.minimum -gt 0) { " *(required)*" } else { "" }
            $default = if ($argDetails.hasDefaultValue -and $null -ne $argDetails.defaultValue) { " (default: ``$($argDetails.defaultValue)``)" } else { "" }
            $output += "- ``<$argName>``$required - $($argDetails.description)$default"
        }
    }
    
    # Options (exclude hidden)
    if ($Command.options) {
        $visibleOptions = $Command.options.PSObject.Properties | Where-Object { -not $_.Value.hidden }
        if ($visibleOptions) {
            $output += ""
            $output += "**Options:**"
            foreach ($opt in $visibleOptions) {
                $optName = $opt.Name
                $optDetails = $opt.Value
                $aliases = if ($optDetails.aliases -and $optDetails.aliases.Count -gt 0) {
                    $filteredAliases = $optDetails.aliases | Where-Object { $_ -ne $optName -and $_ -ne "--$optName" }
                    if ($filteredAliases) { " / ``$($filteredAliases -join '``, ``')``" } else { "" }
                } else { "" }
                $default = if ($optDetails.hasDefaultValue -and $null -ne $optDetails.defaultValue -and $optDetails.defaultValue -ne "") { 
                    " (default: ``$($optDetails.defaultValue)``)" 
                } else { "" }
                $output += "- ``$optName``$aliases - $($optDetails.description)$default"
            }
        }
    }
    
    # Subcommands
    if ($Command.subcommands) {
        foreach ($sub in $Command.subcommands.PSObject.Properties) {
            if (-not $sub.Value.hidden) {
                $subOutput = Format-Command -Name "$Name $($sub.Name)" -Command $sub.Value -Depth ($Depth + 1)
                $output += $subOutput
            }
        }
    }
    
    return $output
}

# Generate command documentation
foreach ($cmd in $Schema.subcommands.PSObject.Properties | Sort-Object Name) {
    if (-not $cmd.Value.hidden) {
        $cmdOutput = Format-Command -Name $cmd.Name -Command $cmd.Value
        $LlmContext += ($cmdOutput -join "`n")
    }
}

# Add footer from separate markdown file (workflows, prerequisites, etc.)
$FooterPath = Join-Path $PSScriptRoot "assets\llm-context-footer.md"
if (Test-Path $FooterPath) {
    $Footer = Get-Content $FooterPath -Raw
    $LlmContext += "`n`n" + $Footer
} else {
    Write-Warning "Footer file not found: $FooterPath"
}

# Save llm-context.md
$LlmContext | Set-Content $LlmContextPath -Encoding UTF8
Write-Host "[DOCS] Saved: $LlmContextPath" -ForegroundColor Green

Write-Host "[DOCS] LLM documentation generated successfully!" -ForegroundColor Green
