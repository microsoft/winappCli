param([Parameter(Mandatory)][string]$Journey, [string]$Parity = 'Off', [int]$MaxActions = 0, [string]$Packaging = 'Unpackaged', [string]$AppSource, [switch]$KeepRunning, [string]$Tag = '', [ValidateSet('11','10')][string]$Sdk = '11', [string[]]$ExtraProp = @())
$ErrorActionPreference = 'Stop'
if ($Sdk -eq '11') { $env:PATH = "C:\dotnet11;$env:PATH"; $env:DOTNET_ROOT = 'C:\dotnet11'; $env:DOTNET_MULTILEVEL_LOOKUP = '0' } else { $Tag += '-sdk10'; $env:DOTNET_ROOT = $null }
$env:NUGET_PACKAGES = 'C:\hr-spike-a\nuget'
$out = "C:\hr-spike-a\runs\$Journey$Tag"
New-Item -ItemType Directory $out -Force | Out-Null
$stamp = Get-Date -Format 'HHmmss'
$env:WINAPP_XAML_SIDECAR_LOG = "$out\sidecar-$stamp.log"
$env:WINAPP_XAML_AGENT_LOG = "$out\agent-$stamp.log"
& C:\hr-spike-a\lock.ps1 take
try {
    $extra = @{}
    if ($AppSource) { $extra.AppSource = $AppSource }
    if ($MaxActions) { $extra.MaxActions = $MaxActions }
    if ($KeepRunning) { $extra.KeepRunning = $true }
    & C:\hr-spike-a\runner\test\benchmarks\watch-hot-reload\Run-WatchJourney.ps1 -JourneyName $Journey -Winapp C:\hr-spike-a\cli\winapp.exe -OutputRoot $out `
        -WinUIPackageSource C:\hr-shared\winui\int-63c7904af -Parity $Parity -ParityAcceptReported -NoDesktopLock -Packaging $Packaging -SaveTimeoutSeconds 90 `
        -BuildProperty (@('EnableHotReloadStableConnectionIds=true') + $ExtraProp) @extra
}
finally {
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($out) -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    & C:\hr-spike-a\lock.ps1 release
}




