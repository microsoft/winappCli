param([string[]]$Runs)
function P([double[]]$xs, [double]$q) { $s = @($xs | Sort-Object); if (-not $s) { return $null }; $s[[int][math]::Floor(($s.Count - 1) * $q)] }
foreach ($run in $Runs) {
    $sum = Get-Content (Join-Path $run 'summary.json') -Raw | ConvertFrom-Json | Select-Object -First 1
    $rows = @(Get-Content (Join-Path $run 'default\results.json') -Raw | ConvertFrom-Json | Where-Object { $_.Index -gt 0 })
    $x = @($rows | Where-Object { $_.Kind -eq 'xaml' -and $_.Outcome -eq 'applied' })
    $outcomes = ($rows | Group-Object Outcome | ForEach-Object { "$($_.Name) $($_.Count)" }) -join ', '
    [pscustomobject]@{
        Journey = Split-Path (Split-Path $run) -Leaf; Saves = $sum.Saves; Outcomes = $outcomes; SameProcess = $sum.SameProcess
        Checks = "$($sum.ChecksPassed)/$($sum.Checks)"; Parity = $sum.Parity; XamlN = $x.Count
        XamlWallP50 = P $x.WallMs 0.5; XamlWallP90 = P $x.WallMs 0.9; Run = $run
    }
}
