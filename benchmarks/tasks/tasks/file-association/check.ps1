param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'file-association'
$hit = Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'NoteViewer.exe'
$types = if ($hit) { @(Get-ManifestFileTypes $hit.Manifest) } else { @() }
[void](Add-Check $r 'file-type-declared' ('.ctnote' -in $types) "file types: [$($types -join ', ')]" -Required)
$progids = @((Get-Item 'HKCU:\Software\Classes\.ctnote\OpenWithProgids' -ErrorAction SilentlyContinue)?.GetValueNames() | Where-Object { $_ -like 'AppX*' })
[void](Add-Check $r 'file-type-registered' ([bool]$progids) "AppX ProgIDs: [$($progids -join ', ')]" -Required)
# 'launches' is informative here; the association is the point of the task.
foreach ($c in $r.checks) { if ($c.id -eq 'launches') { $c.required = $false } }
Complete-CheckResult -Result $r -OutFile $OutFile
