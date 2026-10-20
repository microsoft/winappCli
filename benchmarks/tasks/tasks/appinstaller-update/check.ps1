param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'appinstaller-update'
$file = Find-WorkspaceFile -Root $Workspace -Include '*.appinstaller' | Select-Object -First 1
if (Add-Check $r 'appinstaller-exists' ([bool]$file) ($(if ($file) { $file.FullName } else { 'no .appinstaller file' })) -Required) {
    $identity = Get-ManifestIdentity (Read-MsixManifest (Join-Path $Workspace 'dist\Contoso.Notes_1.2.0.0_x64.msix'))
    foreach ($i in Test-AppInstallerDocument -Text (Get-Content -Raw $file.FullName) -Identity $identity -BaseUri 'https://downloads.contoso.com/notes/' -MsixFileName 'Contoso.Notes_1.2.0.0_x64.msix') {
        [void](Add-Check $r $i.Id $i.Passed $i.Detail -Required:$true)
    }
    $r.facts.appinstaller = Get-Content -Raw $file.FullName
}
Complete-CheckResult -Result $r -OutFile $OutFile
