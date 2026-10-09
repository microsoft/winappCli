param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'winforms-msix'
$msix = Add-MsixFileChecks -Result $r -Workspace $Workspace -RequireSigned
[void](Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'TaskBoard.exe' -Msix $msix)
Complete-CheckResult -Result $r -OutFile $OutFile
