$ErrorActionPreference = 'Stop'
$env:PATH = "C:\dotnet11;$env:PATH"; $env:DOTNET_ROOT = 'C:\dotnet11'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'; $env:NUGET_PACKAGES = 'C:\hr-spike-a\nuget'
$app = 'C:\hr-spike-a\reactor\samples\TodoApp'; $out = 'C:\hr-spike-a\runs\reactor'; New-Item -ItemType Directory $out -Force | Out-Null
$prog = "$app\Program.cs"; Copy-Item $prog "$out\Program.cs.orig" -Force
& C:\hr-spike-a\lock.ps1 take
try {
  $p = Start-Process C:\hr-spike-a\cli\winapp.exe -ArgumentList @('run', $app, '--watch') -WorkingDirectory $app -RedirectStandardOutput "$out\out.log" -RedirectStandardError "$out\err.log" -PassThru -WindowStyle Hidden
  $deadline = (Get-Date).AddMinutes(8)
  while ((Get-Date) -lt $deadline -and -not ((Get-Content "$out\out.log" -Raw -ErrorAction SilentlyContinue) -match 'DevTools attached')) { Start-Sleep 1 }
  "attached: $((Get-Content "$out\out.log" -Raw) -match 'DevTools attached')"
  Start-Sleep 3
  $sidecars = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -like '*WinApp.XamlSidecar*' -and $_.CommandLine -like '*TodoApp*' })
  "side-car processes for TodoApp: $($sidecars.Count)"
  $before = @(Select-String -Path "$out\err.log" -Pattern 'changes applied').Count
  (Get-Content $prog -Raw).Replace('"No tasks"', '"No tasks yet"') | Set-Content $prog -NoNewline
  Start-Sleep 45
  "C# applied by dotnet watch: $(@(Select-String -Path "$out\err.log" -Pattern 'changes applied').Count -gt $before)"
  "[watch] lines: $(@(Select-String -Path "$out\out.log" -Pattern '^\[watch\]').Count)"
  Get-Content "$out\err.log" | Select -Last 4
} finally {
  Copy-Item "$out\Program.cs.orig" $prog -Force
  foreach ($proc in @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains('reactor\samples\TodoApp') -and $_.ProcessId -ne $PID })) { Stop-Process -Id ([int]$proc.ProcessId) -Force -ErrorAction SilentlyContinue }
  foreach ($t in @(Get-Process TodoApp -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'C:\hr-spike-a\*' })) { Stop-Process -Id ([int]$t.Id) -Force }
  & C:\hr-spike-a\lock.ps1 release
}

