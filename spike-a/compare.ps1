param([string]$Journey, [string]$Run, [string]$Cell)
$b = Import-Csv C:\hr-shared\n1-baseline\saves.csv | Where-Object { $_.Sdk -eq 'sdk11' -and $_.Cell -eq $Cell }
$a = Get-Content (Join-Path $Run 'default\results.json') -Raw | ConvertFrom-Json | Where-Object { $_.Index -gt 0 }
foreach ($row in $a) {
    $br = $b | Where-Object Action -eq $row.Action | Select-Object -First 1
    [pscustomobject]@{ Journey = $Journey; Save = $row.Action; Kind = $row.Kind; A = $row.Outcome; AWall = $row.WallMs; ASame = $row.SameProcess
        AChecksFailed = @($row.Checks | Where-Object { -not $_.Ok }).Count; B = $br.Outcome; BWall = $br.WallMs; BSame = $br.SameProcess; BChecksFailed = $br.ChecksFailed }
}
