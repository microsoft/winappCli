# Starts Order Calculator on the desktop. Its total uses tax and shipping picked at launch, so it can only be read from the UI.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Copy-Item 'C:\bench\harness\apps\OrderCalculator' 'C:\Tools\OrderCalculator' -Recurse -Force
Remove-Item 'C:\ProgramData\OrderCalculator' -Recurse -Force -ErrorAction SilentlyContinue
Start-Process 'C:\Tools\OrderCalculator\OrderCalculator.exe'
$deadline = (Get-Date).AddSeconds(30)
while (-not (Get-Process OrderCalculator -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
