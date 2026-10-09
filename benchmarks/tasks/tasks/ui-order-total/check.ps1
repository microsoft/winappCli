param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'ui-order-total'
$log = 'C:\ProgramData\OrderCalculator\events.jsonl'
$events = if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() }
$calc = @($events | Where-Object { $_.evt -eq 'calculate' -and $_.qty -eq '7' -and [decimal]::TryParse([string]$_.price, [ref]$null) -and [decimal]$_.price -eq 13 -and $_.total }) | Select-Object -Last 1
$r.facts.events = @($events)
[void](Add-Check $r 'calculated' ([bool]$calc) ($(if ($calc) { "app showed $($calc.total)" } else { "calculate events: $(@($events | Where-Object evt -eq 'calculate').Count)" })) -Required)
$file = Join-Path $Workspace 'total.txt'
$saved = if (Test-Path $file) { (Get-Content -Raw $file).Trim() } else { $null }
$num = if ($saved -match '(\d+(?:[.,]\d+)?)') { [decimal]::Parse($Matches[1].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) } else { $null }
$match = $calc -and $null -ne $num -and $num -eq [decimal]::Parse($calc.total, [Globalization.CultureInfo]::InvariantCulture)
[void](Add-Check $r 'total-saved' ([bool]$match) "total.txt: '$saved'" -Required)
Complete-CheckResult -Result $r -OutFile $OutFile
