using ReportExporter;

var connection = Database.ConnectionString();
Console.WriteLine($"Exporting the monthly report ({connection.Split(';')[0]})");
