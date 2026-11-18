# Reference solution: sparse debug identity for the built exe.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
dotnet build -c Debug 2>&1 | Write-Host
$exe = Get-ChildItem -Recurse -Filter PingNotifier.exe -Path bin | Select-Object -First 1
winapp manifest generate . 2>&1 | Write-Host
winapp create-debug-identity $exe.FullName 2>&1 | Write-Host
