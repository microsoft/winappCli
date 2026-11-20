$ErrorActionPreference = 'Stop'
$env:PATH = "C:\dotnet11;$env:PATH"; $env:DOTNET_ROOT = 'C:\dotnet11'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'; $env:NUGET_PACKAGES = 'C:\hr-spike-a\nuget'
$env:WINAPP_XAML_SIDECAR_OFF = '1'
Import-Module C:\hr-spike-a\runner\test\benchmarks\watch-hot-reload\WatchJourney.psm1 -Force
$root = 'C:\hr-spike-a\runs\plain-1197-xaml-then-rude'; Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
$app = "$root\app"; New-Item -ItemType Directory $app -Force | Out-Null
Copy-Item C:\hr-spike-a\runner\test\benchmarks\watch-hot-reload\app-template\* $app; Copy-Item C:\hr-spike-a\warm\Directory.Build.* $root; Copy-Item C:\hr-spike-a\warm\nuget.config $root
$j = Get-Content C:\hr-spike-a\runner\test\benchmarks\watch-hot-reload\journeys\rude-restart.json -Raw | ConvertFrom-Json
foreach ($s in $j.setup) { Invoke-JourneyEdit -AppDir $app -Action $s }
& C:\hr-spike-a\lock.ps1 take
try {
  $p = Start-Process C:\hr-spike-a\cli\winapp.exe -ArgumentList @('run', $app, '--watch') -WorkingDirectory $app -RedirectStandardOutput "$root\out.log" -RedirectStandardError "$root\err.log" -PassThru -WindowStyle Hidden
  $deadline = (Get-Date).AddMinutes(5)
  while ((Get-Date) -lt $deadline -and -not ((Get-Content "$root\out.log" -Raw -ErrorAction SilentlyContinue) -match 'DevTools attached')) { Start-Sleep 1 }
  "attached: $((Get-Content "$root\out.log" -Raw) -match 'DevTools attached')"
  Start-Sleep 3
  Invoke-JourneyEdit -AppDir $app -Action ($j.segments[0].actions[1]); Start-Sleep 5
  Invoke-JourneyEdit -AppDir $app -Action ($j.segments[0].actions[0])
  Start-Sleep 60
  "--- out"; Get-Content "$root\out.log" | Select-String 'attached|restarted|MSB3027|error|Restart' | Select -Last 8 | % Line
  "--- err"; Get-Content "$root\err.log" | Select -Last 8
} finally {
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($root) -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Get-Process TodoBench -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force
  & C:\hr-spike-a\lock.ps1 release
}

