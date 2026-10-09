param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $Workspace)
winapp new -t winui -n NoteTaker -o $Workspace --use-defaults --force 2>&1 | Write-Host
Set-Location $Workspace
dotnet build 2>&1 | Write-Host
