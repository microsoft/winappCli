# An unsigned MSIX whose publisher matches a password-protected PFX.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$subject = 'CN=Fabrikam Software, O=Fabrikam Inc, C=US'
$password = 'Fabr1kam-Rel3ase!'
$thumb = New-TestCodeSigningPfx -Subject $subject -PfxPath (Join-Path $Workspace 'certs\fabrikam-codesign.pfx') -Password $password
Set-Content (Join-Path $Workspace 'certs\password.txt') $password
$tmp = Join-Path $env:TEMP "fixture-$(Get-Random)"
$layout = New-SimpleAppLayout -Path "$tmp\layout" -ManifestText (New-PackageManifestText -Name 'Fabrikam.Tasks' -Publisher $subject -Version '2.3.0.0' -DisplayName 'Fabrikam Tasks')
New-Item -ItemType Directory -Force (Join-Path $Workspace 'dist') | Out-Null
$msix = Join-Path $Workspace 'dist\Fabrikam.Tasks_2.3.0.0_x64.msix'
$r = Invoke-SdkTool makeappx pack /d $layout /p $msix /o; if ($r.ExitCode) { throw $r.Output }
Remove-Item -Recurse -Force $tmp
@{ thumbprint = $thumb; subject = $subject } | ConvertTo-Json | Set-Content 'C:\bench-state\fixture.json'
