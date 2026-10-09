param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
winapp ui set-value QuantityBox 7 -a OrderCalculator 2>&1 | Write-Host
winapp ui set-value UnitPriceBox 13 -a OrderCalculator 2>&1 | Write-Host
winapp ui invoke CalculateButton -a OrderCalculator 2>&1 | Write-Host
Start-Sleep -Seconds 1
$v = winapp ui get-value TotalValue -a OrderCalculator 2>&1 | Out-String
Write-Host $v
if ($v -match '(\d+\.\d{2})') { Set-Content total.txt $Matches[1] } else { throw "could not read total: $v" }
