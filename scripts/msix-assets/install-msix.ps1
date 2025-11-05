#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Install MSIX package and its certificate
.DESCRIPTION
    This script extracts and installs the certificate from an MSIX package to the local machine's
    Trusted People certificate store, then installs the package itself. This provides a complete
    installation experience in one command.
.PARAMETER PackagePath
    Path to the MSIX package file. If not specified, searches for .msix files in the current directory.
.PARAMETER CertPassword
    Password for the certificate if it's password-protected (optional).
.EXAMPLE
    .\install-msix.ps1
    .\install-msix.ps1 -PackagePath "winappcli_1.0.0.0_x64.msix"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$PackagePath,
    
    [Parameter(Mandatory=$false)]
    [SecureString]$CertPassword,
    
    [Parameter(Mandatory=$false)]
    [switch]$Elevated
)

# Set error action preference to stop on errors
$ErrorActionPreference = "Stop"

# Unblock downloaded files to avoid "downloaded from internet" warnings
Write-Host "Checking for blocked files..." -ForegroundColor Gray
$ScriptPath = $PSCommandPath
if ($ScriptPath -and (Test-Path $ScriptPath)) {
    try {
        Unblock-File -Path $ScriptPath -ErrorAction SilentlyContinue
        Write-Host "  - Unblocked installer script" -ForegroundColor Gray
    } catch {
        # Ignore errors - file might not be blocked
    }
}

# Unblock all files in the current directory (bundle and other assets)
try {
    Get-ChildItem -Path (Split-Path $ScriptPath -Parent) -File | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
    Write-Host "  - Unblocked bundle files" -ForegroundColor Gray
} catch {
    # Ignore errors - files might not be blocked
}

# Trap all errors and pause if elevated
trap {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  ERROR OCCURRED" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    
    if ($_.Exception) {
        Write-Host "Details:" -ForegroundColor Yellow
        Write-Host $_.Exception.Message -ForegroundColor Yellow
        Write-Host ""
    }
    
    if ($_.ScriptStackTrace) {
        Write-Host "Stack Trace:" -ForegroundColor Gray
        Write-Host $_.ScriptStackTrace -ForegroundColor Gray
        Write-Host ""
    }
    
    if ($Elevated) {
        Write-Host "Press Enter to close this window..." -ForegroundColor Cyan
        Read-Host
    }
    
    exit 1
}

Write-Host ""
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "  MSIX Package Certificate Installer" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "[INFO] This script needs administrator privileges to install certificates." -ForegroundColor Yellow
    Write-Host ""
    
    # Prompt user for elevation
    $response = Read-Host "Would you like to elevate to Administrator? (Y/N)"
    
    if ($response -eq 'Y' -or $response -eq 'y') {
        Write-Host ""
        Write-Host "[ELEVATE] Restarting script with administrator privileges..." -ForegroundColor Blue
        Write-Host ""
        
        # Build the arguments to pass to the elevated process
        # Preserve current directory and package path
        $currentDir = Get-Location
        $arguments = "-NoProfile -ExecutionPolicy Bypass -Command `"Set-Location '$currentDir'; & '$PSCommandPath' -Elevated"
        
        if (-not [string]::IsNullOrEmpty($PackagePath)) {
            # Convert to absolute path before passing
            $PackagePath = Resolve-Path $PackagePath -ErrorAction SilentlyContinue
            if ($PackagePath) {
                $arguments += " -PackagePath '$PackagePath'"
            }
        }
        
        $arguments += "`""
        
        # Start elevated process
        try {
            Start-Process PowerShell -Verb RunAs -ArgumentList $arguments
            Write-Host "[INFO] Script is running in elevated window. Check that window for results." -ForegroundColor Cyan
            Write-Host ""
            exit 0
        } catch {
            Write-Error "Failed to elevate: $_"
            Write-Host ""
            Read-Host "Press Enter to exit"
            exit 1
        }
    } else {
        Write-Host ""
        Write-Host "[CANCELLED] Certificate installation requires administrator privileges." -ForegroundColor Yellow
        Write-Host "The package cannot be installed without the certificate in the Trusted People store." -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
}

Write-Host "[INFO] Running with administrator privileges" -ForegroundColor Green
Write-Host ""

# Find the package if not specified
if ([string]::IsNullOrEmpty($PackagePath)) {
    Write-Host "[SEARCH] Looking for MSIX package in current directory..." -ForegroundColor Blue
    
    # Detect current processor architecture
    $CurrentArch = $env:PROCESSOR_ARCHITECTURE
    $ArchPattern = switch ($CurrentArch) {
        "AMD64" { "*_x64_*.msix" }
        "ARM64" { "*_arm64_*.msix" }
        default { "*.msix" }
    }
    
    Write-Host "[INFO] Detected architecture: $CurrentArch, looking for: $ArchPattern" -ForegroundColor Gray
    
    $packages = Get-ChildItem -Filter $ArchPattern | Select-Object -First 1
    
    if ($null -eq $packages) {
        Write-Host ""
        Write-Warning "No matching .msix file found for architecture: $CurrentArch"
        Write-Host "Looking for any .msix file..." -ForegroundColor Yellow
        $packages = Get-ChildItem -Filter "*.msix" | Select-Object -First 1
        
        if ($null -eq $packages) {
            Write-Error "No .msix files found in current directory."
            Write-Host "Please specify the package path with -PackagePath parameter." -ForegroundColor Yellow
            exit 1
        }
    }
    
    $PackagePath = $packages.FullName
    Write-Host "[FOUND] Using package: $($packages.Name)" -ForegroundColor Green
}

# Validate package exists
if (-not (Test-Path $PackagePath)) {
    Write-Error "Package not found at: $PackagePath"
    exit 1
}

$PackagePath = Resolve-Path $PackagePath
Write-Host "[INFO] Package: $PackagePath" -ForegroundColor Gray
Write-Host ""

# Create temporary directory for extraction
$TempDir = Join-Path $env:TEMP "msix-cert-install-$(Get-Random)"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    # Extract certificate from the package
    Write-Host "[EXTRACT] Extracting certificate from package..." -ForegroundColor Blue
    
    # Extract the MSIX (rename to .zip first as Expand-Archive doesn't recognize .msix)
    $MsixExtractPath = Join-Path $TempDir "msix"
    New-Item -ItemType Directory -Path $MsixExtractPath -Force | Out-Null
    $MsixAsZip = Join-Path $TempDir "package.zip"
    Copy-Item $PackagePath $MsixAsZip -Force
    Expand-Archive -Path $MsixAsZip -DestinationPath $MsixExtractPath -Force
    
    # Extract certificate from signature
    Write-Host "[CERT] Extracting certificate information..." -ForegroundColor Blue
    
    # Try to get signature from the package file
    $signature = Get-AuthenticodeSignature -FilePath $PackagePath
    $cert = $null
    
    if ($signature -and $signature.SignerCertificate) {
        $cert = $signature.SignerCertificate
        Write-Host "  - Found certificate in package signature" -ForegroundColor Gray
    } else {
        # Try to get signature from the MSIX package
        Write-Host "  - No signature in package, trying to extract manually..." -ForegroundColor Gray
        
        # Look for signature file manually
        $signatureFile = Get-ChildItem -Path $MsixExtractPath -Filter "AppxSignature.p7x" -Recurse | Select-Object -First 1
        
        if ($null -eq $signatureFile) {
            Write-Host ""
            Write-Warning "No signature found in MSIX package."
            Write-Host ""
            Write-Host "The package may not be signed. To sign the package:" -ForegroundColor Yellow
            Write-Host "  1. Create a code signing certificate" -ForegroundColor Yellow
            Write-Host "  2. Use the certificate when packaging with package-msix.ps1" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "For development, you can create a self-signed certificate:" -ForegroundColor Cyan
            Write-Host '  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=DevCert" -CertStoreLocation Cert:\CurrentUser\My' -ForegroundColor Gray
            Write-Host '  Export-PfxCertificate -Cert $cert -FilePath devcert.pfx -Password (ConvertTo-SecureString -String "password" -Force -AsPlainText)' -ForegroundColor Gray
            Write-Host ""
            exit 1
        }
        
        Write-Host "  - Found signature file, extracting certificate..." -ForegroundColor Gray
        # Try to extract certificate from the p7x file
        try {
            $p7xBytes = [System.IO.File]::ReadAllBytes($signatureFile.FullName)
            $signedCms = New-Object System.Security.Cryptography.Pkcs.SignedCms
            $signedCms.Decode($p7xBytes)
            $cert = $signedCms.Certificates[0]
            Write-Host "  - Extracted certificate from AppxSignature.p7x" -ForegroundColor Gray
        } catch {
            Write-Error "Failed to extract certificate from signature file: $_"
            exit 1
        }
    }
    
    if ($null -eq $cert) {
        Write-Error "Could not extract certificate from package"
        exit 1
    }
    Write-Host ""
    Write-Host "Certificate Details:" -ForegroundColor White
    Write-Host "  Subject:  $($cert.Subject)" -ForegroundColor Gray
    Write-Host "  Issuer:   $($cert.Issuer)" -ForegroundColor Gray
    Write-Host "  Expires:  $($cert.NotAfter)" -ForegroundColor Gray
    Write-Host "  Thumbprint: $($cert.Thumbprint)" -ForegroundColor Gray
    Write-Host ""
    
    # Check if certificate is already installed
    $existingCert = Get-ChildItem -Path Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    
    if ($existingCert) {
        Write-Host "[INFO] Certificate is already installed in Trusted People store!" -ForegroundColor Green
    } else {
        # Install certificate to Trusted People store
        Write-Host "[INSTALL] Installing certificate to Trusted People store..." -ForegroundColor Blue
        
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "LocalMachine")
        $store.Open("ReadWrite")
        $store.Add($cert)
        $store.Close()
        
        Write-Host "[SUCCESS] Certificate installed successfully!" -ForegroundColor Green
    }
    
    Write-Host ""
    
    # Now install the MSIX package
    Write-Host "[INSTALL] Installing MSIX package..." -ForegroundColor Blue
    Write-Host "  Package: $PackagePath" -ForegroundColor Gray
    Write-Host ""
    
    # Use Add-AppxPackage to install the package
    try {
        Add-AppxPackage -Path $PackagePath -ErrorAction Stop
        Write-Host "[SUCCESS] MSIX package installed successfully!" -ForegroundColor Green
        Write-Host ""
        Write-Host "The Windows App Development CLI has been installed." -ForegroundColor Cyan
        Write-Host "You can now use 'winapp' command from your terminal." -ForegroundColor Cyan
    } catch {
        Write-Host ""
        Write-Warning "Failed to install MSIX package automatically: $_"
        Write-Host ""
        Write-Host "You can try installing manually:" -ForegroundColor Yellow
        Write-Host "  1. Double-click the .msix file" -ForegroundColor Yellow
        Write-Host "  2. Or run: Add-AppxPackage -Path '$PackagePath'" -ForegroundColor Yellow
        Write-Host ""
    }
    
    Write-Host ""
    Write-Host "[DONE] Installation complete!" -ForegroundColor Green
    Write-Host ""
    
} finally {
    # Clean up temporary files
    if (Test-Path $TempDir) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Pause if running elevated so user can see results
    if ($Elevated) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}
