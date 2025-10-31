#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Package Windows App Development CLI as MSIX bundle
.DESCRIPTION
    This script creates an MSIX bundle from pre-built CLI binaries for x64 and arm64 architectures.
.PARAMETER CliBinariesPath
    Path to the directory containing the built CLI binaries (should contain win-x64 and win-arm64 subdirectories).
    Defaults to artifacts/cli relative to the project root.
.PARAMETER Version
    Version number for the MSIX package in the format major.minor.patch (e.g., "1.2.3").
    Will be converted to MSIX format major.minor.patch.0 (e.g., "1.2.3.0").
    If not specified, reads from version.json and appends build number.
.PARAMETER CertPassword
    Password for the certificate file (devcert.pfx) if it's password-protected.
    If not provided, signtool will attempt to sign without a password.
.EXAMPLE
    .\scripts\package-msix.ps1
    .\scripts\package-msix.ps1 -CliBinariesPath "artifacts/cli"
    .\scripts\package-msix.ps1 -Version "1.2.3"
    .\scripts\package-msix.ps1 -Version "1.2.3" -CertPassword "MyPassword123"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$CliBinariesPath,
    
    [Parameter(Mandatory=$false)]
    [string]$Version,
    
    [Parameter(Mandatory=$false)]
    [string]$CertPassword = "password"
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Set-Location $ProjectRoot

# Default to artifacts/cli if not specified
if ([string]::IsNullOrEmpty($CliBinariesPath)) {
    $CliBinariesPath = "artifacts\cli"
}

# Convert to absolute path if relative
if (-not [System.IO.Path]::IsPathRooted($CliBinariesPath)) {
    $CliBinariesPath = Join-Path $ProjectRoot $CliBinariesPath
}

Write-Host "[MSIX] Starting MSIX bundle packaging..." -ForegroundColor Green
Write-Host "[INFO] Project root: $ProjectRoot" -ForegroundColor Gray
Write-Host "[INFO] CLI binaries path: $CliBinariesPath" -ForegroundColor Gray

# Validate that the path exists
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

# Detect current processor architecture and set the appropriate CLI exe
$CurrentArch = $env:PROCESSOR_ARCHITECTURE
$CliExe = switch ($CurrentArch) {
    "AMD64" { 
        Write-Host "[INFO] Detected x64 architecture" -ForegroundColor Gray
        $X64Exe 
    }
    "ARM64" { 
        Write-Host "[INFO] Detected ARM64 architecture" -ForegroundColor Gray
        $Arm64Exe 
    }
    default { 
        Write-Warning "Unknown architecture: $CurrentArch, defaulting to x64"
        $X64Exe 
    }
}

Write-Host "[INFO] Using CLI executable: $CliExe" -ForegroundColor Cyan
Write-Host ""

# Determine version for the MSIX package
if ([string]::IsNullOrEmpty($Version)) {
    Write-Host "[VERSION] Calculating package version..." -ForegroundColor Blue
    
    # Read base version from version.json
    $VersionJsonPath = Join-Path $ProjectRoot "version.json"
    if (-not (Test-Path $VersionJsonPath)) {
        Write-Error "version.json not found at $VersionJsonPath and no -Version parameter provided"
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
    
    # MSIX version format is major.minor.patch.build (e.g., 1.2.3.25)
    $MsixVersion = "$BaseVersion.$BuildNumber"
} else {
    # Use provided version and append .0 for the build number if not already 4 parts
    $VersionParts = $Version.Split('.')
    if ($VersionParts.Length -eq 3) {
        $MsixVersion = "$Version.0"
    } elseif ($VersionParts.Length -eq 4) {
        $MsixVersion = $Version
    } else {
        Write-Error "Version must be in format major.minor.patch or major.minor.patch.build (e.g., 1.2.3 or 1.2.3.0)"
        exit 1
    }
}

Write-Host "[VERSION] MSIX package version: $MsixVersion" -ForegroundColor Cyan

# [Temporary], Ensure build tools are available in CI
Write-Host "[CLI] Ensure build tools are available" -ForegroundColor Cyan
$UpdateCmd = "& `"$CliExe`" update"
Write-Host "  Command: $UpdateCmd" -ForegroundColor DarkGray
Invoke-Expression $UpdateCmd

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to download build tools"
    exit 1
}

# Define paths
$ArtifactsPath = Join-Path $ProjectRoot "artifacts"
$MsixLayoutPath = Join-Path $ArtifactsPath "msix-layout"
$MsixSourcePath = Join-Path $ProjectRoot "msix"
$MsixAssetsPath = Join-Path $MsixSourcePath "Assets"
$MsixManifestPath = Join-Path $MsixSourcePath "appxmanifest.xml"

# Validate MSIX source files exist
if (-not (Test-Path $MsixManifestPath)) {
    Write-Error "AppxManifest.xml not found at: $MsixManifestPath"
    exit 1
}

if (-not (Test-Path $MsixAssetsPath)) {
    Write-Error "Assets folder not found at: $MsixAssetsPath"
    exit 1
}

# Clean and create MSIX layout structure
Write-Host "[LAYOUT] Creating MSIX bundle layout structure..." -ForegroundColor Blue
if (Test-Path $MsixLayoutPath) {
    Remove-Item $MsixLayoutPath -Recurse -Force
}
New-Item -ItemType Directory -Path $MsixLayoutPath -Force | Out-Null

# Create architecture-specific package folders
$X64LayoutPath = Join-Path $MsixLayoutPath "x64"
$Arm64LayoutPath = Join-Path $MsixLayoutPath "arm64"

New-Item -ItemType Directory -Path $X64LayoutPath -Force | Out-Null
New-Item -ItemType Directory -Path $Arm64LayoutPath -Force | Out-Null

Write-Host "[LAYOUT] Created layout folders:" -ForegroundColor Green
Write-Host "  - x64: $X64LayoutPath" -ForegroundColor Gray
Write-Host "  - arm64: $Arm64LayoutPath" -ForegroundColor Gray

# Function to create package layout for a specific architecture
function New-MsixPackageLayout {
    param(
        [string]$LayoutPath,
        [string]$SourceBinPath,
        [string]$Architecture,
        [string]$Version
    )
    
    Write-Host "[COPY] Creating $Architecture package layout..." -ForegroundColor Blue
    
    # Copy only the exe from the source
    Write-Host "  - Copying winapp.exe from $SourceBinPath..." -ForegroundColor Gray
    $SourceExe = Join-Path $SourceBinPath "winapp.exe"
    $TargetExe = Join-Path $LayoutPath "winapp.exe"
    
    if (-not (Test-Path $SourceExe)) {
        Write-Error "winapp.exe not found at $SourceExe"
        return
    }
    
    Copy-Item $SourceExe $TargetExe -Force
    Write-Host "  - Copied winapp.exe" -ForegroundColor Gray
    
    # Copy Assets folder
    $TargetAssetsPath = Join-Path $LayoutPath "Assets"
    Write-Host "  - Copying assets..." -ForegroundColor Gray
    Copy-Item $MsixAssetsPath $TargetAssetsPath -Recurse -Force
    
    # Copy and update AppxManifest.xml
    Write-Host "  - Creating AppxManifest.xml for $Architecture..." -ForegroundColor Gray
    [xml]$ManifestXml = Get-Content $MsixManifestPath
    
    # Update ProcessorArchitecture in the Identity element
    $ManifestXml.Package.Identity.ProcessorArchitecture = $Architecture
    Write-Host "  - Updated ProcessorArchitecture to $Architecture" -ForegroundColor Gray
    
    # Update Version in the Identity element
    $ManifestXml.Package.Identity.Version = $Version
    Write-Host "  - Updated Version to $Version" -ForegroundColor Gray
    
    # Write updated manifest
    $TargetManifestPath = Join-Path $LayoutPath "AppxManifest.xml"
    $ManifestXml.Save($TargetManifestPath)
    
    Write-Host "[COPY] $Architecture package layout created successfully!" -ForegroundColor Green
}

# Create package layouts for both architectures
New-MsixPackageLayout -LayoutPath $X64LayoutPath -SourceBinPath $X64Path -Architecture "x64" -Version $MsixVersion
Write-Host ""
New-MsixPackageLayout -LayoutPath $Arm64LayoutPath -SourceBinPath $Arm64Path -Architecture "arm64" -Version $MsixVersion
Write-Host ""

Write-Host "[SUCCESS] MSIX bundle layout structure created!" -ForegroundColor Green
Write-Host "[INFO] Version: $MsixVersion" -ForegroundColor Cyan
Write-Host "[INFO] Layout location: $MsixLayoutPath" -ForegroundColor Cyan
Write-Host ""

# Create temporary folder for individual MSIX packages (they'll be bundled and placed in artifacts root)
$PackagesPath = Join-Path $MsixLayoutPath "packages"
Write-Host "[PACKAGE] Creating MSIX packages..." -ForegroundColor Blue
New-Item -ItemType Directory -Path $PackagesPath -Force | Out-Null

# Check for dev certificate and generate if needed
$DevCertPath = Join-Path $ProjectRoot "devcert.pfx"
$CertParam = ""

if (-not (Test-Path $DevCertPath)) {
    Write-Host "[CERT] Dev certificate not found, generating new certificate..." -ForegroundColor Yellow
    Write-Host "  Certificate will be generated from manifest: $MsixManifestPath" -ForegroundColor Gray
    
    # Generate certificate using the CLI
    $CertGenerateCmd = "& `"$CliExe`" cert generate --manifest `"$MsixManifestPath`""
    Write-Host "  Command: $CertGenerateCmd" -ForegroundColor DarkGray
    Invoke-Expression $CertGenerateCmd
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to generate certificate"
        exit 1
    }
    
    # Verify certificate was created
    if (-not (Test-Path $DevCertPath)) {
        Write-Error "Certificate generation completed but devcert.pfx not found at $DevCertPath"
        exit 1
    }
    
    Write-Host "[CERT] Certificate generated successfully!" -ForegroundColor Green
}

if (Test-Path $DevCertPath) {
    $CertParam = "--cert `"$DevCertPath`""
    Write-Host "[INFO] Using dev certificate: $DevCertPath" -ForegroundColor Gray
} else {
    Write-Warning "Dev certificate not found at $DevCertPath. Packages will not be signed."
}

# Package x64
Write-Host "[PACKAGE] Creating x64 MSIX package..." -ForegroundColor Blue
$X64PackageCmd = "& `"$CliExe`" package `"$X64LayoutPath`" --name winapp_x64 --output-folder `"$PackagesPath`" $CertParam"
Write-Host "  Command: $X64PackageCmd" -ForegroundColor Gray
Invoke-Expression $X64PackageCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create x64 MSIX package"
    exit 1
}
Write-Host "[PACKAGE] x64 package created successfully!" -ForegroundColor Green
Write-Host ""

# Package arm64
Write-Host "[PACKAGE] Creating arm64 MSIX package..." -ForegroundColor Blue
$Arm64PackageCmd = "& `"$CliExe`" package `"$Arm64LayoutPath`" --name winapp_arm64 --output-folder `"$PackagesPath`" $CertParam"
Write-Host "  Command: $Arm64PackageCmd" -ForegroundColor Gray
Invoke-Expression $Arm64PackageCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create arm64 MSIX package"
    exit 1
}
Write-Host "[PACKAGE] arm64 package created successfully!" -ForegroundColor Green
Write-Host ""

Write-Host "[SUCCESS] MSIX packages created!" -ForegroundColor Green
Write-Host ""

# Create distribution folder first
Write-Host "[BUNDLE] Preparing distribution package..." -ForegroundColor Blue
$DistributionPath = Join-Path $ArtifactsPath "msix-bundle"

if (Test-Path $DistributionPath) {
    Remove-Item $DistributionPath -Recurse -Force
}
New-Item -ItemType Directory -Path $DistributionPath -Force | Out-Null

# Create the MSIX bundle directly in the distribution folder
Write-Host "[BUNDLE] Creating MSIX bundle..." -ForegroundColor Blue
$BundleName = "winapp_$($MsixVersion -replace '\.', '_')"
$BundleOutputPath = Join-Path $DistributionPath "$BundleName.msixbundle"

# Create mapping file for makeappx
$MappingFilePath = Join-Path $PackagesPath "bundle_mapping.txt"
$MappingContent = @"
[Files]
"$PackagesPath\winapp_x64.msix" "winapp_x64.msix"
"$PackagesPath\winapp_arm64.msix" "winapp_arm64.msix"
"@
$MappingContent | Set-Content $MappingFilePath -Encoding ASCII

Write-Host "  - Created bundle mapping file" -ForegroundColor Gray

# Create the bundle using makeappx via CLI
$MakeAppxCmd = "& `"$CliExe`" tool makeappx bundle /f `"$MappingFilePath`" /p `"$BundleOutputPath`" /o"
Write-Host "  - Creating bundle with makeappx..." -ForegroundColor Gray
Write-Host "    Command: $MakeAppxCmd" -ForegroundColor DarkGray
Invoke-Expression $MakeAppxCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create MSIX bundle"
    exit 1
}
Write-Host "[BUNDLE] Bundle created successfully!" -ForegroundColor Green
Write-Host ""

# Sign the bundle if certificate is available
if (Test-Path $DevCertPath) {
    Write-Host "[SIGN] Signing MSIX bundle..." -ForegroundColor Blue
    
    # Build signtool command with password if provided
    $SignToolCmd = "& `"$CliExe`" tool signtool sign /fd SHA256 /a /f `"$DevCertPath`""
    
    if (-not [string]::IsNullOrEmpty($CertPassword)) {
        $SignToolCmd += " /p `"$CertPassword`""
    }
    
    $SignToolCmd += " `"$BundleOutputPath`""
    
    Write-Host "  Signing with certificate: $DevCertPath" -ForegroundColor Gray
    Invoke-Expression $SignToolCmd
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to sign MSIX bundle (exit code: $LASTEXITCODE)"
        Write-Host "  This could be due to:" -ForegroundColor Yellow
        Write-Host "    - Incorrect certificate password" -ForegroundColor Yellow
        Write-Host "    - Certificate not suitable for code signing" -ForegroundColor Yellow
        Write-Host "    - Certificate expired" -ForegroundColor Yellow
        Write-Host "  Use -CertPassword parameter if the certificate is password-protected" -ForegroundColor Yellow
    } else {
        Write-Host "[SIGN] Bundle signed successfully!" -ForegroundColor Green
    }
    Write-Host ""
}

# Add helper scripts and documentation to distribution folder
Write-Host "[DISTRIBUTE] Adding installation helpers..." -ForegroundColor Blue

$MsixAssetsPath = Join-Path $PSScriptRoot "msix-assets"

# Copy the PowerShell installer script
$InstallerScriptSource = Join-Path $MsixAssetsPath "install-msix.ps1"
$InstallerScriptDest = Join-Path $DistributionPath "install.ps1"
Copy-Item $InstallerScriptSource $InstallerScriptDest -Force
Write-Host "  - Added PowerShell installer script" -ForegroundColor Gray

# Copy the CMD wrapper script
$InstallerCmdSource = Join-Path $MsixAssetsPath "install.cmd"
$InstallerCmdDest = Join-Path $DistributionPath "install.cmd"
Copy-Item $InstallerCmdSource $InstallerCmdDest -Force
Write-Host "  - Added CMD wrapper script" -ForegroundColor Gray

# Copy and customize the README
$ReadmeSource = Join-Path $MsixAssetsPath "README.md"
$ReadmeDest = Join-Path $DistributionPath "README.md"

# Read the template README and replace version placeholder
$ReadmeContent = Get-Content $ReadmeSource -Raw
$ReadmeContent = $ReadmeContent -replace '\[version\]', $MsixVersion
$ReadmeContent = $ReadmeContent -replace 'winapp\_[version]\.msixbundle', (Split-Path $BundleOutputPath -Leaf)

$ReadmeContent | Set-Content $ReadmeDest -Encoding UTF8
Write-Host "  - Added README.md" -ForegroundColor Gray

Write-Host "[DISTRIBUTE] Distribution package created!" -ForegroundColor Green
Write-Host ""

Write-Host "[SUCCESS] MSIX bundle packaging complete!" -ForegroundColor Green
Write-Host "[INFO] Version: $MsixVersion" -ForegroundColor Cyan
Write-Host "[INFO] Distribution: $DistributionPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Distribution package contents:" -ForegroundColor White
Get-ChildItem $DistributionPath | ForEach-Object {
    $size = if ($_.PSIsContainer) { "(folder)" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  * $($_.Name) $size" -ForegroundColor Gray
}
Write-Host ""
Write-Host "[DONE] Ready for distribution!" -ForegroundColor Green
Write-Host "Share the '$DistributionPath' folder with users." -ForegroundColor Cyan


