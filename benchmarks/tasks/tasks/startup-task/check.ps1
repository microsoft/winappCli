param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'startup-task'
$hit = Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'SyncTray.exe'
$tasks = if ($hit) { @(Get-ManifestStartupTasks $hit.Manifest) } else { @() }
$ok = @($tasks | Where-Object { $_.Enabled -eq 'true' -and $_.Category -eq 'windows.startupTask' -and (-not $_.Executable -or (Split-Path $_.Executable -Leaf) -ieq 'SyncTray.exe') })
[void](Add-Check $r 'startup-task-declared' ([bool]$ok) "startup tasks: $(($tasks | ForEach-Object { "$($_.TaskId) enabled=$($_.Enabled) exe=$($_.Executable)" }) -join '; ')" -Required)
foreach ($c in $r.checks) { if ($c.id -eq 'launches') { $c.required = $false } }
Complete-CheckResult -Result $r -OutFile $OutFile
