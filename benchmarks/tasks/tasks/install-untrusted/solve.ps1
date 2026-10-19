# Reference solution: trust the package's signer for app installs only (TrustedPeople), then install.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$msix = Join-Path $Workspace 'Contoso.Notes_1.0.0.0_x64.msix'
$cert = (Get-AuthenticodeSignature $msix).SignerCertificate
$cer = Join-Path $env:TEMP 'signer.cer'
[IO.File]::WriteAllBytes($cer, $cert.Export('Cert'))
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
powershell -NoProfile -Command "Add-AppxPackage -Path '$msix'"
