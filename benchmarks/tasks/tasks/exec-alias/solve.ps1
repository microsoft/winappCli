param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
dotnet publish -c Release -r win-x64 --self-contained false -o publish 2>&1 | Write-Host
Set-Location .\publish
winapp manifest generate . --package-name ContosoNotes --executable ContosoNotes.exe --publisher-name 'CN=Contoso' 2>&1 | Write-Host
winapp manifest add-alias --name contoso-notes.exe --manifest .\Package.appxmanifest 2>&1 | Write-Host
winapp package . --generate-cert --install-cert --publisher 'CN=Contoso' --output ..\ContosoNotes.msix 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path ..\ContosoNotes.msix"
