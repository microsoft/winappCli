param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Workspace 'dist\Contoso.Notes_1.2.0.0_x64.msix'))
[xml]$m = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open()).ReadToEnd(); $zip.Dispose()
$i = $m.Package.Identity
$pub = [Security.SecurityElement]::Escape($i.Publisher)
@"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="1.2.0.0" Uri="https://downloads.contoso.com/notes/Contoso.Notes.appinstaller">
  <MainPackage Name="$($i.Name)" Publisher="$pub" Version="$($i.Version)" ProcessorArchitecture="$($i.ProcessorArchitecture)" Uri="https://downloads.contoso.com/notes/Contoso.Notes_1.2.0.0_x64.msix" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="0" />
  </UpdateSettings>
</AppInstaller>
"@ | Set-Content (Join-Path $Workspace 'dist\Contoso.Notes.appinstaller') -Encoding utf8NoBOM
