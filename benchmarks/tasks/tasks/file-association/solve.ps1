param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
dotnet publish -c Release -r win-x64 --self-contained false -o publish 2>&1 | Write-Host
Set-Location .\publish
winapp manifest generate . --package-name NoteViewer --executable NoteViewer.exe --publisher-name 'CN=Contoso' 2>&1 | Write-Host
[xml]$m = Get-Content .\Package.appxmanifest
$ns = 'http://schemas.microsoft.com/appx/manifest/uap/windows10'
$app = $m.Package.Applications.Application
$ext = $app.SelectSingleNode("*[local-name()='Extensions']")
if (-not $ext) { $ext = $m.CreateElement('Extensions', $m.Package.NamespaceURI); [void]$app.AppendChild($ext) }
$e = $m.CreateElement('uap', 'Extension', $ns); $e.SetAttribute('Category', 'windows.fileTypeAssociation')
$fta = $m.CreateElement('uap', 'FileTypeAssociation', $ns); $fta.SetAttribute('Name', 'ctnote')
$types = $m.CreateElement('uap', 'SupportedFileTypes', $ns)
$ft = $m.CreateElement('uap', 'FileType', $ns); $ft.InnerText = '.ctnote'
[void]$types.AppendChild($ft); [void]$fta.AppendChild($types); [void]$e.AppendChild($fta); [void]$ext.AppendChild($e)
$m.Save((Resolve-Path .\Package.appxmanifest))
winapp package . --generate-cert --install-cert --publisher 'CN=Contoso' --output ..\NoteViewer.msix 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path ..\NoteViewer.msix"
