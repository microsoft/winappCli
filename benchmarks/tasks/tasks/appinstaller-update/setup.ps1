param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP "fixture-$(Get-Random)"
$publisher = 'CN=Contoso Ltd, O=Contoso Ltd, L=Redmond, S=Washington, C=US'
$pfx = Join-Path $tmp 'signer.pfx'
[void](New-TestCodeSigningPfx -Subject $publisher -PfxPath $pfx -Password 'fixture')
$layout = New-SimpleAppLayout -Path "$tmp\layout" -ManifestText (New-PackageManifestText -Name 'Contoso.Notes' -Publisher $publisher -Version '1.2.0.0' -DisplayName 'Contoso Notes')
New-Item -ItemType Directory -Force (Join-Path $Workspace 'dist') | Out-Null
$msix = Join-Path $Workspace 'dist\Contoso.Notes_1.2.0.0_x64.msix'
$r = Invoke-SdkTool makeappx pack /d $layout /p $msix /o; if ($r.ExitCode) { throw $r.Output }
$r = Invoke-SdkTool signtool sign /fd SHA256 /f $pfx /p fixture $msix; if ($r.ExitCode) { throw $r.Output }
Remove-Item -Recurse -Force $tmp
