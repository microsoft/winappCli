$cert = Get-PfxCertificate .\certs\tidewell-2026.pfx
Write-Host "Signing with $($cert.Subject)"   # CN=Tidewell Software Ltd, O=Tidewell Software Ltd, C=GB
& signtool.exe sign /fd SHA256 /a /f .\certs\tidewell-2026.pfx /p $env:PFX_PASSWORD .\out\Tidewell.msix
