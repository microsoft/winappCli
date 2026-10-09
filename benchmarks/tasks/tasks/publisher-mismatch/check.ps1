param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'publisher-mismatch'
$fixture = Get-Content -Raw 'C:\bench-state\fixture.json' | ConvertFrom-Json
$candidates = @(Find-WorkspaceFile -Root $Workspace -Include '*.msix')
$signed = @($candidates | Where-Object { (Get-FileSignature $_.FullName).Thumbprint -eq $fixture.thumbprint }) | Select-Object -First 1
[void](Add-Check $r 'msix-built' ([bool]$candidates) ($(if ($candidates) { $candidates.Name -join ', ' } else { 'no .msix' })))
[void](Add-Check $r 'signed-by-pfx' ([bool]$signed) ($(if ($signed) { $signed.FullName } else { 'no .msix signed by certs\northwind.pfx' })) -Required)
if ($signed) {
    $id = Get-ManifestIdentity (Read-MsixManifest $signed.FullName)
    [void](Add-Check $r 'publisher-matches' ($id.Publisher -eq $fixture.subject) "Publisher '$($id.Publisher)'" -Required)
}
else { [void](Add-Check $r 'publisher-matches' $false 'no signed package to inspect' -Required) }
$hit = Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'SimpleApp.exe'
if ($hit -and $hit.Package.Name -ne 'Northwind.Viewer') { [void](Add-Check $r 'is-northwind' $false "installed $($hit.Package.Name)" -Required) }
Complete-CheckResult -Result $r -OutFile $OutFile
