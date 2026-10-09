dotnet publish .\Catlists.sln `
  -f net8.0-windows10.0.19041.0 `
  -c Release `
  -p:RuntimeIdentifierOverride=win10-x64 `
  -p:PackageCertificateThumbprint=$env:WIN_CERT_THUMBPRINT

dotnet publish .\src\Catlists\Catlists.csproj `
  -f net8.0-windows10.0.19041.0 `
  -c Release `
  -p:RuntimeIdentifierOverride=win-x64 `
  -p:Platform=x64 `
  -p:WindowsAppSDKSelfContained=true `
  -p:PublishSingleFile=true
