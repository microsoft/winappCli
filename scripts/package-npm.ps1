#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Package Windows App Development CLI as npm package
.DESCRIPTION
    This script creates an npm package tarball from pre-built CLI binaries for x64 and arm64 architectures.
    Uses artifacts/cli for binaries and outputs to artifacts directory.
.PARAMETER Version
    Version number for the npm package (e.g., "0.1.0" or "0.1.0-prerelease.73").
    If not specified, reads from version.json and calculates based on Stable flag.
.PARAMETER Stable
    Use stable build configuration (default: false, uses prerelease config)
.EXAMPLE
    .\scripts\package-npm.ps1
    .\scripts\package-npm.ps1 -Version "0.1.0" -Stable
    .\scripts\package-npm.ps1 -Version "0.1.0-prerelease.73"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Version,

    [Parameter(Mandatory=$false)]
    [switch]$Stable = $false
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Push-Location $ProjectRoot
try
{
    # Define standard paths
    $CliBinariesPath = Join-Path $ProjectRoot "artifacts\cli"
    $OutputPath = Join-Path $ProjectRoot "artifacts"
    
    Write-Host "[NPM] Starting npm package creation..." -ForegroundColor Green
    Write-Host "[INFO] Project root: $ProjectRoot" -ForegroundColor Gray
    Write-Host "[INFO] CLI binaries path: $CliBinariesPath" -ForegroundColor Gray
    Write-Host "[INFO] Output path: $OutputPath" -ForegroundColor Gray
    
    # Validate that the CLI binaries path exists
    if (-not (Test-Path $CliBinariesPath)) {
        Write-Error "CLI binaries path does not exist: $CliBinariesPath"
        exit 1
    }
    
    # Validate that required architecture folders exist
    $X64Path = Join-Path $CliBinariesPath "win-x64"
    $Arm64Path = Join-Path $CliBinariesPath "win-arm64"
    
    if (-not (Test-Path $X64Path)) {
        Write-Error "win-x64 folder not found at: $X64Path"
        exit 1
    }
    
    if (-not (Test-Path $Arm64Path)) {
        Write-Error "win-arm64 folder not found at: $Arm64Path"
        exit 1
    }
    
    Write-Host "[VALIDATE] Found CLI binaries:" -ForegroundColor Green
    Write-Host "  - x64: $X64Path" -ForegroundColor Gray
    Write-Host "  - arm64: $Arm64Path" -ForegroundColor Gray
    
    # Validate that the main executable exists in both folders
    $X64Exe = Join-Path $X64Path "winapp.exe"
    $Arm64Exe = Join-Path $Arm64Path "winapp.exe"
    
    if (-not (Test-Path $X64Exe)) {
        Write-Error "winapp.exe not found in x64 folder: $X64Exe"
        exit 1
    }
    
    if (-not (Test-Path $Arm64Exe)) {
        Write-Error "winapp.exe not found in arm64 folder: $Arm64Exe"
        exit 1
    }
    
    Write-Host "[VALIDATE] All required files found!" -ForegroundColor Green
    
    # Calculate version if not provided
    if ([string]::IsNullOrEmpty($Version)) {
        Write-Host "[VERSION] Calculating package version..." -ForegroundColor Blue

        # Read base version from version.json
        $VersionJsonPath = "$ProjectRoot\version.json"
        if (-not (Test-Path $VersionJsonPath)) {
            Write-Error "version.json not found at $VersionJsonPath"
            exit 1
        }

        $VersionJson = Get-Content $VersionJsonPath | ConvertFrom-Json
        $BaseVersion = $VersionJson.version

        # Get build number
        $GetBuildNumberScript = Join-Path $PSScriptRoot "get-build-number.ps1"
        $BuildNumber = & $GetBuildNumberScript
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to get build number"
            exit 1
        }

        # Construct full version based on Stable flag
        if ($Stable) {
            # Stable build: use semantic version without prerelease suffix (e.g., "0.1.0")
            $Version = $BaseVersion
            Write-Host "[VERSION] Using stable version (no prerelease suffix)" -ForegroundColor Cyan
        } else {
            # Prerelease build: add prerelease number suffix (e.g., "0.1.0-prerelease.73")
            $Version = "$BaseVersion-prerelease.$BuildNumber"
            Write-Host "[VERSION] Using prerelease version (with prerelease suffix)" -ForegroundColor Cyan
        }
    }
    
    Write-Host "[VERSION] Package version: $Version" -ForegroundColor Cyan
    
    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
        Write-Host "[SETUP] Created output directory: $OutputPath" -ForegroundColor Blue
    }
    
    # Navigate to npm project directory
    $NpmProjectPath = Join-Path $ProjectRoot "src\winapp-npm"
    if (-not (Test-Path $NpmProjectPath)) {
        Write-Error "npm project path does not exist: $NpmProjectPath"
        exit 1
    }
    
    Write-Host "[NPM] Preparing npm package..." -ForegroundColor Blue
    
    # Clean npm bin directory first
    Push-Location $NpmProjectPath
    npm run clean
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "npm clean failed, continuing..."
    }

    # Install dependencies and compile TypeScript
    Write-Host "[NPM] Installing dependencies..." -ForegroundColor Blue
    npm ci
    if ($LASTEXITCODE -ne 0) {
        Write-Error "npm ci failed"
        Pop-Location
        exit 1
    }

    Write-Host "[NPM] Running format check, lint, and compile..." -ForegroundColor Blue
    npm run format:check
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Format check failed - run 'npm run format' to fix"
        Pop-Location
        exit 1
    }

    npm run lint
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Lint failed"
        Pop-Location
        exit 1
    }

    npm run compile
    if ($LASTEXITCODE -ne 0) {
        Write-Error "TypeScript compilation failed"
        Pop-Location
        exit 1
    }
    
    # Backup original package.json
    Write-Host "[NPM] Setting package version to $Version..." -ForegroundColor Blue
    $PackageJsonPath = "package.json"
    Copy-Item $PackageJsonPath "$PackageJsonPath.backup" -Force
    
    # Update package.json version temporarily
    $PackageJson = Get-Content $PackageJsonPath | ConvertFrom-Json
    $PackageJson.version = $Version
    $PackageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonPath
    
    # Copy the CLI binaries to npm package
    Write-Host "[NPM] Copying CLI binaries to npm package..." -ForegroundColor Blue
    $NpmBinPath = "bin"
    New-Item -ItemType Directory -Path "$NpmBinPath\win-x64" -Force | Out-Null
    New-Item -ItemType Directory -Path "$NpmBinPath\win-arm64" -Force | Out-Null
    
    # Copy from CLI binaries to npm bin folders
    Copy-Item "$CliBinariesPath\win-x64\*" "$NpmBinPath\win-x64\" -Recurse -Force
    Copy-Item "$CliBinariesPath\win-arm64\*" "$NpmBinPath\win-arm64\" -Recurse -Force
    
    # Create npm package tarball
    Write-Host "[PACK] Creating npm package tarball..." -ForegroundColor Blue
    
    # Calculate relative path from npm project to output directory
    $RelativeOutputPath = [System.IO.Path]::GetRelativePath($NpmProjectPath, $OutputPath)
    
    npm pack --pack-destination $RelativeOutputPath
    $PackResult = $LASTEXITCODE
    
    # Restore original package.json
    Write-Host "[NPM] Restoring original package.json..." -ForegroundColor Blue
    if (Test-Path "$PackageJsonPath.backup") {
        Move-Item "$PackageJsonPath.backup" $PackageJsonPath -Force
    }
    
    Pop-Location
    
    if ($PackResult -ne 0) {
        Write-Error "Failed to create npm package"
        exit 1
    }
    
    # Find the created tarball and report success
    # Get the latest .tgz file in the output directory
    $CreatedTarball = Get-ChildItem -Path $OutputPath -Filter "*.tgz" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    
    if ($CreatedTarball) {
        $TarballSize = [math]::Round($CreatedTarball.Length / 1MB, 2)
        Write-Host ""
        Write-Host "[SUCCESS] npm package created successfully!" -ForegroundColor Green
        Write-Host "[INFO] Package: $($CreatedTarball.Name) ($TarballSize MB)" -ForegroundColor Cyan
        Write-Host "[INFO] Location: $($CreatedTarball.FullName)" -ForegroundColor Cyan
    } else {
        Write-Warning "npm package was created but could not be located in $OutputPath"
    }
    
    Write-Host "[DONE] npm packaging complete!" -ForegroundColor Green
}
finally
{
    # Restore original working directory
    Pop-Location
}