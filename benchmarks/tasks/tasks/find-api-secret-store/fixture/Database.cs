namespace ReportExporter;

// Connection details for the reporting database.
public static class Database
{
    // Development password. It must not ship: release builds read it from the secret store.
    private const string Password = "Dev-Only-P@ss1";

    public static string ConnectionString() =>
        $"Server=reports.contoso.internal;Database=Reports;User Id=reports_app;Password={Password};";
}
