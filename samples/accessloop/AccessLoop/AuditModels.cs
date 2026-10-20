using System.Text.Json;

namespace AccessLoop;

public sealed record AuditTarget(
    int ProcessId, long WindowHandle, string ProcessName,
    DateTimeOffset ProcessStartedAtUtc, string? Title, string? RootRuntimeId);

public sealed record AuditArtifact(
    string Path, string Sha256, long Length, string MediaType, string Source);

public sealed record AuditCheckDefinition(
    string Id, string Title, string Source, string Mode,
    IReadOnlyList<string> Criteria, string Availability);

public sealed record AuditCheckResult
{
    public required string CheckId { get; init; }
    public required string Status { get; init; }
    public required string Summary { get; init; }
    public string? SourceRuleId { get; init; }
    public string? ElementId { get; init; }
    public string? ElementName { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record AuditReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required string Status { get; init; }
    public required string Mode { get; init; }
    public required AuditTarget Target { get; init; }
    public required string WinappVersion { get; init; }
    public string CatalogVersion { get; init; } = "accessloop-audit/1";
    public IReadOnlyList<AuditCheckDefinition> Catalog { get; init; } = AuditCatalog.Checks;
    public IReadOnlyList<AuditCheckResult> Results { get; init; } = [];
    public IReadOnlyList<AuditArtifact> Artifacts { get; init; } = [];
    public JsonElement? Snapshot { get; init; }
    public JsonElement? Observation { get; init; }
    public AccessibilityScan? Axe { get; init; }
    public string? Error { get; init; }
    public string PrivacyNotice { get; init; } =
        "This local report may contain application names, accessible labels, notifications and screenshots. " +
        "Raw UIA Value fields and document text are excluded, but labels and screenshots can still reveal user data. " +
        "Inspect artifacts before sharing.";

    public int RuleFailureCount => Results.Count(result => result.Status == "rule-failure");
}

public static class AuditCatalog
{
    public static IReadOnlyList<AuditCheckDefinition> Checks { get; } =
    [
        new("capture.scope", "Requested window and process identity", "WinAppCLI", "snapshot", [], "implemented"),
        new("capture.tree", "UIA snapshot completeness and read errors", "WinAppCLI", "snapshot", [], "implemented"),
        new("capture.screenshot", "Window screenshot evidence", "WinAppCLI", "snapshot", [], "implemented"),
        new("axe.scan", "Axe.Windows rule scan (individual failures retain native rule IDs)", "Axe.Windows", "snapshot", [], "implemented"),
        new("uia.identifiers", "Missing or ambiguous automation identifiers (testability only)", "AccessLoop", "snapshot", [], "implemented"),
        new("text.contrast", "Text contrast from provider attributes", "AccessLoop", "snapshot", ["1.4.3 (partial)"], "implemented"),
        new("keyboard.path", "Observed forward and reverse keyboard focus paths", "WinAppCLI", "active", ["2.1.1 (observation)", "2.4.3 (observation)"], "implemented"),
        new("keyboard.repeats", "Repeated focus targets, not a keyboard-trap verdict", "AccessLoop", "active", ["2.1.2 (observation)"], "implemented"),
        new("keyboard.unvisited", "Snapshot focus candidates not visited by this bounded probe", "AccessLoop", "active", ["2.1.1 (observation)"], "implemented"),
        new("events.timeline", "Scoped UIA events actually observed", "WinAppCLI", "observe", ["4.1.3 (observation)"], "implemented"),
        new("label.visible-match", "Accessible name contains independently acquired visible label", "AccessLoop", "snapshot", ["2.5.3"], "unsupported"),
        new("pointer.targets", "Effective pointer target size, spacing and exceptions", "AccessLoop", "snapshot", ["2.5.8"], "unsupported"),
        new("controls.behavior", "Standard control activation and state-transition contracts", "AccessLoop", "active", ["2.1.1 (partial)", "4.1.2 (partial)"], "unsupported"),
        new("visual.estimates", "Pixel contrast, focus visibility and clipping candidates", "AccessLoop", "snapshot", ["1.4.3", "2.4.7", "2.4.11"], "unsupported"),
        new("environment.matrix", "Isolated contrast-theme, text-size and layout variants", "AccessLoop", "environment", ["1.4.4", "1.4.10"], "unsupported"),
        new("reader.output", "Actual assistive-technology output", "AccessLoop", "active", ["4.1.2 (evidence)", "4.1.3 (evidence)"], "unsupported"),
        new("content.meaning", "Names, reading order, errors, business tasks and media equivalence", "Assessment", "assisted", [], "assisted")
    ];

    public static readonly IReadOnlySet<string> ResultStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "rule-failure", "no-violations-reported", "observed", "inconclusive",
        "not-tested", "unsupported", "blocked"
    };
}
