param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
winapp sign .\dist\Fabrikam.Tasks_2.3.0.0_x64.msix .\certs\fabrikam-codesign.pfx --password (Get-Content .\certs\password.txt -Raw).Trim() 2>&1 | Write-Host
