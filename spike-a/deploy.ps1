$ErrorActionPreference = 'Stop'
Set-Location C:\Users\nikolame\source\copilot-worktrees\winappcli\nmetulev-improved-train
$o1 = dotnet build src\winapp-devtools\sidecar\WinApp.XamlSidecar\WinApp.XamlSidecar.csproj -c Debug -o C:\hr-spike-a\sidecar -v q -nologo 2>&1
if ($LASTEXITCODE) { $o1 | Select-String 'error' | Select -First 5; throw 'side-car build failed' }
$o2 = dotnet build src\winapp-devtools\agent\WinApp.DevTools.Managed\WinApp.DevTools.Managed.csproj -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE) { $o2 | Select-String ' error ' | Select -First 5; throw 'agent build failed' }
Copy-Item C:\hr-spike-a\sidecar\* C:\hr-spike-a\cli\xaml-sidecar -Force -Recurse
Copy-Item src\winapp-devtools\agent\WinApp.DevTools.Managed\bin\Debug\net8.0-windows10.0.19041.0\win-x64\WinApp.DevTools.Managed.dll C:\hr-spike-a\cli -Force
"deployed $(Get-Date -Format T)"
