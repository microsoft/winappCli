param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'exec-alias'
$hit = Add-InstalledAppChecks -Result $r -Baseline $Baseline -ExeName 'ContosoNotes.exe' -SkipLaunch
$aliases = if ($hit) { @(Get-ManifestExecutionAliases $hit.Manifest) } else { @() }
[void](Add-Check $r 'alias-declared' (@($aliases | Where-Object { $_ -ieq 'contoso-notes.exe' }).Count -gt 0) "aliases: [$($aliases -join ', ')]" -Required)
$out = & cmd.exe /d /c "contoso-notes --version 2>&1"
$text = ($out | Out-String).Trim()
[void](Add-Check $r 'alias-runs' ($text -match 'ContosoNotes 1\.2\.0') "output: $text" -Required)
Complete-CheckResult -Result $r -OutFile $OutFile
