<#
.SYNOPSIS
    Builds the pinned WinUI 3 Gallery once, caches the package, and installs it for the UI benchmark.

.DESCRIPTION
    The benchmark needs a Gallery whose version never changes and that cannot be confused with a
    Gallery you installed yourself. This script clones microsoft/WinUI-Gallery at the tag pinned in
    config.json, gives the package its own identity, display name, window title and protocol, builds
    it for this machine's architecture, and caches the result under results\.cache\gallery.

    Installing is idempotent: when the cached build is already installed, nothing changes. With
    Developer Mode on, the cached layout is registered without trusting any certificate. Otherwise
    the signed MSIX is installed, which needs its certificate trusted first (run the printed
    `winapp cert install` command from an elevated prompt).

.EXAMPLE
    ./setup-gallery.ps1              # build if needed, then install if needed
    ./setup-gallery.ps1 -NoInstall   # build and cache only
    ./setup-gallery.ps1 -Rebuild     # rebuild even when a cached build exists
    ./setup-gallery.ps1 -Uninstall   # remove the benchmark Gallery package
#>
[CmdletBinding()]
param(
    [switch]$NoInstall,
    [switch]$Rebuild,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$config = (Get-Content -Raw (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json).gallery
$cache = Join-Path $PSScriptRoot 'results\.cache'
$src = Join-Path $cache "WinUI-Gallery-$($config.tag)"
$out = Join-Path $cache 'gallery'
$layout = Join-Path $out 'layout'
$pfx = Join-Path $out 'uibench.pfx'
$cer = Join-Path $out 'uibench.cer'
$stampPath = Join-Path $out 'build.json'
$platform = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'Arm64' { 'ARM64' }
    'X64' { 'x64' }
    default { throw "Unsupported architecture $_." }
}
$rid = "win-$($platform.ToLowerInvariant())"

function Get-InstalledGallery {
    Get-AppxPackage -Name $config.packageName -ErrorAction SilentlyContinue | Select-Object -First 1
}

if ($Uninstall) {
    $pkg = Get-InstalledGallery
    if ($pkg) {
        Remove-AppxPackage -Package $pkg.PackageFullName
        Write-Host "Removed $($pkg.PackageFullName)."
    }
    else { Write-Host 'The benchmark Gallery is not installed.' }
    return
}

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') exited $LASTEXITCODE." }
}

# --- Source ------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    Invoke-Native git @('clone', '--quiet', '--depth', '1', '--branch', $config.tag, $config.repository, $src)
}
$head = (& git -C $src rev-parse HEAD).Trim()
if ($head -ne $config.commit) { throw "Gallery source at $src is $head, expected $($config.commit) for tag $($config.tag). Delete the folder and rerun." }

$built = $false
if (Test-Path $stampPath) {
    $stamp = Get-Content -Raw $stampPath | ConvertFrom-Json
    $built = $stamp.commit -eq $config.commit -and $stamp.packageName -eq $config.packageName -and
        $stamp.version -eq $config.version -and $stamp.platform -eq $platform -and (Test-Path (Join-Path $layout 'AppxManifest.xml'))
}

if ($Rebuild -or -not $built) {
    # --- Patch: own identity, name, title and protocol so it never collides with another Gallery.
    $manifestRel = 'WinUIGallery/Package.appxmanifest'
    $windowRel = 'WinUIGallery/MainWindow.xaml.cs'
    Invoke-Native git @('-C', $src, 'checkout', '--quiet', '--', $manifestRel, $windowRel)

    $manifestPath = Join-Path $src $manifestRel
    [xml]$manifest = Get-Content -Raw $manifestPath
    $ns = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $ns.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
    $identity.SetAttribute('Name', $config.packageName)
    $identity.SetAttribute('Publisher', $config.publisher)
    $identity.SetAttribute('Version', $config.version)
    $manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $ns).InnerText = $config.displayName
    $visual = $manifest.SelectSingleNode('//uap:VisualElements', $ns)
    $visual.SetAttribute('DisplayName', $config.displayName)
    $visual.SetAttribute('Description', $config.displayName)
    $protocol = $manifest.SelectSingleNode('//uap:Protocol', $ns)
    $protocol.SetAttribute('Name', $config.protocol)
    $protocol.SelectSingleNode('uap:DisplayName', $ns).InnerText = $config.displayName
    # Web links to winuigallery.com must keep opening the user's own Gallery.
    $uriHandler = $manifest.SelectSingleNode("//uap3:Extension[@Category='windows.appUriHandler']", $ns)
    if ($uriHandler) { [void]$uriHandler.ParentNode.RemoveChild($uriHandler) }
    $manifest.Save($manifestPath)

    $windowPath = Join-Path $src $windowRel
    $window = Get-Content -Raw $windowPath
    $titleLine = 'this.Title = "WinUI 3 Gallery";'
    if (([regex]::Matches($window, [regex]::Escape($titleLine))).Count -ne 1) { throw "Expected one '$titleLine' in $windowRel." }
    Set-Content -Path $windowPath -Value $window.Replace($titleLine, "this.Title = `"$($config.displayName)`";") -NoNewline -Encoding utf8

    # --- Certificate (local test signing only; never installed by this script).
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    if (-not (Test-Path $pfx) -or -not (Test-Path $cer)) {
        Invoke-Native winapp @('cert', 'generate', '--publisher', $config.publisher, '--output', $pfx, '--export-cer', '--if-exists', 'Overwrite', '--quiet')
    }

    # --- Build
    # Gallery v2.9.3 targets Windows SDK 10.0.22621, which current Visual Studio installs no longer
    # include; build against the SDK named in config.json instead (the app's code is unchanged).
    $sdk = $config.windowsSdk
    $platformXml = "${env:ProgramFiles(x86)}\Windows Kits\10\Platforms\UAP\$($sdk.version)\Platform.xml"
    if (-not (Test-Path $platformXml)) { throw "Windows SDK $($sdk.version) is not installed ($platformXml). Install it with the Visual Studio Installer, or change gallery.windowsSdk in config.json to an installed SDK." }

    # The XAML compiler fails without a message on long paths, so build through a short junction.
    $short = Join-Path ([System.IO.Path]::GetTempPath()) "uibench-gallery-$PID"
    New-Item -ItemType Junction -Path $short -Target $src | Out-Null
    try {
        $proj = Join-Path $short 'WinUIGallery\WinUIGallery.csproj'
        $pkgDir = Join-Path ([System.IO.Path]::GetTempPath()) "uibench-gallery-pkg-$PID\"
        Remove-Item -Recurse -Force -LiteralPath (Join-Path $src 'WinUIGallery\obj'), (Join-Path $src 'WinUIGallery\bin') -ErrorAction SilentlyContinue
        $log = Join-Path $out 'build.log'
        Write-Host "Building WinUI Gallery $($config.tag) for $platform (log: $log); this takes several minutes the first time..."
        & dotnet build $proj -c Release -nologo -v minimal `
            "-p:Platform=$platform" "-p:RuntimeIdentifier=$rid" '-p:SelfContained=true' '-p:WindowsAppSDKSelfContained=true' `
            "-p:SamplesTargetFrameworkMoniker=net9.0-windows$($sdk.version)" "-p:WindowsSdkPackageVersion=$($sdk.packageVersion)" `
            '-p:GenerateAppxPackageOnBuild=true' '-p:AppxBundle=Never' '-p:UapAppxPackageBuildMode=SideloadOnly' `
            '-p:AppxPackageSigningEnabled=false' "-p:AppxPackageDir=$pkgDir" *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content $log -Tail 30 | Write-Host
            throw "Gallery build failed (exit $LASTEXITCODE). Full log: $log"
        }
        $msix = Get-ChildItem -Path $pkgDir -Recurse -Filter *.msix | Select-Object -First 1
        if (-not $msix) { throw "The build produced no .msix under $pkgDir." }
        $cachedMsix = Join-Path $out 'WinUIGallery-uibench.msix'
        Copy-Item -Path $msix.FullName -Destination $cachedMsix -Force
        Remove-Item -Recurse -Force -LiteralPath $pkgDir -ErrorAction SilentlyContinue
    }
    finally {
        # Removing the junction itself, never its target.
        [System.IO.Directory]::Delete($short)
    }
    Invoke-Native winapp @('sign', $cachedMsix, $pfx, '--quiet')

    # A loose layout for Developer Mode registration, which needs no trusted certificate.
    Remove-Item -Recurse -Force -LiteralPath $layout -ErrorAction SilentlyContinue
    Expand-Archive -LiteralPath $cachedMsix -DestinationPath $layout
    foreach ($f in 'AppxSignature.p7x', 'AppxBlockMap.xml', '[Content_Types].xml', 'AppxMetadata') {
        Remove-Item -Recurse -Force -LiteralPath (Join-Path $layout $f) -ErrorAction SilentlyContinue
    }

    [ordered]@{
        tag = $config.tag; commit = $config.commit; packageName = $config.packageName; version = $config.version
        platform = $platform; builtAt = (Get-Date).ToString('o')
    } | ConvertTo-Json | Set-Content -Path $stampPath -Encoding utf8NoBOM
    Write-Host "Cached build: $out"
}
else {
    Write-Host "Using cached build: $out"
}

if ($NoInstall) { return }

# --- Install (idempotent) ----------------------------------------------------------------
$installed = Get-InstalledGallery
$stamp = Get-Content -Raw $stampPath | ConvertFrom-Json
if ($installed -and $installed.Version -eq $config.version -and -not $Rebuild -and
    $installed.InstallLocation -and (Test-Path (Join-Path $installed.InstallLocation 'AppxManifest.xml'))) {
    Write-Host "Already installed: $($installed.PackageFullName)"
    return
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
if ($devMode) {
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceUpdateFromAnyVersion
}
else {
    $thumb = (Get-PfxData -FilePath $pfx -Password (ConvertTo-SecureString 'password' -AsPlainText -Force)).EndEntityCertificates[0].Thumbprint
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople, Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb
    if (-not $trusted) {
        throw "Developer Mode is off and the benchmark certificate is not trusted. Either turn on Developer Mode, or run this once from an elevated prompt and rerun: winapp cert install `"$cer`""
    }
    Add-AppxPackage -Path (Join-Path $out 'WinUIGallery-uibench.msix') -ForceUpdateFromAnyVersion
}
$installed = Get-InstalledGallery
if (-not $installed) { throw "Install finished but $($config.packageName) is not registered." }
Write-Host "Installed: $($installed.PackageFullName)"
