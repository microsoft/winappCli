# Reference solution: open the store, read reports-db, and use the decrypted text property, with this
# run's generated names (from the state file setup.ps1 wrote).
param([string]$Workspace, [string]$StatePath = 'C:\bench-state\find-api-secret-store.json')
$ErrorActionPreference = 'Stop'
$s = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
Set-Location $Workspace
@"
using Contoso.Secrets;

namespace ReportExporter;

// Connection details for the reporting database.
public static class Database
{
    public static string ConnectionString()
    {
        using var store = $($s.store).$($s.open)();
        var password = store.$($s.reveal)("reports-db").$($s.plain);
        return `$"Server=reports.contoso.internal;Database=Reports;User Id=reports_app;Password={password};";
    }
}
"@ | Set-Content (Join-Path $Workspace 'Database.cs') -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
