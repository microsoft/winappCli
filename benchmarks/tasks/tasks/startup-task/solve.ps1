param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
dotnet publish -c Release -r win-x64 --self-contained false -o publish 2>&1 | Write-Host
Set-Location .\publish
winapp manifest generate . --package-name SyncTray --executable SyncTray.exe --publisher-name 'CN=Contoso' 2>&1 | Write-Host
[xml]$m = Get-Content .\Package.appxmanifest
$ns = 'http://schemas.microsoft.com/appx/manifest/desktop/windows10'
$m.Package.SetAttribute('xmlns:desktop', $ns)
$app = $m.Package.Applications.Application
$ext = $app.SelectSingleNode("*[local-name()='Extensions']")
if (-not $ext) { $ext = $m.CreateElement('Extensions', $m.Package.NamespaceURI); [void]$app.AppendChild($ext) }
$e = $m.CreateElement('desktop', 'Extension', $ns); $e.SetAttribute('Category', 'windows.startupTask'); $e.SetAttribute('Executable', $app.Executable); $e.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
$st = $m.CreateElement('desktop', 'StartupTask', $ns); $st.SetAttribute('TaskId', 'SyncTrayStartup'); $st.SetAttribute('Enabled', 'true'); $st.SetAttribute('DisplayName', 'SyncTray')
[void]$e.AppendChild($st); [void]$ext.AppendChild($e)
$m.Save((Resolve-Path .\Package.appxmanifest))
winapp package . --generate-cert --install-cert --publisher 'CN=Contoso' --output ..\SyncTray.msix 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path ..\SyncTray.msix"
