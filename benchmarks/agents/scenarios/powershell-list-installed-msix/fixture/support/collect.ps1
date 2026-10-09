# Collects diagnostics for support tickets.
$out = Join-Path $env:TEMP 'support'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ComputerInfo | Out-File (Join-Path $out 'computer.txt')
# TODO: installed packages
