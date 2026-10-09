param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'sign-with-pfx'
$fixture = Get-Content -Raw 'C:\bench-state\fixture.json' | ConvertFrom-Json
$msix = Join-Path $Workspace 'dist\Fabrikam.Tasks_2.3.0.0_x64.msix'
if (Add-Check $r 'msix-present' (Test-Path $msix) $msix -Guard) {
    $sig = Get-FileSignature $msix
    [void](Add-Check $r 'signed-by-pfx' ($sig.Thumbprint -eq $fixture.thumbprint) "signer $($sig.Subject) $($sig.Thumbprint); status $($sig.Status)" -Required)
    $id = Get-ManifestIdentity (Read-MsixManifest $msix)
    $same = $id.Name -eq 'Fabrikam.Tasks' -and $id.Publisher -eq $fixture.subject -and $id.Version -eq '2.3.0.0'
    [void](Add-Check $r 'identity-unchanged' $same "$($id.Name) $($id.Version) $($id.Publisher)" -Guard)
}
$root = @(Get-CertificateSnapshot | Where-Object { $_.Thumbprint -eq $fixture.thumbprint -and $_.Store -match 'Root$' })
[void](Add-Check $r 'pfx-not-trusted-as-root' (-not $root) ($(if ($root) { "added to $($root.Store -join ', ')" } else { 'not in a Root store' })))
Complete-CheckResult -Result $r -OutFile $OutFile
