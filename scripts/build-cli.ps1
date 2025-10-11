#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for Windows SDK CLI and npm package
.DESCRIPTION
    This script builds the Windows SDK CLI for both x64 and arm64 architectures,
    creates the npm package, and places all artifacts in an artifacts folder.
    Run this script from the root of the project.
.EXAMPLE
    .\scripts\build-cli.ps1
#>

param(
    [switch]$Clean = $false,
    [switch]$SkipTests = $false
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Set-Location $ProjectRoot

# Define paths
$CliProjectPath = "src\winsdk-CLI\Winsdk.Cli\Winsdk.Cli.csproj"
$NpmProjectPath = "src\winsdk-npm"
$ArtifactsPath = "artifacts"

Write-Host "[*] Starting Windows SDK build process..." -ForegroundColor Green
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Write-Host "[CLEAN] Cleaning artifacts..." -ForegroundColor Yellow
if (Test-Path $ArtifactsPath) {
    Remove-Item $ArtifactsPath -Recurse -Force
}

# Create artifacts directory
Write-Host "[SETUP] Creating artifacts directory..." -ForegroundColor Blue
New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null

# Step 1: Build CLI for x64
Write-Host "[BUILD] Building CLI for x64..." -ForegroundColor Blue
dotnet publish $CliProjectPath -c Release -r win-x64 --self-contained -o "$ArtifactsPath\cli\win-x64"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build CLI for x64"
    exit 1
}

# Step 2: Build CLI for arm64
Write-Host "[BUILD] Building CLI for arm64..." -ForegroundColor Blue
dotnet publish $CliProjectPath -c Release -r win-arm64 --self-contained -o "$ArtifactsPath\cli\win-arm64"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build CLI for arm64"
    exit 1
}

# Step 3: Prepare npm package
Write-Host "[NPM] Preparing npm package..." -ForegroundColor Blue

# Clean npm bin directory first
Push-Location $NpmProjectPath
npm run clean
if ($LASTEXITCODE -ne 0) {
    Write-Warning "npm clean failed, continuing..."
}

# Copy the CLI binaries we just built instead of rebuilding them
Write-Host "[NPM] Copying CLI binaries to npm package..." -ForegroundColor Blue
$NpmBinPath = "bin"
New-Item -ItemType Directory -Path "$NpmBinPath\win-x64" -Force | Out-Null
New-Item -ItemType Directory -Path "$NpmBinPath\win-arm64" -Force | Out-Null

# Copy from our artifacts to npm bin folders
Copy-Item "$ProjectRoot\$ArtifactsPath\cli\win-x64\*" "$NpmBinPath\win-x64\" -Recurse -Force
Copy-Item "$ProjectRoot\$ArtifactsPath\cli\win-arm64\*" "$NpmBinPath\win-arm64\" -Recurse -Force

Pop-Location

# Step 4: Create npm package tarball
Write-Host "[PACK] Creating npm package tarball..." -ForegroundColor Blue
Push-Location $NpmProjectPath
npm pack --pack-destination "..\..\$ArtifactsPath"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create npm package"
    Pop-Location
    exit 1
}
Pop-Location

# Build process complete - all artifacts are ready

# Display results
Write-Host ""
Write-Host "[SUCCESS] Build completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "[INFO] Artifacts created in: $ArtifactsPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Contents:" -ForegroundColor White
Get-ChildItem $ArtifactsPath | ForEach-Object {
    $size = if ($_.PSIsContainer) { "(folder)" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  * $($_.Name) $size" -ForegroundColor Gray
}

Write-Host ""
Write-Host "[DONE] Ready for distribution!" -ForegroundColor Green