param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'electron-msix'
[void](Add-MsixFileChecks -Result $r -Workspace $Workspace -RequireSigned)
$hit = foreach ($p in Get-NewPackages -BaselineFullNames @($Baseline.packages)) {
    $loc = $p.InstallLocation
    $isElectron = $loc -and (@(Get-ChildItem -LiteralPath $loc -Recurse -File -Include 'app.asar', 'package.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\resources\\(app\.asar$|app\\package\.json$)' }).Count -gt 0)
    if ($isElectron) {
        $m = Get-InstalledManifest $p
        [pscustomobject]@{ Package = $p; Manifest = $m; App = (Get-ManifestApplications $m | Select-Object -First 1) }
    }
}
$hit = @($hit) | Select-Object -First 1
[void](Add-Check $r 'installed' ([bool]$hit) ($(if ($hit) { $hit.Package.PackageFullName } else { 'no new installed package contains an Electron app' })) -Required)
if ($hit -and $hit.App) {
    $aumid = Start-PackagedApp -Package $hit.Package -AppId $hit.App.Id
    $exeName = [IO.Path]::GetFileNameWithoutExtension((Split-Path $hit.App.Executable -Leaf))
    $proc = Wait-AppProcess -Name $exeName -UnderPath $hit.Package.InstallLocation -TimeoutSeconds 40
    [void](Add-Check $r 'launches' ([bool]$proc) "activated $aumid; $(if ($proc) { $proc.Path } else { "no $exeName process from the package" })" -Required)
    Get-Process -Name $exeName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
elseif ($hit) { [void](Add-Check $r 'launches' $false 'installed manifest has no Application' -Required) }
Complete-CheckResult -Result $r -OutFile $OutFile
