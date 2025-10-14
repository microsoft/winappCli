#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for Windows SDK CLI, npm package, and MSIX bundle
.DESCRIPTION
    This script builds the Windows SDK CLI for both x64 and arm64 architectures,
    creates the npm package, creates MSIX bundle with distribution package, and 
    places all artifacts in an artifacts folder. Run this script from the root of the project.
.PARAMETER SkipTests
    Skip running unit tests
.PARAMETER FailOnTestFailure
    Exit with error code if tests fail (default: false, only warns)
.PARAMETER SkipMsix
    Skip MSIX bundle creation
.EXAMPLE
    .\scripts\build-cli.ps1
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipTests
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipMsix
#>

param(
    [switch]$Clean = $false,
    [switch]$SkipTests = $false,
    [switch]$FailOnTestFailure = $false,
    [switch]$SkipMsix = $true
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Set-Location $ProjectRoot

# Define paths
$CliSolutionPath = "src\winsdk-CLI\winsdk.sln"
$CliProjectPath = "src\winsdk-CLI\Winsdk.Cli\Winsdk.Cli.csproj"
$NpmProjectPath = "src\winsdk-npm"
$ArtifactsPath = "artifacts"
$TestResultsPath = "TestResults"

Write-Host "[*] Starting Windows SDK build process..." -ForegroundColor Green
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Write-Host "[CLEAN] Cleaning artifacts and test results..." -ForegroundColor Yellow
if (Test-Path $ArtifactsPath) {
    Remove-Item $ArtifactsPath -Recurse -Force
}
if (Test-Path $TestResultsPath) {
    Remove-Item $TestResultsPath -Recurse -Force
}

# Create artifacts directory
Write-Host "[SETUP] Creating artifacts directory..." -ForegroundColor Blue
New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null

# Step 1: Build CLI solution
Write-Host "[BUILD] Building CLI solution..." -ForegroundColor Blue
dotnet build $CliSolutionPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build CLI solution"
    exit 1
}

# Step 2: Run tests (unless skipped)
if (-not $SkipTests) {
    Write-Host "[TEST] Running tests..." -ForegroundColor Blue
    dotnet test $CliSolutionPath -c Release --no-build --results-directory .\TestResults -- --report-trx
    $TestExitCode = $LASTEXITCODE
    
    if ($TestExitCode -ne 0) {
        Write-Warning "Tests failed with exit code $TestExitCode"
        if ($FailOnTestFailure) {
            Write-Error "Stopping build due to test failures (FailOnTestFailure flag set)"
            exit 1
        } else {
            Write-Host "[TEST] Continuing build despite test failures..." -ForegroundColor Yellow
        }
    } else {
        Write-Host "[TEST] Tests passed!" -ForegroundColor Green
    }
    
    # Copy test results to artifacts - find all TRX files
    Write-Host "[TEST] Collecting test results..." -ForegroundColor Blue
    $TrxFiles = Get-ChildItem -Path "src\winsdk-CLI" -Filter "*.trx" -Recurse -File
    if ($TrxFiles) {
        New-Item -ItemType Directory -Path "$ArtifactsPath\TestResults" -Force | Out-Null
        foreach ($trxFile in $TrxFiles) {
            Copy-Item $trxFile.FullName "$ArtifactsPath\TestResults\" -Force
            Write-Host "[TEST] Copied: $($trxFile.Name)" -ForegroundColor Gray
        }
        Write-Host "[TEST] Test results copied successfully ($($TrxFiles.Count) file(s))" -ForegroundColor Green
    } else {
        Write-Warning "No TRX test result files found in src\winsdk-CLI"
    }
} else {
    Write-Host "[TEST] Skipping tests (SkipTests flag set)" -ForegroundColor Yellow
}

# Step 3: Calculate version with build number (moved before publish)
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
$BuildNumber = & "$PSScriptRoot\get-build-number.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to get build number"
    exit 1
}

# Construct full version (npm format: major.minor.patch-build.number)
$FullVersion = "$BaseVersion-build.$BuildNumber"
Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan

# Extract semantic version components for assembly versioning
# BaseVersion should be in format major.minor.patch (e.g., "0.1.0")
$VersionParts = $BaseVersion -split '\.'
$MajorVersion = $VersionParts[0]
$MinorVersion = $VersionParts[1]
$PatchVersion = $VersionParts[2]

# Assembly version uses format: major.minor.patch.buildnumber (e.g., "0.1.0.73")
$AssemblyVersion = "$MajorVersion.$MinorVersion.$PatchVersion.$BuildNumber"
Write-Host "[VERSION] Assembly version: $AssemblyVersion" -ForegroundColor Cyan

# InformationalVersion shows in --version output (e.g., "0.1.0-build.73")
$InformationalVersion = $FullVersion

# Step 4: Publish CLI for x64 with version properties
Write-Host "[PUBLISH] Publishing CLI for x64..." -ForegroundColor Blue
dotnet publish $CliProjectPath -c Release -r win-x64 --self-contained -o "$ArtifactsPath\cli\win-x64" `
    /p:Version=$AssemblyVersion `
    /p:AssemblyVersion=$AssemblyVersion `
    /p:FileVersion=$AssemblyVersion `
    /p:InformationalVersion=$InformationalVersion `
    /p:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish CLI for x64"
    exit 1
}

# Step 5: Publish CLI for arm64 with version properties
Write-Host "[PUBLISH] Publishing CLI for arm64..." -ForegroundColor Blue
dotnet publish $CliProjectPath -c Release -r win-arm64 --self-contained -o "$ArtifactsPath\cli\win-arm64" `
    /p:Version=$AssemblyVersion `
    /p:AssemblyVersion=$AssemblyVersion `
    /p:FileVersion=$AssemblyVersion `
    /p:InformationalVersion=$InformationalVersion `
    /p:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish CLI for arm64"
    exit 1
}

# Step 6: Prepare npm package
Write-Host "[NPM] Preparing npm package..." -ForegroundColor Blue

# Clean npm bin directory first
Push-Location $NpmProjectPath
npm run clean
if ($LASTEXITCODE -ne 0) {
    Write-Warning "npm clean failed, continuing..."
}

# Backup original package.json
Write-Host "[NPM] Setting package version to $FullVersion..." -ForegroundColor Blue
$PackageJsonPath = "package.json"
Copy-Item $PackageJsonPath "$PackageJsonPath.backup" -Force

# Update package.json version temporarily
$PackageJson = Get-Content $PackageJsonPath | ConvertFrom-Json
$OriginalVersion = $PackageJson.version
$PackageJson.version = $FullVersion
$PackageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonPath

# Copy the CLI binaries we just built instead of rebuilding them
Write-Host "[NPM] Copying CLI binaries to npm package..." -ForegroundColor Blue
$NpmBinPath = "bin"
New-Item -ItemType Directory -Path "$NpmBinPath\win-x64" -Force | Out-Null
New-Item -ItemType Directory -Path "$NpmBinPath\win-arm64" -Force | Out-Null

# Copy from our artifacts to npm bin folders
Copy-Item "$ProjectRoot\$ArtifactsPath\cli\win-x64\*" "$NpmBinPath\win-x64\" -Recurse -Force
Copy-Item "$ProjectRoot\$ArtifactsPath\cli\win-arm64\*" "$NpmBinPath\win-arm64\" -Recurse -Force

Pop-Location

# Step 7: Create npm package tarball
Write-Host "[PACK] Creating npm package tarball..." -ForegroundColor Blue
Push-Location $NpmProjectPath
npm pack --pack-destination "..\..\$ArtifactsPath"
$PackResult = $LASTEXITCODE

# Restore original package.json
Write-Host "[NPM] Restoring original package.json..." -ForegroundColor Blue
if (Test-Path "$PackageJsonPath.backup") {
    Move-Item "$PackageJsonPath.backup" $PackageJsonPath -Force
}

if ($PackResult -ne 0) {
    Write-Error "Failed to create npm package"
    Pop-Location
    exit 1
}
Pop-Location

# Step 8: Create MSIX bundle (optional)
if (-not $SkipMsix) {
    Write-Host ""
    Write-Host "[MSIX] Creating MSIX bundle..." -ForegroundColor Blue
    
    # Convert npm version format (e.g., 0.1.0-build.73) to MSIX format (e.g., 0.1.0.73)
    $MsixVersion = $FullVersion -replace '-build\.', '.'
    
    $PackageMsixScript = Join-Path $PSScriptRoot "package-msix.ps1"
    $CliBinariesPath = Join-Path (Join-Path $ProjectRoot $ArtifactsPath) "cli"
    
    & $PackageMsixScript -CliBinariesPath $CliBinariesPath -Version $MsixVersion
    
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "MSIX bundle creation failed, but continuing..."
    } else {
        Write-Host "[MSIX] MSIX bundle created successfully!" -ForegroundColor Green
    }
} else {
    Write-Host ""
    Write-Host "[MSIX] Skipping MSIX bundle creation (use -SkipMsix:`$false to enable)" -ForegroundColor Gray
}

# Build process complete - all artifacts are ready

# Display results
Write-Host ""
Write-Host "[SUCCESS] Build completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan
Write-Host "[INFO] Artifacts created in: $ArtifactsPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Contents:" -ForegroundColor White
Get-ChildItem $ArtifactsPath | ForEach-Object {
    $size = if ($_.PSIsContainer) { "(folder)" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  * $($_.Name) $size" -ForegroundColor Gray
}

Write-Host ""
Write-Host "[DONE] Ready for distribution!" -ForegroundColor Green