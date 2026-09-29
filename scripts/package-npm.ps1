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

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevToolsEngine.psm1') -Force

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
    
    Assert-DevToolsArchiveSupport -Format Npm
    Assert-DevToolsEnginePayload -Directory $X64Path
    Assert-DevToolsEnginePayload -Directory $Arm64Path
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
            # Determine prerelease label based on current branch
            $PrereleaseLabel = & "$PSScriptRoot\get-prerelease-label.ps1"
            # Prerelease build: add prerelease label suffix (e.g., "0.1.0-prerelease.73" or "0.1.0-dev-my-feature.73")
            $Version = "$BaseVersion-$PrereleaseLabel.$BuildNumber"
            Write-Host "[VERSION] Using prerelease version (with $PrereleaseLabel suffix)" -ForegroundColor Cyan
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
    
    Push-Location $NpmProjectPath
    $PackageJsonPath = Join-Path $NpmProjectPath 'package.json'
    $OriginalPackageBytes = $null
    $PackagingFailure = $null
    try {
        $OriginalPackageBytes = [IO.File]::ReadAllBytes($PackageJsonPath)
        npm run clean
        if ($LASTEXITCODE -ne 0) { throw 'npm clean failed; refusing to package stale binaries.' }

        # Stage first: command generation prefers npm bin over repository artifacts.
        foreach ($arch in 'x64', 'arm64') {
            $destination = Join-Path $NpmProjectPath "bin\win-$arch"
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item "$CliBinariesPath\win-$arch\*" $destination -Recurse -Force
        }

        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm run generate-commands
        if ($LASTEXITCODE -ne 0) { throw 'Command code generation failed' }
        npm run format:check
        if ($LASTEXITCODE -ne 0) { throw "Format check failed - run 'npm run format' to fix" }
        npm run lint
        if ($LASTEXITCODE -ne 0) { throw 'Lint failed' }
        npm run compile
        if ($LASTEXITCODE -ne 0) { throw 'TypeScript compilation failed' }

        $PackageJson = Get-Content $PackageJsonPath -Raw | ConvertFrom-Json
        $PackageJson.version = $Version
        $PackageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonPath

        $RelativeOutputPath = [IO.Path]::GetRelativePath($NpmProjectPath, $OutputPath)
        $PackOutput = @(npm pack --json --pack-destination $RelativeOutputPath)
        if ($LASTEXITCODE -ne 0) { throw 'Failed to create npm package' }
        $PackRows = @(($PackOutput -join "`n") | ConvertFrom-Json)
        $TarballName = if ($PackRows.Count -eq 1) { [string]$PackRows[0].filename } else { '' }
        if (-not $TarballName.EndsWith('.tgz', [StringComparison]::OrdinalIgnoreCase) -or
            $TarballName.IndexOfAny([char[]]('/','\')) -ge 0 -or
            [IO.Path]::IsPathRooted($TarballName)) {
            throw 'npm pack did not return exactly one local .tgz filename.'
        }
        $TarballPath = Join-Path $OutputPath $TarballName
        Assert-DevToolsEngineArchive -ArchivePath $TarballPath -Format Npm -CliBinariesPath $CliBinariesPath
        $CreatedTarball = Get-Item -LiteralPath $TarballPath
        $TarballSize = [math]::Round($CreatedTarball.Length / 1MB, 2)
    } catch {
        $PackagingFailure = $_
        throw
    } finally {
        try {
            if ($null -ne $OriginalPackageBytes) {
                [IO.File]::WriteAllBytes($PackageJsonPath, $OriginalPackageBytes)
            }
        } catch {
            if ($null -eq $PackagingFailure) { throw }
            Write-Error "Also failed to restore package.json: $_" -ErrorAction Continue
        } finally {
            Pop-Location
        }
    }

    Write-Host "[SUCCESS] npm package: $($CreatedTarball.Name) ($TarballSize MB)" -ForegroundColor Green
    Write-Host "[DONE] npm packaging complete!" -ForegroundColor Green
}
finally
{
    # Restore original working directory
    Pop-Location
}