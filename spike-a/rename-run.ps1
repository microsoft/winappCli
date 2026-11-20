param([ValidateSet('A','Anowd','B')][string]$Mode, [int]$Runs = 1, [int]$GapMs = 400, [int]$First = 1)
$ErrorActionPreference = 'Stop'
$env:PATH = "C:\dotnet11;$env:PATH"; $env:DOTNET_ROOT = 'C:\dotnet11'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'; $env:NUGET_PACKAGES = 'C:\hr-spike-a\nuget'
$sol = 'C:\hr-spike-a\dw\sol'; $ui = 'C:\hr-spike-a\cli\winapp.exe'
$winapp = if ($Mode -eq 'B') { 'C:\hr-probe-b\cli\winapp.exe' } else { 'C:\hr-spike-a\cli\winapp.exe' }
if ($Mode -eq 'B') { $env:WINAPP_HOT_RELOAD_PIPELINE = 'compiler'; $env:WINAPP_HOT_RELOAD_PAGE_UPDATE = 'recreate' }
$env:WINAPP_XAML_WATCHDOG_OFF = if ($Mode -eq 'Anowd') { '1' } else { $null }
function Ticks { $p = Get-Process Probe -ErrorAction SilentlyContinue | Sort StartTime -Descending | Select -First 1; if (-not $p) { return $null }
  $v = (& $ui ui get-value UptimeText -a $p.Id 2>&1 | Out-String); if ($v -match 'ticks=(\d+) pid=(\d+)') { [pscustomobject]@{ Ticks = [int]$Matches[1]; Pid = [int]$Matches[2] } } }
function Moving { $a = Ticks; Start-Sleep 2; $b = Ticks; [pscustomobject]@{ Moving = ($a -and $b -and $b.Ticks -gt $a.Ticks -and $a.Pid -eq $b.Pid); Pid = $b.Pid; From = $a.Ticks; To = $b.Ticks } }
& C:\hr-spike-a\lock.ps1 take
$results = @()
try {
  for ($i = $First; $i -lt $First + $Runs; $i++) {
    Set-Location $sol; git checkout -q -f main; git clean -fdq
    $dir = "C:\hr-spike-a\runs\rename\$Mode-$i"; Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $dir | Out-Null
    $env:PROBE_LOG_DIR = "$dir\applog"; $env:WINAPP_XAML_SIDECAR_LOG = "$dir\sidecar.log"; $env:WINAPP_HOT_RELOAD_LOG = "$dir\hotreload.log"
    $p = Start-Process $winapp -ArgumentList @('run', '.', '--watch') -WorkingDirectory "$sol\App" -RedirectStandardOutput "$dir\out.log" -RedirectStandardError "$dir\err.log" -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(300); while ((Get-Date) -lt $deadline -and -not (Test-Path "$dir\applog\state.log" -PathType Leaf) -or ((Get-Content "$dir\applog\state.log" -Raw -ErrorAction SilentlyContinue) -notmatch 'app=a0')) { if ((Get-Date) -gt $deadline) { break }; Start-Sleep 1 }
    Start-Sleep 5
    $before = Moving
    $files = @("$sol\App\Rename\Tagger.cs") + ('A','B','C','D','E' | ForEach-Object { "$sol\App\Rename\Ren$_.cs" })
    $t0 = Get-Date
    foreach ($f in $files) { (Get-Content $f -Raw).Replace('Tag()', 'Mark()') | Set-Content $f -NoNewline; Start-Sleep -Milliseconds $GapMs }
    Start-Sleep 8; $at10 = Moving
    $deadline = (Get-Date).AddSeconds(75); do { Start-Sleep 3; $end = Moving } while (-not $end.Moving -and (Get-Date) -lt $deadline)
    $recovered = $end.Moving; $recoverS = [int]((Get-Date) - $t0).TotalSeconds
    $log = (Get-Content "$dir\out.log" -Raw -ErrorAction SilentlyContinue) + (Get-Content "$dir\err.log" -Raw -ErrorAction SilentlyContinue)
    $r = [pscustomobject]@{ Mode = $Mode; Run = $i; MovingBefore = $before.Moving; FrozenAt10s = -not $at10.Moving; Detected = $log -match 'stopped responding'; Relaunched = $log -match 'relaunching'
      RecoveredBy75s = $recovered; SecondsToMoving = $(if ($recovered) { $recoverS } else { $null }); PidBefore = $before.Pid; PidAfter = $end.Pid
      Applied = ([regex]::Matches($log, 'changes applied|\[watch\] applied')).Count; Restarting = ([regex]::Matches($log, 'Restarting application|The app restarted')).Count }
    $r | ConvertTo-Json | Set-Content "$dir\result.json"; $results += $r; $r | Format-List | Out-String -Width 200
    foreach ($proc in @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ($_.CommandLine.Contains("$sol\App") -or $_.CommandLine.Contains('dw\sol')) -and $_.ProcessId -ne $PID })) { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue }
    if (-not $p.HasExited) { & taskkill /PID $p.Id /T /F 2>&1 | Out-Null }
    foreach ($t in @(Get-Process Probe -ErrorAction SilentlyContinue)) { if ($t.Path -like 'C:\hr-spike-a\*') { Stop-Process -Id ([int]$t.Id) -Force } }
    Start-Sleep 2
  }
} finally { & C:\hr-spike-a\lock.ps1 release; Set-Location $sol; git checkout -q -f main; git clean -fdq }
$results | Format-Table Mode, Run, MovingBefore, FrozenAt10s, Detected, Relaunched, RecoveredBy75s, SecondsToMoving, PidBefore, PidAfter, Applied, Restarting -AutoSize | Out-String -Width 220
