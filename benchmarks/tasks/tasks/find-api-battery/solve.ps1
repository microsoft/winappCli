# Reference solution: target a Windows 10 SDK framework so the WinRT projection is referenced, then
# read the aggregate battery report and divide remaining by full-charge capacity.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
$csproj = Join-Path $Workspace 'PowerInfo.csproj'
(Get-Content -Raw $csproj) -replace '<TargetFramework>net10\.0-windows</TargetFramework>', '<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>' |
    Set-Content $csproj -NoNewline -Encoding utf8NoBOM
@'
using Windows.Devices.Power;

namespace PowerInfo;

// Power details shown by the status command.
public static class PowerStatus
{
    public static string MachineName() => Environment.MachineName;

    // Charge as a percentage, or null when the machine has no battery.
    public static double? GetBatteryPercent()
    {
        BatteryReport report = Battery.AggregateBattery.GetReport();
        int? remaining = report.RemainingCapacityInMilliwattHours;
        int? full = report.FullChargeCapacityInMilliwattHours;
        if (remaining is null || full is null or 0) return null;
        return remaining.Value * 100.0 / full.Value;
    }
}
'@ | Set-Content (Join-Path $Workspace 'Battery.cs') -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
