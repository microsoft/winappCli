using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessLoop;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}

public sealed record Scenario
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string TargetId { get; init; }
    public required string ExpectedName { get; init; }
    public required string AnchorId { get; init; }
    public required string ResultId { get; init; }
    public required string InputText { get; init; }
    public required string ExpectedResult { get; init; }
    public int MaxTabs { get; init; } = 5;
    public int TimeoutSeconds { get; init; } = 15;

    public static Scenario Load(string path) => Load(path, File.ReadAllBytes(path));

    public static Scenario Load(string path, ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("\uFEFF"u8))
            content = content[3..];
        var scenario = JsonSerializer.Deserialize<Scenario>(content, Json.Options)
            ?? throw new InvalidDataException("The scenario is empty.");
        scenario.Validate();
        return scenario with
        {
            Project = Path.GetFullPath(scenario.Project, Path.GetDirectoryName(Path.GetFullPath(path))!)
        };
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
            throw new InvalidDataException("Only scenario schemaVersion 1 is supported.");
        foreach (var (name, value) in new[]
        {
            (nameof(Name), Name), (nameof(Project), Project), (nameof(TargetId), TargetId),
            (nameof(ExpectedName), ExpectedName), (nameof(AnchorId), AnchorId),
            (nameof(ResultId), ResultId), (nameof(InputText), InputText),
            (nameof(ExpectedResult), ExpectedResult)
        })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Scenario {name} must not be empty.");
        }
        if (new[] { AnchorId, TargetId, ResultId }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Anchor, target, and result must have distinct AutomationIds.");
        if (MaxTabs is < 1 or > 30 || TimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("maxTabs must be 1..30 and timeoutSeconds 1..120.");
        if (!Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This sample supports a single XAML .csproj.");
    }
}

public sealed record CheckResult(string RuleId, bool Passed, string Expected, string? Actual);
public sealed record FocusObservation(int Tab, string? AutomationId, string? Name);

public sealed record Observation
{
    public required string Phase { get; init; }
    public required string Status { get; init; }
    public int? ProcessId { get; init; }
    public string? Error { get; init; }
    public AccessibilityScan? Scan { get; init; }
    public List<CheckResult> Checks { get; init; } = [];
    public List<FocusObservation> FocusTrace { get; init; } = [];
    public IReadOnlyList<string> VerifiedTargets { get; init; } = [];
}

public sealed record RunReport
{
    public int SchemaVersion { get; init; } = 3;
    public string RuleVersion { get; init; } = "axe-windows-workbench/3";
    public required string SuiteName { get; init; }
    public required string InputPath { get; init; }
    public required IReadOnlyList<InputSnapshot> Inputs { get; init; }
    public required string WinappVersion { get; init; }
    public required string Status { get; set; }
    public string SuiteStatus { get; set; } = "inconclusive";
    public IReadOnlyList<ScenarioResult> Before { get; set; } = [];
    public IReadOnlyList<ScenarioResult> After { get; set; } = [];
    public IReadOnlyList<WorkbenchFinding> Findings { get; set; } = [];
    public IReadOnlyList<WorkbenchFinding> RemainingFindings { get; set; } = [];
    public string? SelectedFindingId { get; set; }
    public IReadOnlyList<string> VerificationErrors { get; set; } = [];
    public string? Error { get; set; }
    public string? RepairSource { get; set; }
    public bool PatchApplied { get; set; }
    public bool PatchRolledBack { get; set; }
}

public sealed record InputSnapshot(string Path, string Sha256, string EvidencePath);
public sealed record ScenarioResult(Scenario Scenario, string EvidencePath, Observation Observation);
public sealed record FindingOccurrence(string ScenarioName, AccessibilityFinding Finding);

public sealed record WorkbenchFinding(
    string Id, string Project, string RuleId, string? AutomationId,
    bool StableIdentity, IReadOnlyList<FindingOccurrence> Occurrences)
{
    public bool Repairable { get; init; }
    public string? RepairReason { get; init; }
    public string? OwnerScenarioName { get; init; }
}

public sealed record RegressionResult(
    bool Accepted, string Status, IReadOnlyList<string> Errors,
    IReadOnlyList<WorkbenchFinding> RemainingFindings);
