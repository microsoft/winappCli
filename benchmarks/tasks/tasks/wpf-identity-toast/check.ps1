param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'wpf-identity-toast'
$diagPath = 'C:\ProgramData\PingNotifier\diagnostics.json'
$hits = @(Find-NewPackageForExe -BaselineFullNames @($Baseline.packages) -ExeName 'PingNotifier.exe')
[void](Add-Check $r 'identity-registered' ([bool]$hits) ($(if ($hits) { $hits.Package.PackageFullName -join ', ' } else { 'no new package runs PingNotifier.exe' })) -Required)

function Invoke-Launch([scriptblock]$Start, [string]$How) {
    Get-Process PingNotifier -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $diagPath -ErrorAction SilentlyContinue
    & $Start
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path $diagPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
    Get-Process PingNotifier -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not (Test-Path $diagPath)) { return [pscustomobject]@{ how = $How; hasIdentity = $false; toastShown = $false; note = 'no diagnostics written' } }
    $d = Get-Content -Raw $diagPath | ConvertFrom-Json
    $d | Add-Member how $How -Force
    return $d
}

$launches = [System.Collections.Generic.List[object]]::new()
foreach ($h in $hits) { $launches.Add((Invoke-Launch { [void](Start-PackagedApp -Package $h.Package -AppId $h.App.Id) } "activate $($h.Package.PackageFamilyName)!$($h.App.Id)")) }
$exe = Find-WorkspaceFile -Root $Workspace -Include 'PingNotifier.exe' | Select-Object -First 1
if ($exe -and -not @($launches | Where-Object { $_.hasIdentity -and $_.toastShown })) { $launches.Add((Invoke-Launch { Start-Process $exe.FullName } "run $($exe.FullName)")) }
$r.facts.launches = @($launches)
if (-not $launches.Count) { $launches.Add([pscustomobject]@{ how = 'nothing to launch'; hasIdentity = $false; toastShown = $false; note = 'no registered package and no built PingNotifier.exe' }) }
$best = @($launches | Where-Object { $_.hasIdentity }) | Select-Object -First 1
[void](Add-Check $r 'has-identity' ([bool]$best) ($(if ($best) { "$($best.how): $($best.packageFamilyName)" } else { (@($launches | ForEach-Object { "$($_.how): $($_.identityError)$($_.note)" }) -join '; ') })) -Required)
$toast = @($launches | Where-Object { $_.hasIdentity -and $_.toastShown }) | Select-Object -First 1
[void](Add-Check $r 'toast-shown' ([bool]$toast) ($(if ($toast) { $toast.how } else { (@($launches | ForEach-Object { "$($_.how): $($_.toastError)" }) -join '; ') })) -Required)
Complete-CheckResult -Result $r -OutFile $OutFile
