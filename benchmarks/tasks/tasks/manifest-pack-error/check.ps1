param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'manifest-pack-error'
$layout = Join-Path $Workspace 'layout'
$manifestPath = Join-Path $layout 'AppxManifest.xml'
$tmp = Join-Path $env:TEMP "check-$(Get-Random).msix"
$pack = Invoke-SdkTool makeappx pack /d $layout /p $tmp /o
$err = (($pack.Output -split "`r?`n") | Where-Object { $_ -match 'error' } | Select-Object -First 2) -join ' | '
[void](Add-Check $r 'layout-packs' ($pack.ExitCode -eq 0) ($(if ($pack.ExitCode -eq 0) { 'makeappx pack succeeded' } else { $err })) -Required)
if (Test-Path $manifestPath) {
    $m = Get-XmlDocument (Get-Content -Raw $manifestPath)
    $id = Get-ManifestIdentity $m
    [void](Add-Check $r 'version-kept' ($id -and $id.Version -match '^2\.1\.\d+\.\d+$') "Version '$($id.Version)'" -Guard)
    $ve = Select-ByLocalName $m 'VisualElements' | Select-Object -First 1
    $ok = $ve -and $ve.GetAttribute('Square44x44Logo') -and $ve.GetAttribute('Square150x150Logo')
    [void](Add-Check $r 'visual-elements-kept' ([bool]$ok) ($(if ($ve) { "Square44x44Logo='$($ve.GetAttribute('Square44x44Logo'))'" } else { 'no VisualElements' })) -Guard)
}
else { [void](Add-Check $r 'version-kept' $false 'layout\AppxManifest.xml missing' -Required) }
$msix = Find-WorkspaceFile -Root $Workspace -Include '*.msix' | Select-Object -First 1
[void](Add-Check $r 'msix-built' ([bool]$msix) ($(if ($msix) { $msix.FullName } else { 'no .msix' })))
Remove-Item $tmp -ErrorAction SilentlyContinue
Complete-CheckResult -Result $r -OutFile $OutFile
