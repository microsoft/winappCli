<#
.SYNOPSIS
End-to-end test for WinApp CLI with Electron framework.

.DESCRIPTION
This script tests the complete WinApp CLI workflow with Electron:
1. Creates a new Electron application
2. Installs the locally-built winapp npm package
3. Runs 'winapp init' with non-interactive mode
4. Creates C++ and C# native addons
5. Builds the addons to validate they compile
6. Adds Electron debug identity
7. Packages the app to MSIX
8. Signs the MSIX package

The test creates a 'test-wd' directory in the repo root for the test project and cleans it up after completion.

.PARAMETER ArtifactsPath
Path to the artifacts folder containing the built winapp npm package.
Default: "$PSScriptRoot\..\artifacts\npm"

.PARAMETER NpmPackagePath
Path to the winapp npm package. If not specified, uses the one from ArtifactsPath.
Default: "$PSScriptRoot\..\src\winapp-npm"

.PARAMETER SkipCleanup
If specified, does not delete the test project after completion (useful for debugging).

.PARAMETER Verbose
Enable verbose output for debugging.

.EXAMPLE
.\test-e2e-electron.ps1
Run the test with default settings.

.EXAMPLE
.\test-e2e-electron.ps1 -SkipCleanup -Verbose
Run the test, keep the project folder, and show detailed output.
#>

param(
    [string]$ArtifactsPath = "$PSScriptRoot\..\artifacts\npm",
    [string]$NpmPackagePath = "$PSScriptRoot\..\src\winapp-npm",
    [switch]$SkipCleanup,
    [switch]$Verbose
)

# Enable strict mode
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VerbosePreference = if ($Verbose) { 'Continue' } else { 'SilentlyContinue' }

# ============================================================================
# Helper Functions
# ============================================================================

function Write-TestHeader {
    param([string]$Message)
    Write-Host "`n$('='*80)" -ForegroundColor Cyan
    Write-Host "TEST: $Message" -ForegroundColor Cyan
    Write-Host "$('='*80)`n" -ForegroundColor Cyan
}

function Write-TestStep {
    param([string]$Message, [int]$Step)
    Write-Host "[$Step] $Message" -ForegroundColor Yellow
}

function Write-TestSuccess {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-TestError {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Assert-Command {
    param(
        [string]$Command,
        [string]$FailMessage
    )
    Write-Verbose "Running: $Command"
    $result = Invoke-Expression $Command
    if ($LASTEXITCODE -ne 0) {
        Write-TestError $FailMessage
        throw $FailMessage
    }
    Write-TestSuccess "$Command"
    return $result
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Description
    )
    if (-not (Test-Path $Path)) {
        Write-TestError "$Description not found at $Path"
        throw "$Description not found at $Path"
    }
    Write-TestSuccess "$Description exists: $Path"
}

function Assert-DirectoryExists {
    param(
        [string]$Path,
        [string]$Description
    )
    if (-not (Test-Path $Path -PathType Container)) {
        Write-TestError "$Description not found at $Path"
        throw "$Description not found at $Path"
    }
    Write-TestSuccess "$Description exists: $Path"
}

# ============================================================================
# Validation
# ============================================================================

Write-TestHeader "E2E Electron Test - Validation Phase"

Write-TestStep "Validating prerequisites..." 1

# Check Node.js
try {
    $nodeVersion = node --version
    Write-TestSuccess "Node.js found: $nodeVersion"
} catch {
    Write-TestError "Node.js is not installed or not in PATH"
    throw "Node.js is required but not found"
}

# Check npm
try {
    $npmVersion = npm --version
    Write-TestSuccess "npm found: $npmVersion"
} catch {
    Write-TestError "npm is not installed or not in PATH"
    throw "npm is required but not found"
}



# Verify artifacts path or npm package path
if ($ArtifactsPath -and (Test-Path $ArtifactsPath)) {
    # Convert to absolute path to ensure it works after directory changes
    $resolvedArtifactsPath = (Resolve-Path $ArtifactsPath).Path
    
    # Check if this is a directory containing .tgz files (from CI artifact download)
    # or a directory with package.json (local npm package)
    $tgzFiles = Get-ChildItem -Path $resolvedArtifactsPath -Filter "*.tgz" -ErrorAction SilentlyContinue
    if ($tgzFiles) {
        # Use the first .tgz file found
        $localNpmPackagePath = $tgzFiles[0].FullName
        Write-TestSuccess "Found npm tarball: $localNpmPackagePath"
    } elseif (Test-Path (Join-Path $resolvedArtifactsPath "package.json")) {
        # It's a directory with package.json (local development)
        $localNpmPackagePath = $resolvedArtifactsPath
        Write-TestSuccess "Found npm package directory: $localNpmPackagePath"
    } else {
        Write-TestError "Artifacts path exists but contains no .tgz files or package.json: $resolvedArtifactsPath"
        throw "Invalid artifacts path - no installable npm package found"
    }
} elseif (Test-Path $NpmPackagePath) {
    Write-TestSuccess "npm package found: $NpmPackagePath"
    # Convert to absolute path to ensure it works after directory changes
    $localNpmPackagePath = (Resolve-Path $NpmPackagePath).Path
} else {
    Write-TestError "Neither artifacts path nor npm package path exists"
    throw "Cannot find winapp npm package at $ArtifactsPath or $NpmPackagePath"
}

Write-Verbose "Using npm package path: $localNpmPackagePath"

# ============================================================================
# Setup Test Environment
# ============================================================================

Write-TestHeader "E2E Electron Test - Setup Phase"

Write-TestStep "Creating test directory..." 2

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$testDir = Join-Path $repoRoot "test-wd"

# Clean up any existing test directory
if (Test-Path $testDir) {
    Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
}

$null = New-Item -ItemType Directory -Path $testDir -Force
Write-TestSuccess "Test directory created: $testDir"

# Save original location to restore on exit
$originalLocation = Get-Location

try {
    Push-Location $testDir
    Write-Verbose "Working directory: $(Get-Location)"

    # ========================================================================
    # Configure npm for CI environment
    # ========================================================================

    # Set a unique npm cache directory to avoid ECOMPROMISED errors in CI
    # This prevents conflicts with concurrent builds and stale cache issues
    $npmCacheDir = Join-Path $testDir ".npm-cache"
    $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
    $env:npm_config_cache = $npmCacheDir
    Write-Verbose "npm cache directory set to: $npmCacheDir"

    # ========================================================================
    # Create Electron Application
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Create Electron App Phase"

    Write-TestStep "Creating new Electron app..." 3

    # Clear npm cache to avoid ECOMPROMISED errors in CI environments
    Write-Verbose "Clearing npm cache to avoid lock file issues..."
    npm cache clean --force 2>$null

    # Use Electron Forge to scaffold basic app (no webpack)
    # Retry logic for CI environments where npm can have transient failures
    $maxRetries = 3
    $retryCount = 0
    $electronAppCreated = $false
    
    while (-not $electronAppCreated -and $retryCount -lt $maxRetries) {
        $retryCount++
        Write-Verbose "Attempt $retryCount of $maxRetries to create Electron app..."
        
        try {
            # Use --prefer-offline to reduce network issues and clear package-lock before retry
            if ($retryCount -gt 1) {
                Write-Verbose "Cleaning up failed attempt..."
                Remove-Item -Path (Join-Path $testDir "electron-app") -Recurse -Force -ErrorAction SilentlyContinue
                npm cache clean --force 2>$null
                Start-Sleep -Seconds 2
            }
            
            $electronCommand = "npx -y create-electron-app@latest electron-app --template=webpack"
            Write-Verbose "Running: $electronCommand"
            Invoke-Expression $electronCommand
            
            if ($LASTEXITCODE -eq 0) {
                $electronAppCreated = $true
                Write-TestSuccess "Electron app created successfully"
            } else {
                Write-Verbose "npx command failed with exit code $LASTEXITCODE"
            }
        } catch {
            Write-Verbose "Exception during Electron app creation: $_"
        }
    }
    
    if (-not $electronAppCreated) {
        Write-TestError "Failed to create Electron app after $maxRetries attempts"
        throw "Failed to create Electron app"
    }

    $electronAppDir = Join-Path $testDir "electron-app"
    Assert-DirectoryExists $electronAppDir "Electron app directory"

    Push-Location $electronAppDir

    # Update package.json to add required fields for MSIX
    Write-TestStep "Configuring package.json for Windows packaging..." 4

    $packageJsonPath = Join-Path $electronAppDir "package.json"
    $packageJson = Get-Content $packageJsonPath | ConvertFrom-Json

    # Add required fields for MSIX packaging
    $packageJson | Add-Member -MemberType NoteProperty -Name "displayName" -Value "WinApp Electron Test" -Force
    $packageJson | Add-Member -MemberType NoteProperty -Name "description" -Value "E2E test application for WinApp CLI" -Force

    # Ensure version is set
    if ([string]::IsNullOrEmpty($packageJson.version)) {
        $packageJson.version = "1.0.0"
    }

    $packageJson | ConvertTo-Json | Set-Content $packageJsonPath
    Write-TestSuccess "package.json configured"

    # ========================================================================
    # Install WinApp npm package
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Install WinApp Phase"

    Write-TestStep "Installing winapp npm package from local artifacts..." 5

    # Install the local winapp package
    $installCommand = "npm install $localNpmPackagePath --save-dev"
    Assert-Command $installCommand "Failed to install winapp npm package"

    # Verify winapp is installed
    $nodeModulesPath = Join-Path $electronAppDir "node_modules" ".bin" "winapp"
    if ($PSVersionTable.Platform -eq 'Win32NT' -or $PSVersionTable.OS -like 'Windows*') {
        $winappCli = Join-Path $electronAppDir "node_modules" ".bin" "winapp.cmd"
        Assert-FileExists $winappCli "winapp CLI"
    } else {
        Assert-FileExists $nodeModulesPath "winapp CLI"
    }

    # ========================================================================
    # Initialize WinApp Workspace
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Initialize Workspace Phase"

    Write-TestStep "Running 'winapp init' with non-interactive mode..." 6

    # Use --use-defaults for non-interactive initialization
    # Setup stable SDKs for packaging
    $initCommand = "npx winapp init . --use-defaults --setup-sdks=stable"
    Assert-Command $initCommand "Failed to initialize winapp workspace"

    # Verify workspace was created
    Assert-DirectoryExists ".winapp" ".winapp directory"
    Assert-FileExists "winapp.yaml" "winapp.yaml configuration file"
    Assert-FileExists "appxmanifest.xml" "appxmanifest.xml manifest file"
    Assert-FileExists "devcert.pfx" "Development certificate"

    # ========================================================================
    # Create Native Addons
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Create Native Addons Phase"

    Write-TestStep "Creating C++ addon..." 7

    $addCppCommand = "npx winapp node create-addon --template cpp --name testCppAddon"
    Assert-Command $addCppCommand "Failed to create C++ addon"

    Assert-DirectoryExists "testCppAddon" "C++ addon directory"
    Assert-FileExists "testCppAddon\binding.gyp" "C++ addon binding.gyp file"

    Write-TestSuccess "C++ addon created"

    Write-TestStep "Creating C# addon..." 8

    $addCsharpCommand = "npx winapp node create-addon --template cs --name testCsAddon"
    Assert-Command $addCsharpCommand "Failed to create C# addon"

    Assert-DirectoryExists "testCsAddon" "C# addon directory"
    Assert-FileExists "testCsAddon\testCsAddon.csproj" "C# addon project file"

    Write-TestSuccess "C# addon created"

    # ========================================================================
    # Build Native Addons
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Build Addons Phase"

    Write-TestStep "Building C++ addon..." 9

    $buildCppCommand = "npm run build-testCppAddon"
    Assert-Command $buildCppCommand "Failed to build C++ addon"

    Write-TestSuccess "C++ addon built successfully"

    Write-TestStep "Building C# addon..." 10

    $buildCsCommand = "npm run build-testCsAddon"
    Assert-Command $buildCsCommand "Failed to build C# addon"

    Write-TestSuccess "C# addon built successfully"

    # ========================================================================
    # Add Electron Debug Identity
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Debug Identity Phase"

    Write-TestStep "Adding Electron debug identity..." 11

    $addIdentityCommand = "npx winapp node add-electron-debug-identity"
    Assert-Command $addIdentityCommand "Failed to add Electron debug identity"

    # Verify manifest was updated
    $manifestPath = Join-Path $electronAppDir "appxmanifest.xml"
    $manifestContent = Get-Content $manifestPath -Raw
    if ($manifestContent -like "*VisualElements*") {
        Write-TestSuccess "Manifest updated with visual elements"
    }

    # ========================================================================
    # Package Application
    # ========================================================================

    Write-TestHeader "E2E Electron Test - Package Phase"

    Write-TestStep "Building Electron application package..." 12

    # First, run npm package to create the packaged app
    $packageCommand = "npm run package"
    Assert-Command $packageCommand "Failed to package Electron app"

    # Find the output directory created by electron-forge
    $outDir = Join-Path $electronAppDir "out"
    if (-not (Test-Path $outDir)) {
        Write-TestError "Electron package output directory not found at $outDir"
        throw "Electron app packaging did not create output directory"
    }

    # Find the app package directory (typically 'out/<platform>' or similar)
    $appPackageDirs = Get-ChildItem -Path $outDir -Directory -ErrorAction SilentlyContinue
    if (-not $appPackageDirs) {
        Write-TestError "No app package directories found in $outDir"
        throw "Electron app package not created"
    }

    $appPackageDir = $appPackageDirs[0].FullName
    Write-TestSuccess "Electron app packaged to: $appPackageDir"

    Write-TestStep "Packaging app to MSIX..." 13

    $certPath = Join-Path $electronAppDir "devcert.pfx"
    $packCommand = "npx winapp pack `"$appPackageDir`" --cert `"$certPath`""
    Assert-Command $packCommand "Failed to package app to MSIX"

    # Verify MSIX was created (winapp pack outputs to the project root)
    $msixFiles = Get-ChildItem -Path $electronAppDir -Filter "*.msix" -ErrorAction SilentlyContinue
    if ($msixFiles) {
        Write-TestSuccess "MSIX package created and signed: $($msixFiles[0].Name)"
    } else {
        Write-TestError "No MSIX file found after packaging"
        throw "MSIX packaging failed - no output file generated"
    }

    # ========================================================================
    # Final Verification
    # ========================================================================

    Write-Host "`n$('='*80)" -ForegroundColor Green
    Write-Host "E2E ELECTRON TEST COMPLETED SUCCESSFULLY" -ForegroundColor Green
    Write-Host "$('='*80)`n" -ForegroundColor Green

} finally {
    # Restore to original location (handles any number of Push-Location calls)
    Set-Location $originalLocation

    # ========================================================================
    # Cleanup
    # ========================================================================

    if (-not $SkipCleanup) {
        Write-TestHeader "Cleanup"
        Write-TestStep "Cleaning up temporary test directory..." 15

        try {
            Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-TestSuccess "Test directory cleaned up: $testDir"
        } catch {
            Write-Verbose "Warning: Could not fully clean up test directory: $_"
        }
    } else {
        Write-Host "Test directory preserved at: $testDir" -ForegroundColor Yellow
    }
}
