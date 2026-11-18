# The manifest's Publisher (CN=Northwind) does not match the PFX subject, so signing fails with 0x8007000B.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$subject = 'CN=Northwind Traders, O=Northwind Traders, C=US'
$thumb = New-TestCodeSigningPfx -Subject $subject -PfxPath (Join-Path $Workspace 'certs\northwind.pfx') -Password 'nw-signing-2026'
Set-Content (Join-Path $Workspace 'certs\password.txt') 'nw-signing-2026'
$layout = New-SimpleAppLayout -Path (Join-Path $Workspace 'layout') -ManifestText (New-PackageManifestText -Name 'Northwind.Viewer' -Publisher 'CN=Northwind' -Version '1.4.0.0' -DisplayName 'Northwind Viewer')
New-Item -ItemType Directory -Force (Join-Path $Workspace 'dist') | Out-Null
$r = Invoke-SdkTool makeappx pack /d $layout /p (Join-Path $Workspace 'dist\Northwind.Viewer_1.4.0.0_x64.msix') /o; if ($r.ExitCode) { throw $r.Output }
@{ thumbprint = $thumb; subject = $subject } | ConvertTo-Json | Set-Content 'C:\bench-state\fixture.json'
