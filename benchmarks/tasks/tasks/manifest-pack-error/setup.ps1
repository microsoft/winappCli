# Two problems, reported one at a time: a three-part Version, then a missing Square44x44Logo asset.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$layout = New-SimpleAppLayout -Path (Join-Path $Workspace 'layout') -ManifestText (New-PackageManifestText -Name 'Litware.Clock' -Publisher 'CN=Litware' -Version '2.1.0' -DisplayName 'Litware Clock')
Remove-Item (Join-Path $layout 'Assets\Square44x44Logo.png')
