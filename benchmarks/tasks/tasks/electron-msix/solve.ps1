# Reference solution without Electron tooling: Electron's prebuilt runtime plus the app as resources\app.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
npm install --no-audit --no-fund 2>&1 | Write-Host
$layout = Join-Path $Workspace 'out\layout'
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item .\node_modules\electron\dist\* $layout -Recurse -Force
Rename-Item (Join-Path $layout 'electron.exe') 'StickyNotes.exe'
New-Item -ItemType Directory -Force (Join-Path $layout 'resources\app') | Out-Null
Copy-Item .\package.json, .\main.js, .\index.html (Join-Path $layout 'resources\app')
Set-Location $layout
winapp manifest generate . --package-name StickyNotes --executable StickyNotes.exe --publisher-name 'CN=Contoso' 2>&1 | Write-Host
winapp package . --generate-cert --install-cert --publisher 'CN=Contoso' --output ..\StickyNotes.msix 2>&1 | Write-Host
powershell -NoProfile -Command "Add-AppxPackage -Path ..\StickyNotes.msix"
