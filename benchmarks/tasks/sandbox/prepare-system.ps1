# Runs as SYSTEM inside a fresh Windows Sandbox before the task: makes it look like a typical
# developer machine (Developer Mode on, tools on the machine PATH, scripts allowed).
$ErrorActionPreference = 'Stop'
Start-Transcript -Path 'C:\bench\out\prepare-system.log' -Force | Out-Null
try {
$unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
New-Item -Path $unlock -Force | Out-Null
Set-ItemProperty -Path $unlock -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
# Windows PowerShell's machine policy (PowerShell 7's lives in its read-only install folder).
$psKey = 'HKLM:\SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell'
New-Item -Path $psKey -Force | Out-Null
Set-ItemProperty -Path $psKey -Name ExecutionPolicy -Value RemoteSigned

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$add = 'C:\Program Files\WinAppCli;C:\Program Files\dotnet;C:\Program Files\nodejs;C:\Program Files\Git\cmd;C:\Program Files\PowerShell\7'
if ($machinePath -notlike "*WinAppCli*") { [Environment]::SetEnvironmentVariable('Path', "$add;$machinePath", 'Machine') }
foreach ($kv in @{ DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'; WINAPP_CLI_TELEMETRY_OPTOUT = '1'; WINAPP_CLI_UPDATE_CHECK = '0' }.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, 'Machine')
}
New-Item -ItemType Directory -Force 'C:\src', 'C:\bench-state', 'C:\bench-home' | Out-Null
if (Test-Path 'C:\bench\harness\vcruntime') {
    # The Visual C++ runtime DLLs, as a developer machine has them (Electron's installer needs them).
    Copy-Item 'C:\bench\harness\vcruntime\*.dll' "$env:SystemRoot\System32" -Force
}
}
catch { Write-Output "ERROR: $($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)"; Stop-Transcript | Out-Null; exit 1 }
Stop-Transcript | Out-Null
