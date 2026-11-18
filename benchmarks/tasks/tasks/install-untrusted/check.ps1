param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'install-untrusted'
$hit = Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'SimpleApp.exe'
if ($hit -and $hit.Package.Name -ne 'Contoso.Notes') { [void](Add-Check $r 'is-contoso-notes' $false "installed $($hit.Package.Name), expected Contoso.Notes" -Required) }
$fixture = Get-Content -Raw 'C:\bench-state\fixture.json' | ConvertFrom-Json
$msix = Join-Path $Workspace 'Contoso.Notes_1.0.0.0_x64.msix'
$same = (Test-Path $msix) -and (Get-FileHash $msix).Hash -eq $fixture.msixHash
[void](Add-Check $r 'package-unchanged' $same ($(if ($same) { 'original .msix untouched' } else { 'the .msix was changed, replaced, or removed' })))
Complete-CheckResult -Result $r -OutFile $OutFile
