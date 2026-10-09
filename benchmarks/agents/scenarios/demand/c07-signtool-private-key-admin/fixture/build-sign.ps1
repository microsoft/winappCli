param(
  [string]$Configuration = "Release"
)

$signTool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.18362.0\x64\signtool.exe"
$dll = "bin\$Configuration\net48\Orbit.Reporting.dll"
& $signTool sign /v /debug /ph /i "Northwind Internal Code Signing" /fd sha256 /td sha256 /tr http://timestamp.digicert.com $dll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
