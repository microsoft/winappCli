param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
$subject = (Get-PfxData -FilePath .\certs\northwind.pfx -Password (ConvertTo-SecureString (Get-Content .\certs\password.txt -Raw).Trim() -AsPlainText -Force)).EndEntityCertificates[0].Subject
[xml]$m = Get-Content .\layout\AppxManifest.xml
$m.Package.Identity.Publisher = $subject
$m.Save((Resolve-Path .\layout\AppxManifest.xml))
$pw = (Get-Content .\certs\password.txt -Raw).Trim()
winapp package .\layout --cert .\certs\northwind.pfx --cert-password $pw --output .\dist\Northwind.Viewer_1.4.0.0_x64.msix 2>&1 | Write-Host
winapp cert install .\certs\northwind.pfx --password $pw 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path .\dist\Northwind.Viewer_1.4.0.0_x64.msix"
