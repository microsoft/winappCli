param([ValidateSet('take','release')][string]$Op, [string]$Name = 'spike-a (session f683ce85)')
$lock = 'C:\hr-shared\desktop.lock'
if ($Op -eq 'release') { if ((Test-Path $lock) -and ((Get-Content $lock -Raw) -like "$Name*")) { Remove-Item $lock -Force; Set-Content C:\hr-spike-a\last-release.txt (Get-Date -Format o) }; return }
# After releasing, give other sessions 60 s to take the desktop before taking it again.
if (Test-Path C:\hr-spike-a\last-release.txt) { $since = (Get-Date) - [datetime](Get-Content C:\hr-spike-a\last-release.txt -Raw).Trim(); if ($since.TotalSeconds -lt 60) { Start-Sleep -Seconds ([int](60 - $since.TotalSeconds)) } }
# Paused at the coordinator's request: wait until the "Option B check" session has taken the lock and released it.
if (Test-Path C:\hr-spike-a\pause-for-b.txt) {
    Write-Host "[lock] paused until option-b-check takes and releases the desktop"
    $seen = $false
    while ($true) {
        $owner = if (Test-Path $lock) { (Get-Content $lock -Raw -ErrorAction SilentlyContinue) } else { '' }
        if ("$owner" -like 'option-b-check*') { $seen = $true; $lastSeen = Get-Date }
        elseif ($seen -and ((Get-Date) - $lastSeen).TotalMinutes -ge 3) { Remove-Item C:\hr-spike-a\pause-for-b.txt -Force; Write-Host "[lock] option-b-check released (gone 3 min) at $(Get-Date -Format T); resuming"; break }
        Start-Sleep -Seconds 2
    }
}
while ($true) {
    if (Test-Path $lock) {
        $text = Get-Content $lock -Raw
        $age = (Get-Date) - (Get-Item $lock).LastWriteTime
        if ($text -like "$Name*") { break }
        if ($age.TotalMinutes -lt 45) { if (-not $script:said) { Write-Host "[lock] held by: $($text.Trim()); waiting"; $script:said = $true }; Start-Sleep -Milliseconds 1500; continue }
        Write-Host "[lock] stale ($([int]$age.TotalMinutes) min): taking over from $($text.Trim())"; Remove-Item $lock -Force -ErrorAction SilentlyContinue
    }
    try { $fs = [IO.File]::Open($lock, 'CreateNew'); $b = [Text.Encoding]::UTF8.GetBytes("$Name $(Get-Date -Format s)"); $fs.Write($b, 0, $b.Length); $fs.Dispose(); break } catch { Start-Sleep -Milliseconds 500 }
}
Write-Host "[lock] taken $(Get-Date -Format T)"





