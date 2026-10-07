var lines = File.ReadAllLines(args[0]);
foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
{
    Console.WriteLine(line.Trim());
}
