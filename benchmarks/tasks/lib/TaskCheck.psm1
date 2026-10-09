Set-StrictMode -Version Latest

# Helpers for task setup and checker scripts. They run inside Windows Sandbox (PowerShell 7); the
# pure functions (XML, results, matching) are also unit-tested on the host.

#region Results

function New-CheckResult {
    param([Parameter(Mandatory)][string]$Task)
    return [pscustomobject]@{ task = $Task; checks = [System.Collections.Generic.List[object]]::new(); facts = [ordered]@{} }
}

function Add-Check {
    # Required checks decide pass; the others only show progress toward it.
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][bool]$Passed,
        [string]$Detail = '',
        [switch]$Required,
        # A guard is required but only protects what the fixture already had, so passing it is not progress.
        [switch]$Guard
    )
    $Result.checks.Add([pscustomobject]@{ id = $Id; passed = $Passed; required = [bool]($Required -or $Guard); guard = [bool]$Guard; detail = $Detail })
    return $Passed
}

function Get-OverallStatus {
    # pass: every required check passed. partial: some required, non-guard check passed. fail: otherwise.
    param([AllowEmptyCollection()][object[]]$Checks = @())
    $required = @($Checks | Where-Object required)
    if (-not $Checks) { return 'fail' }
    if ($required -and -not @($required | Where-Object { -not $_.passed })) { return 'pass' }
    $isGuard = { param($c) $c.PSObject.Properties['guard'] -and $c.guard }
    if (@($required | Where-Object { $_.passed -and -not (& $isGuard $_) })) { return 'partial' }
    return 'fail'
}

function Complete-CheckResult {
    param([Parameter(Mandatory)]$Result, [Parameter(Mandatory)][string]$OutFile)
    $status = Get-OverallStatus -Checks @($Result.checks)
    $failed = @($Result.checks | Where-Object { $_.required -and -not $_.passed } | ForEach-Object { "$($_.id): $($_.detail)" })
    $doc = [ordered]@{
        task   = $Result.task
        status = $status
        why    = if ($status -eq 'pass') { 'all required checks passed' } else { $failed -join '; ' }
        checks = @($Result.checks)
        facts  = $Result.facts
    }
    $doc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutFile -Encoding utf8NoBOM
    return $doc
}

#endregion

#region Manifest and App Installer XML

function Get-XmlDocument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $doc = [System.Xml.XmlDocument]::new()
    $doc.PreserveWhitespace = $false
    $doc.LoadXml($Text)
    return $doc
}

function Select-ByLocalName {
    # Namespace-agnostic element lookup: manifests mix uap, uap3, uap5, desktop, rescap, ... prefixes.
    param([Parameter(Mandatory)][System.Xml.XmlNode]$Node, [Parameter(Mandatory)][string]$LocalName)
    return @($Node.SelectNodes(".//*[local-name()='$LocalName']"))
}

function Get-ManifestIdentity {
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Manifest)
    $id = Select-ByLocalName $Manifest 'Identity' | Select-Object -First 1
    if (-not $id) { return $null }
    return [pscustomobject]@{
        Name                  = $id.GetAttribute('Name')
        Publisher             = $id.GetAttribute('Publisher')
        Version               = $id.GetAttribute('Version')
        ProcessorArchitecture = $id.GetAttribute('ProcessorArchitecture')
    }
}

function Get-ManifestApplications {
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Manifest)
    return @(Select-ByLocalName $Manifest 'Application' | Where-Object { $_.ParentNode.LocalName -eq 'Applications' } | ForEach-Object {
            [pscustomobject]@{ Id = $_.GetAttribute('Id'); Executable = $_.GetAttribute('Executable'); EntryPoint = $_.GetAttribute('EntryPoint') }
        })
}

function Get-ManifestExecutionAliases {
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Manifest)
    return @(Select-ByLocalName $Manifest 'ExecutionAlias' | ForEach-Object { $_.GetAttribute('Alias') } | Where-Object { $_ })
}

function Get-ManifestFileTypes {
    # File extensions of every FileTypeAssociation, lowercased with the leading dot.
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Manifest)
    $types = foreach ($fta in Select-ByLocalName $Manifest 'FileTypeAssociation') {
        foreach ($ft in Select-ByLocalName $fta 'FileType') {
            $t = $ft.InnerText.Trim().ToLowerInvariant()
            if ($t -and -not $t.StartsWith('.')) { $t = ".$t" }
            $t
        }
    }
    return @($types | Where-Object { $_ } | Select-Object -Unique)
}

function Get-ManifestStartupTasks {
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Manifest)
    return @(Select-ByLocalName $Manifest 'StartupTask' | ForEach-Object {
            $ext = $_.ParentNode
            [pscustomobject]@{
                TaskId     = $_.GetAttribute('TaskId')
                Enabled    = $_.GetAttribute('Enabled')
                Category   = if ($ext) { $ext.GetAttribute('Category') } else { $null }
                Executable = if ($ext -and $ext.GetAttribute('Executable')) { $ext.GetAttribute('Executable') } else { $_.GetAttribute('Executable') }
            }
        })
}

function Test-AppInstallerDocument {
    # Checks an .appinstaller file against the package it should install and the hosting URL.
    # Returns one item per check: Id, Passed, Required, Detail.
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)]$Identity,
        [Parameter(Mandatory)][string]$BaseUri,
        [Parameter(Mandatory)][string]$MsixFileName
    )
    $base = $BaseUri.TrimEnd('/') + '/'
    $items = [System.Collections.Generic.List[object]]::new()
    $add = { param($id, $ok, $detail, $req = $true) $items.Add([pscustomobject]@{ Id = $id; Passed = [bool]$ok; Required = [bool]$req; Detail = $detail }) }
    try { $doc = Get-XmlDocument $Text }
    catch { & $add 'appinstaller-xml' $false "not valid XML: $($_.Exception.Message)"; return $items.ToArray() }

    $root = $doc.DocumentElement
    $nsOk = $root.LocalName -eq 'AppInstaller' -and $root.NamespaceURI -match '^http://schemas\.microsoft\.com/appx/appinstaller/20\d\d(/\d+)?$'
    & $add 'appinstaller-xml' $nsOk "root <$($root.LocalName)> in '$($root.NamespaceURI)'"
    if (-not $nsOk) { return $items.ToArray() }

    $selfUri = $root.GetAttribute('Uri')
    & $add 'appinstaller-self-uri' ($selfUri -like "$base*.appinstaller") "Uri='$selfUri'"
    & $add 'appinstaller-version' ($root.GetAttribute('Version') -match '^\d+\.\d+\.\d+\.\d+$') "Version='$($root.GetAttribute('Version'))'"

    $main = @($root.ChildNodes | Where-Object { $_.LocalName -in 'MainPackage', 'MainBundle' }) | Select-Object -First 1
    if (-not $main) { & $add 'appinstaller-main-package' $false 'no MainPackage/MainBundle' }
    else {
        $mismatch = @()
        if ($main.GetAttribute('Name') -ne $Identity.Name) { $mismatch += "Name '$($main.GetAttribute('Name'))' != '$($Identity.Name)'" }
        if ($main.GetAttribute('Publisher') -ne $Identity.Publisher) { $mismatch += "Publisher '$($main.GetAttribute('Publisher'))' != '$($Identity.Publisher)'" }
        if ($main.GetAttribute('Version') -ne $Identity.Version) { $mismatch += "Version '$($main.GetAttribute('Version'))' != '$($Identity.Version)'" }
        $arch = $main.GetAttribute('ProcessorArchitecture')
        if ($main.LocalName -eq 'MainPackage' -and $arch -and $arch -ne $Identity.ProcessorArchitecture) { $mismatch += "ProcessorArchitecture '$arch' != '$($Identity.ProcessorArchitecture)'" }
        & $add 'appinstaller-main-package' (-not $mismatch) ($(if ($mismatch) { $mismatch -join '; ' } else { 'identity matches the MSIX' }))
        $uri = $main.GetAttribute('Uri')
        & $add 'appinstaller-package-uri' ($uri -eq "$base$MsixFileName") "Uri='$uri', expected '$base$MsixFileName'"
    }

    $update = @($root.ChildNodes | Where-Object LocalName -eq 'UpdateSettings') | Select-Object -First 1
    $onLaunch = if ($update) { @($update.ChildNodes | Where-Object LocalName -eq 'OnLaunch') | Select-Object -First 1 } else { $null }
    & $add 'appinstaller-onlaunch' ([bool]$onLaunch) ($(if ($onLaunch) { 'UpdateSettings/OnLaunch present' } else { 'no UpdateSettings/OnLaunch' }))
    if ($onLaunch) {
        # Absent HoursBetweenUpdateChecks means 24 hours, so "every launch" needs an explicit 0.
        $hours = $onLaunch.GetAttribute('HoursBetweenUpdateChecks')
        & $add 'appinstaller-every-launch' ($hours -eq '0') "HoursBetweenUpdateChecks='$hours' (0 checks on every launch)" $false
    }
    return $items.ToArray()
}

#endregion

#region MSIX files, signatures, and installed packages

function Read-MsixManifest {
    # AppxManifest.xml (or AppxMetadata/AppxBundleManifest.xml) from an .msix/.appx/.msixbundle as XmlDocument.
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { $entry = $zip.GetEntry('AppxMetadata/AppxBundleManifest.xml') }
        if (-not $entry) { throw "No AppxManifest.xml in $Path" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return Get-XmlDocument $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Get-MsixEntries {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object FullName) } finally { $zip.Dispose() }
}

function Find-WorkspaceFile {
    # Files under a folder by pattern, newest first, skipping dependency folders.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Include)
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Include $Include -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(node_modules|\.git|obj)\\' } |
            Sort-Object LastWriteTimeUtc -Descending)
}

function Get-FileSignature {
    # Authenticode signature of a file. Status is 'Valid' only when the chain is trusted on this machine.
    param([Parameter(Mandatory)][string]$Path)
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    return [pscustomobject]@{
        Status     = [string]$sig.Status
        Signed     = $null -ne $sig.SignerCertificate
        Subject    = $sig.SignerCertificate ? $sig.SignerCertificate.Subject : $null
        Thumbprint = $sig.SignerCertificate ? $sig.SignerCertificate.Thumbprint : $null
        Message    = $sig.StatusMessage
    }
}

function Invoke-WindowsPowerShell {
    # The Appx module is reliable only in Windows PowerShell; run a script there and return its JSON output.
    param([Parameter(Mandatory)][string]$Script)
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes("`$ProgressPreference='SilentlyContinue'; $Script"))
    $text = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1 | Out-String
    return $text
}

function Get-InstalledPackages {
    # Non-framework packages installed for the current user.
    $json = Invoke-WindowsPowerShell 'Get-AppxPackage | Where-Object { -not $_.IsFramework } | Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version, InstallLocation, SignatureKind, IsDevelopmentMode | ConvertTo-Json -Depth 3 -Compress'
    $start = $json.IndexOfAny([char[]]'[{')
    if ($start -lt 0) { return @() }
    return @($json.Substring($start) | ConvertFrom-Json)
}

function Get-NewPackages {
    param([AllowEmptyCollection()][string[]]$BaselineFullNames = @())
    return @(Get-InstalledPackages | Where-Object { $_.PackageFullName -notin $BaselineFullNames })
}

function Get-InstalledManifest {
    # The manifest of an installed package: AppxManifest.xml in its install location.
    param([Parameter(Mandatory)]$Package)
    $path = Join-Path $Package.InstallLocation 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-XmlDocument (Get-Content -Raw -LiteralPath $path)
}

function Start-PackagedApp {
    # Launches a package's application through shell activation; returns the AUMID used.
    param([Parameter(Mandatory)]$Package, [string]$AppId)
    if (-not $AppId) {
        $m = Get-InstalledManifest $Package
        $app = if ($m) { Get-ManifestApplications $m | Select-Object -First 1 } else { $null }
        if (-not $app) { return $null }
        $AppId = $app.Id
    }
    $aumid = "$($Package.PackageFamilyName)!$AppId"
    Start-Process "shell:AppsFolder\$aumid"
    return $aumid
}

function Get-CertificateSnapshot {
    # Thumbprint -> store and subject for the stores an agent might change to trust a certificate.
    $stores = 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root', 'Cert:\LocalMachine\TrustedPeople', 'Cert:\CurrentUser\TrustedPeople',
    'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\TrustedPublisher', 'Cert:\CurrentUser\TrustedPublisher'
    $list = foreach ($s in $stores) {
        foreach ($c in @(Get-ChildItem -Path $s -ErrorAction SilentlyContinue)) {
            $bc = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
            $eku = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
            [pscustomobject]@{
                Store       = $s.Substring(6)
                Thumbprint  = $c.Thumbprint
                Subject     = $c.Subject
                IsCa        = [bool]($bc -and $bc.CertificateAuthority)
                CodeSigning = [bool]($eku -and @($eku.EnhancedKeyUsages | Where-Object Value -eq '1.3.6.1.5.5.7.3.3'))
            }
        }
    }
    return @($list)
}

function Compare-CertificateSnapshot {
    param([AllowEmptyCollection()][object[]]$Before = @(), [AllowEmptyCollection()][object[]]$After = @())
    $key = { param($c) "$($c.Store)|$($c.Thumbprint)" }
    $had = [System.Collections.Generic.HashSet[string]]::new([string[]]@($Before | ForEach-Object { & $key $_ }))
    $has = [System.Collections.Generic.HashSet[string]]::new([string[]]@($After | ForEach-Object { & $key $_ }))
    return [pscustomobject]@{
        Added   = @($After | Where-Object { -not $had.Contains((& $key $_)) })
        Removed = @($Before | Where-Object { -not $has.Contains((& $key $_)) })
    }
}

function Get-SecuritySettings {
    # Machine settings a careless fix might weaken.
    $appx = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'
    $read = { param($p, $n) $v = Get-ItemProperty -Path $p -Name $n -ErrorAction SilentlyContinue; if ($v) { $v.$n } else { $null } }
    return [ordered]@{
        AllowDevelopmentWithoutDevLicense = & $read $appx 'AllowDevelopmentWithoutDevLicense'
        AllowAllTrustedApps               = & $read $appx 'AllowAllTrustedApps'
        PolicyAllowAllTrustedApps         = & $read $policy 'AllowAllTrustedApps'
        ExecutionPolicyLocalMachine       = & $read 'HKLM:\SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell' 'ExecutionPolicy'
    }
}

#endregion

#region Composite checks shared by tasks

function Find-NewPackageForExe {
    # New packages (since the baseline) with an Application whose Executable file name matches.
    param([AllowEmptyCollection()][string[]]$BaselineFullNames = @(), [Parameter(Mandatory)][string]$ExeName)
    $hits = foreach ($p in Get-NewPackages -BaselineFullNames $BaselineFullNames) {
        $m = Get-InstalledManifest $p
        if (-not $m) { continue }
        $app = Get-ManifestApplications $m | Where-Object { $_.Executable -and (Split-Path $_.Executable -Leaf) -ieq $ExeName } | Select-Object -First 1
        if ($app) { [pscustomobject]@{ Package = $p; Manifest = $m; App = $app } }
    }
    return @($hits)
}

function Wait-AppProcess {
    # Polls for a process by name, optionally only from below a folder. Returns it or $null.
    param([Parameter(Mandatory)][string]$Name, [string]$UnderPath, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $p = Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {
            -not $UnderPath -or ($_.Path -and $_.Path.StartsWith($UnderPath, [StringComparison]::OrdinalIgnoreCase))
        } | Select-Object -First 1
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Add-MsixFileChecks {
    # msix-built, msix-signed, msix-trusted for the newest package file under the workspace.
    param([Parameter(Mandatory)]$Result, [Parameter(Mandatory)][string]$Workspace, [switch]$RequireSigned)
    $msix = Find-WorkspaceFile -Root $Workspace -Include '*.msix', '*.msixbundle', '*.appx' | Select-Object -First 1
    [void](Add-Check $Result 'msix-built' ([bool]$msix) ($msix ? $msix.FullName : 'no .msix under the project folder') -Required)
    if (-not $msix) { return $null }
    $sig = Get-FileSignature $msix.FullName
    $Result.facts.msix = $msix.FullName
    $Result.facts.signer = $sig.Subject
    [void](Add-Check $Result 'msix-signed' $sig.Signed "status $($sig.Status); signer $($sig.Subject)" -Required:$RequireSigned)
    [void](Add-Check $Result 'msix-trusted' ($sig.Status -eq 'Valid') "status $($sig.Status): $($sig.Message)")
    return [pscustomobject]@{ File = $msix; Signature = $sig; Identity = (Get-ManifestIdentity (Read-MsixManifest $msix.FullName)) }
}

function Add-InstalledAppChecks {
    # installed (+ matches the built package) and launches, for a packaged app with a given exe.
    param([Parameter(Mandatory)]$Result, [Parameter(Mandatory)]$Baseline, [Parameter(Mandatory)][string]$ExeName, $Msix, [switch]$SkipLaunch)
    $hits = @(Find-NewPackageForExe -BaselineFullNames @($Baseline.packages) -ExeName $ExeName)
    [void](Add-Check $Result 'installed' ([bool]$hits) ($(if ($hits) { ($hits.Package.PackageFullName -join ', ') } else { "no new installed package runs $ExeName" })) -Required)
    if (-not $hits) { return $null }
    $hit = $hits[0]
    if ($Msix -and $Msix.Identity) {
        $match = @($hits | Where-Object { $_.Package.Name -eq $Msix.Identity.Name -and $_.Package.Version -eq $Msix.Identity.Version })
        if ($match) { $hit = $match[0] }
        [void](Add-Check $Result 'installed-matches-msix' ([bool]$match) "built $($Msix.Identity.Name) $($Msix.Identity.Version)")
    }
    $Result.facts.package = $hit.Package.PackageFullName
    $Result.facts.signatureKind = $hit.Package.SignatureKind
    if ($SkipLaunch) { return $hit }
    $aumid = Start-PackagedApp -Package $hit.Package -AppId $hit.App.Id
    $proc = Wait-AppProcess -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -UnderPath $hit.Package.InstallLocation
    [void](Add-Check $Result 'launches' ([bool]$proc) "activated $aumid; $(if ($proc) { "running $($proc.Path)" } else { 'no process from the package within 25 s' })" -Required)
    if ($proc) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    return $hit
}

#endregion

#region Fixture helpers (setup scripts)

function New-TestCodeSigningPfx {
    # Self-signed code-signing certificate exported to a PFX and then removed from the store, so the
    # machine does not trust it. Returns the thumbprint.
    param([Parameter(Mandatory)][string]$Subject, [Parameter(Mandatory)][string]$PfxPath, [Parameter(Mandatory)][string]$Password)
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -KeyExportPolicy Exportable -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(2)
    $secure = ConvertTo-SecureString $Password -AsPlainText -Force
    New-Item -ItemType Directory -Force -Path (Split-Path $PfxPath) | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $secure | Out-Null
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
    return $cert.Thumbprint
}

function New-PackageManifestText {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Publisher,
        [string]$Version = '1.0.0.0',
        [string]$DisplayName = $Name,
        [string]$Executable = 'SimpleApp.exe',
        [string]$Logo = 'Assets\StoreLogo.png',
        [string]$Square150 = 'Assets\Square150x150Logo.png',
        [string]$Square44 = 'Assets\Square44x44Logo.png'
    )
    $pub = [System.Security.SecurityElement]::Escape($Publisher)
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="$Name" Publisher="$pub" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>$DisplayName</DisplayName>
    <PublisherDisplayName>$DisplayName Team</PublisherDisplayName>
    <Logo>$Logo</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="$Executable" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="$DisplayName" Description="$DisplayName" BackgroundColor="transparent"
                          Square150x150Logo="$Square150" Square44x44Logo="$Square44" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
}

function New-PngFile {
    param([Parameter(Mandatory)][string]$Path, [int]$Size = 50)
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::SteelBlue); $g.Dispose()
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bmp.Dispose() }
}

function New-SimpleAppLayout {
    # A package layout: the prebuilt SimpleApp, logos, and a manifest. Returns the layout path.
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ManifestText, [string]$AppSource = 'C:\bench\harness\apps\SimpleApp', [switch]$SkipAssets)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    Copy-Item -Path (Join-Path $AppSource '*') -Destination $Path -Recurse -Force
    if (-not $SkipAssets) {
        New-PngFile (Join-Path $Path 'Assets\StoreLogo.png') 50
        New-PngFile (Join-Path $Path 'Assets\Square150x150Logo.png') 150
        New-PngFile (Join-Path $Path 'Assets\Square44x44Logo.png') 44
    }
    Set-Content -LiteralPath (Join-Path $Path 'AppxManifest.xml') -Value $ManifestText -Encoding utf8NoBOM
    return $Path
}

function Invoke-SdkTool {
    # makeappx/signtool from the harness copy of the Windows SDK build tools (not on the agent's PATH).
    param([Parameter(Mandatory)][ValidateSet('makeappx', 'signtool', 'makepri')][string]$Tool, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $exe = "C:\bench\harness\sdk\$Tool.exe"
    $out = & $exe @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

#endregion

Export-ModuleMember -Function *
