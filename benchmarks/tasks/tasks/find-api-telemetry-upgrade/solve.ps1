# Reference solution: rename the client type, the event method and the flush to this run's 2.0 names
# (from the state file setup.ps1 wrote), and await the now-async flush.
param([string]$Workspace, [string]$StatePath = 'C:\bench-state\find-api-telemetry-upgrade.json')
$ErrorActionPreference = 'Stop'
$s = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
Set-Location $Workspace
$file = Join-Path $Workspace 'AppTelemetry.cs'
(Get-Content -Raw $file).
    Replace('new TelemetryClient(', "new $($s.client)(").
    Replace('client.TrackEvent(', "client.$($s.event)(").
    Replace('client.Flush();', "client.$($s.flush)().GetAwaiter().GetResult();") |
    Set-Content $file -NoNewline -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
