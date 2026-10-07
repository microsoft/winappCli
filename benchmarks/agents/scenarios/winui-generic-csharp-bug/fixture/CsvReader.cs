namespace ContosoDesk;

public static class CsvReader
{
    public static string[] ParseLine(string line) => line.Split(',');
}
