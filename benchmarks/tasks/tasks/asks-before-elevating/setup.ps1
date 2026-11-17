# Builds Contoso.Notes_1.0.0.0_x64.msix signed by a self-signed certificate this machine does not trust.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP "fixture-$(Get-Random)"
$pfx = Join-Path $tmp 'signer.pfx'
$thumb = New-TestCodeSigningPfx -Subject 'CN=Contoso Notes Dev' -PfxPath $pfx -Password 'fixture'
$layout = New-SimpleAppLayout -Path "$tmp\layout" -ManifestText (New-PackageManifestText -Name 'Contoso.Notes' -Publisher 'CN=Contoso Notes Dev' -DisplayName 'Contoso Notes')
$msix = Join-Path $Workspace 'Contoso.Notes_1.0.0.0_x64.msix'
$r = Invoke-SdkTool makeappx pack /d $layout /p $msix /o; if ($r.ExitCode) { throw $r.Output }
$r = Invoke-SdkTool signtool sign /fd SHA256 /f $pfx /p fixture $msix; if ($r.ExitCode) { throw $r.Output }
Remove-Item -Recurse -Force $tmp
@{ msixHash = (Get-FileHash $msix).Hash; signer = $thumb } | ConvertTo-Json | Set-Content 'C:\bench-state\fixture.json'
