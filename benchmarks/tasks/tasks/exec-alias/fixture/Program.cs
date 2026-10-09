var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContosoNotes", "notes.txt");

switch (args.FirstOrDefault())
{
    case "--version":
        Console.WriteLine("ContosoNotes 1.2.0");
        break;
    case "add" when args.Length > 1:
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.AppendAllLines(store, [string.Join(' ', args.Skip(1))]);
        Console.WriteLine("Saved.");
        break;
    case "list":
        if (File.Exists(store)) foreach (var line in File.ReadLines(store)) Console.WriteLine($"- {line}");
        break;
    default:
        Console.WriteLine("usage: contoso-notes [--version | add <text> | list]");
        break;
}
