param([Parameter(Mandatory)][string]$Run)
# Per save: kind, outcome, reported ms, wall ms (save to outcome), visible ms (save to change seen on screen), same process.
$rows = Get-Content (Join-Path $Run 'default\results.json') -Raw | ConvertFrom-Json | Where-Object { $_.Index -gt 0 }
$rows | Select-Object Index, Action, Kind, Outcome, Ms, WallMs, VisibleMs, SameProcess, @{ n = 'ChecksOk'; e = { @($_.Checks | Where-Object { -not $_.Ok }).Count -eq 0 } }
