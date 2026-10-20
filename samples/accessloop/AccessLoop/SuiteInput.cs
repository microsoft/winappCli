using System.Security.Cryptography;
using System.Text.Json;

namespace AccessLoop;

public sealed record ScenarioCase(string Path, Scenario Scenario);

public sealed class SuiteInput
{
    private readonly IReadOnlyDictionary<string, byte[]> originals;
    public string Name { get; }
    public string InputPath { get; }
    public IReadOnlyList<ScenarioCase> Cases { get; }

    private SuiteInput(string name, string inputPath, IReadOnlyList<ScenarioCase> cases,
        IReadOnlyDictionary<string, byte[]> originals)
    {
        Name = name;
        InputPath = inputPath;
        Cases = cases;
        this.originals = originals;
    }

    public static SuiteInput Load(string inputPath)
    {
        var path = System.IO.Path.GetFullPath(inputPath);
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] Read(string file)
        {
            if (originals.ContainsKey(file))
                throw new InvalidDataException($"An input file is repeated in the suite: {file}");
            var bytes = File.ReadAllBytes(file);
            originals.Add(file, bytes);
            return bytes;
        }
        var content = Read(path);
        var json = content.AsMemory();
        if (content.AsSpan().StartsWith("\uFEFF"u8))
            json = json[3..];
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The input must be a scenario or suite JSON object.");
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Duplicate input JSON properties are not allowed.");
        var cases = new List<ScenarioCase>();
        string name;
        if (names.Contains("scenarios", StringComparer.OrdinalIgnoreCase))
        {
            var suite = JsonSerializer.Deserialize<SuiteDefinition>(json.Span, Json.Options)
                ?? throw new InvalidDataException("The suite is empty.");
            if (suite.SchemaVersion != 1 || string.IsNullOrWhiteSpace(suite.Name)
                || suite.Scenarios is null || suite.Scenarios.Count is < 1 or > 20)
                throw new InvalidDataException("A suite requires schemaVersion 1, a name, and 1..20 scenario file paths.");
            name = suite.Name;
            foreach (var item in suite.Scenarios)
            {
                if (string.IsNullOrWhiteSpace(item))
                    throw new InvalidDataException("Scenario file paths must not be empty.");
                var casePath = System.IO.Path.GetFullPath(item, System.IO.Path.GetDirectoryName(path)!);
                cases.Add(new(casePath, Scenario.Load(casePath, Read(casePath))));
            }
        }
        else
        {
            var scenario = Scenario.Load(path, content);
            name = scenario.Name;
            cases.Add(new(path, scenario));
        }
        if (cases.Select(c => c.Scenario.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cases.Count)
            throw new InvalidDataException("Scenario names must be unique within a suite.");
        foreach (var item in cases)
        {
            if (!File.Exists(item.Scenario.Project))
                throw new FileNotFoundException("Scenario project does not exist.", item.Scenario.Project);
        }
        return new SuiteInput(name, path, cases, originals);
    }

    public IReadOnlyList<InputSnapshot> SaveSnapshots(string output)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(output, "inputs"));
        return originals.Select((entry, index) =>
        {
            var relative = System.IO.Path.Combine("inputs", $"{index + 1:D2}.json");
            File.WriteAllBytes(System.IO.Path.Combine(output, relative), entry.Value);
            return new InputSnapshot(entry.Key, Convert.ToHexString(SHA256.HashData(entry.Value)), relative);
        }).ToArray();
    }

    public void EnsureUnchanged()
    {
        foreach (var entry in originals)
        {
            if (!File.ReadAllBytes(entry.Key).AsSpan().SequenceEqual(entry.Value))
                throw new InvalidOperationException($"Input changed during the run: {entry.Key}. Verification cannot be accepted.");
        }
    }

    private sealed record SuiteDefinition
    {
        public int SchemaVersion { get; init; } = 1;
        public required string Name { get; init; }
        public required List<string> Scenarios { get; init; }
    }
}
