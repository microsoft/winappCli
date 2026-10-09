param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
dotnet publish -c Release -r win-x64 --self-contained false -o publish 2>&1 | Write-Host
Set-Location .\publish
winapp manifest generate . --package-name TaskBoard --executable TaskBoard.exe --publisher-name 'CN=Contoso' 2>&1 | Write-Host

winapp package . --generate-cert --install-cert --publisher 'CN=Contoso' --output ..\TaskBoard.msix 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path ..\TaskBoard.msix"
