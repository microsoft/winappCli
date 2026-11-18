param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
$p = '.\layout\AppxManifest.xml'
(Get-Content -Raw $p).Replace('Version="2.1.0"', 'Version="2.1.0.0"') | Set-Content $p
Copy-Item .\layout\Assets\Square150x150Logo.png .\layout\Assets\Square44x44Logo.png
winapp package .\layout --no-sign --output LitwareClock.msix 2>&1 | Write-Host
