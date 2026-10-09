#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\TaskCheck.psm1') -Force
    $script:manifestText = New-PackageManifestText -Name 'Contoso.Notes' -Publisher 'CN=Contoso Ltd, O=Contoso Ltd, C=US' -Version '1.2.0.0'
}

Describe 'Get-OverallStatus' {
    It 'passes when every required check passed' {
        Get-OverallStatus @(
            [pscustomobject]@{ passed = $true; required = $true }
            [pscustomobject]@{ passed = $false; required = $false }
        ) | Should -Be 'pass'
    }
    It 'is partial when some required check passed' {
        Get-OverallStatus @(
            [pscustomobject]@{ passed = $true; required = $true }
            [pscustomobject]@{ passed = $false; required = $true }
        ) | Should -Be 'partial'
    }
    It 'does not count guards or optional checks as progress' {
        Get-OverallStatus @(
            [pscustomobject]@{ passed = $true; required = $true; guard = $true }
            [pscustomobject]@{ passed = $true; required = $false }
            [pscustomobject]@{ passed = $false; required = $true }
        ) | Should -Be 'fail'
    }
    It 'fails with no checks' { Get-OverallStatus @() | Should -Be 'fail' }
}

Describe 'Add-Check and Complete-CheckResult' {
    It 'writes status, the failed required checks, and facts' {
        $r = New-CheckResult -Task 't'
        Add-Check $r 'a' $true 'ok' -Required | Should -BeTrue
        [void](Add-Check $r 'b' $false 'missing thing' -Required)
        [void](Add-Check $r 'g' $true 'untouched' -Guard)
        $r.facts.x = 1
        $out = Join-Path $TestDrive 'check.json'
        $doc = Complete-CheckResult -Result $r -OutFile $out
        $doc.status | Should -Be 'partial'
        $doc.why | Should -Be 'b: missing thing'
        $json = Get-Content -Raw $out | ConvertFrom-Json
        $json.checks.Count | Should -Be 3
        ($json.checks | Where-Object id -eq 'g').guard | Should -BeTrue
        $json.facts.x | Should -Be 1
    }
}

Describe 'Manifest parsing' {
    It 'reads identity and applications' {
        $m = Get-XmlDocument $manifestText
        $id = Get-ManifestIdentity $m
        $id.Name | Should -Be 'Contoso.Notes'
        $id.Publisher | Should -Be 'CN=Contoso Ltd, O=Contoso Ltd, C=US'
        $id.Version | Should -Be '1.2.0.0'
        $id.ProcessorArchitecture | Should -Be 'x64'
        (Get-ManifestApplications $m).Executable | Should -Be 'SimpleApp.exe'
    }
    It 'finds execution aliases, file types, and startup tasks under any namespace prefix' {
        $xml = @'
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
         xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
  <Applications>
    <Application Id="App" Executable="bin\NoteViewer.exe" EntryPoint="Windows.FullTrustApplication">
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias">
          <uap5:AppExecutionAlias><uap5:ExecutionAlias Alias="contoso-notes.exe" /></uap5:AppExecutionAlias>
        </uap5:Extension>
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="ctnote"><uap:SupportedFileTypes><uap:FileType>.CTNOTE</uap:FileType><uap:FileType>md</uap:FileType></uap:SupportedFileTypes></uap:FileTypeAssociation>
        </uap:Extension>
        <desktop:Extension Category="windows.startupTask" Executable="bin\SyncTray.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="Sync" Enabled="true" DisplayName="Sync" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>
'@
        $m = Get-XmlDocument $xml
        Get-ManifestExecutionAliases $m | Should -Be @('contoso-notes.exe')
        Get-ManifestFileTypes $m | Should -Be @('.ctnote', '.md')
        $t = @(Get-ManifestStartupTasks $m)
        $t.Count | Should -Be 1
        $t[0].TaskId | Should -Be 'Sync'
        $t[0].Enabled | Should -Be 'true'
        $t[0].Category | Should -Be 'windows.startupTask'
        $t[0].Executable | Should -Be 'bin\SyncTray.exe'
    }
}

Describe 'Test-AppInstallerDocument' {
    BeforeAll {
        $script:identity = [pscustomobject]@{ Name = 'Contoso.Notes'; Publisher = 'CN=Contoso Ltd, O=Contoso Ltd, C=US'; Version = '1.2.0.0'; ProcessorArchitecture = 'x64' }
        function New-AppInstaller([string]$OnLaunch = '<OnLaunch HoursBetweenUpdateChecks="0" />', [string]$Version = '1.2.0.0', [string]$Ns = 'http://schemas.microsoft.com/appx/appinstaller/2021', [string]$PkgUri = 'https://downloads.contoso.com/notes/Contoso.Notes_1.2.0.0_x64.msix') {
            @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="$Ns" Version="1.0.0.0" Uri="https://downloads.contoso.com/notes/Contoso.Notes.appinstaller">
  <MainPackage Name="Contoso.Notes" Publisher="CN=Contoso Ltd, O=Contoso Ltd, C=US" Version="$Version" ProcessorArchitecture="x64" Uri="$PkgUri" />
  <UpdateSettings>$OnLaunch</UpdateSettings>
</AppInstaller>
"@
        }
        function Invoke-Test([string]$Text) {
            $items = Test-AppInstallerDocument -Text $Text -Identity $identity -BaseUri 'https://downloads.contoso.com/notes' -MsixFileName 'Contoso.Notes_1.2.0.0_x64.msix'
            $h = @{}; foreach ($i in $items) { $h[$i.Id] = $i.Passed }; return $h
        }
    }
    It 'accepts a correct file' {
        $h = Invoke-Test (New-AppInstaller)
        $h.Values | Should -Not -Contain $false
        $h.Keys | Should -Contain 'appinstaller-every-launch'
    }
    It 'flags OnLaunch without HoursBetweenUpdateChecks="0"' {
        $h = Invoke-Test (New-AppInstaller -OnLaunch '<OnLaunch />')
        $h['appinstaller-onlaunch'] | Should -BeTrue
        $h['appinstaller-every-launch'] | Should -BeFalse
    }
    It 'flags missing OnLaunch' { (Invoke-Test (New-AppInstaller -OnLaunch '<AutomaticBackgroundTask />'))['appinstaller-onlaunch'] | Should -BeFalse }
    It 'flags a version mismatch' { (Invoke-Test (New-AppInstaller -Version '1.0.0.0'))['appinstaller-main-package'] | Should -BeFalse }
    It 'flags a wrong package URI' { (Invoke-Test (New-AppInstaller -PkgUri 'https://downloads.contoso.com/notes/app.msix'))['appinstaller-package-uri'] | Should -BeFalse }
    It 'accepts the 2017 namespace' { (Invoke-Test (New-AppInstaller -Ns 'http://schemas.microsoft.com/appx/appinstaller/2017/2'))['appinstaller-xml'] | Should -BeTrue }
    It 'rejects a foreign namespace' { (Invoke-Test (New-AppInstaller -Ns 'http://example.com/x'))['appinstaller-xml'] | Should -BeFalse }
    It 'reports invalid XML instead of throwing' { (Invoke-Test '<AppInstaller')['appinstaller-xml'] | Should -BeFalse }
}

Describe 'Compare-CertificateSnapshot' {
    It 'reports certificates added and removed per store' {
        $before = @([pscustomobject]@{ Store = 'LocalMachine\Root'; Thumbprint = 'A'; Subject = 'CN=A' })
        $after = @(
            [pscustomobject]@{ Store = 'LocalMachine\TrustedPeople'; Thumbprint = 'A'; Subject = 'CN=A' }
            [pscustomobject]@{ Store = 'LocalMachine\Root'; Thumbprint = 'B'; Subject = 'CN=B' }
        )
        $d = Compare-CertificateSnapshot -Before $before -After $after
        $d.Added.Count | Should -Be 2
        $d.Removed.Thumbprint | Should -Be 'A'
    }
}

Describe 'MSIX file helpers' {
    It 'reads the manifest out of a package archive' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $dir = Join-Path $TestDrive 'layout'
        New-Item -ItemType Directory $dir | Out-Null
        Set-Content (Join-Path $dir 'AppxManifest.xml') $manifestText
        $zip = Join-Path $TestDrive 'app.msix'
        [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip)
        (Get-ManifestIdentity (Read-MsixManifest $zip)).Name | Should -Be 'Contoso.Notes'
        Get-MsixEntries $zip | Should -Contain 'AppxManifest.xml'
    }
    It 'finds workspace files but skips node_modules and obj' {
        New-Item -ItemType Directory (Join-Path $TestDrive 'ws\node_modules\x'), (Join-Path $TestDrive 'ws\obj'), (Join-Path $TestDrive 'ws\dist') -Force | Out-Null
        Set-Content (Join-Path $TestDrive 'ws\node_modules\x\a.msix') ''
        Set-Content (Join-Path $TestDrive 'ws\obj\b.msix') ''
        Set-Content (Join-Path $TestDrive 'ws\dist\c.msix') ''
        (Find-WorkspaceFile -Root (Join-Path $TestDrive 'ws') -Include '*.msix').Name | Should -Be @('c.msix')
    }
}
