# Runs inside Windows Sandbox after the agent finishes: the task's checker plus a state diff
# against the baseline (certificates, security settings, removed packages).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$out = 'C:\bench\out'
$run = Get-Content -Raw 'C:\bench\in\run.json' | ConvertFrom-Json
Import-Module 'C:\bench\harness\lib\TaskCheck.psm1' -Force
$env:PATH = "C:\Program Files\WinAppCli;C:\Program Files\dotnet;C:\Program Files\nodejs;C:\Program Files\Git\cmd;C:\Program Files\PowerShell\7;$env:LOCALAPPDATA\Microsoft\WindowsApps;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
$env:WINAPP_CLI_UPDATE_CHECK = '0'

$baseline = Get-Content -Raw 'C:\bench-state\baseline.json' | ConvertFrom-Json
$state = [ordered]@{ error = $null }
try {
    $certs = Compare-CertificateSnapshot -Before @($baseline.certificates) -After @(Get-CertificateSnapshot)
    $packagesNow = @(Get-InstalledPackages)
    $ws = $run.workspace
    $filesNow = if (Test-Path $ws) { @(Get-ChildItem -LiteralPath $ws -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($ws.Length + 1) }) } else { @() }
    $state.certificatesAdded = @($certs.Added)
    $state.certificatesRemoved = @($certs.Removed)
    $state.securityBefore = $baseline.security
    $state.securityAfter = Get-SecuritySettings
    $state.packagesAdded = @($packagesNow | Where-Object { $_.PackageFullName -notin @($baseline.packages) } | ForEach-Object PackageFullName)
    $state.packagesRemoved = @(@($baseline.packages) | Where-Object { $_ -notin @($packagesNow.PackageFullName) })
    $state.fixtureFilesDeleted = @(@($baseline.files) | Where-Object { $_ -notin $filesNow })
}
catch { $state.error = $_.Exception.Message }
$state | ConvertTo-Json -Depth 6 | Set-Content "$out\state.json" -Encoding utf8NoBOM

try {
    & 'C:\bench\check\check.ps1' -Workspace $run.workspace -Baseline $baseline -OutFile "$out\check.json" *>&1 |
        Out-File "$out\check.log" -Encoding utf8NoBOM
}
catch {
    [ordered]@{ task = $run.task; status = 'checker_error'; why = "$($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)"; checks = @() } |
        ConvertTo-Json -Depth 5 | Set-Content "$out\check.json" -Encoding utf8NoBOM
}
