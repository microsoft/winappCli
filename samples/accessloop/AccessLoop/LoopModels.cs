using System.Text.Json;

namespace AccessLoop;

public sealed record LoopProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string ProcessName { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public int MaxDurationSeconds { get; init; } = 300;
    public int TreeDepth { get; init; } = 20;
    public int MaxElements { get; init; } = 1000;
    public IReadOnlyList<LoopPage> Pages { get; init; } = [];
    public IReadOnlyList<LoopStep> Steps { get; init; } = [];
    public IReadOnlyList<LoopRepairTarget> RepairTargets { get; init; } = [];
}

public sealed record LoopPage(string Id, string Name, string NavigationId, string ReadyId);

public sealed record LoopStep
{
    public required string Id { get; init; }
    public required string PageId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? TargetId { get; init; }
    public string? ControlType { get; init; }
    public string? Keys { get; init; }
    public double? VerticalPercent { get; init; }
    public string? ExpansionState { get; init; }
    public IReadOnlyList<LoopExpectation> Expectations { get; init; } = [];
    public IReadOnlyList<string> RequiredIds { get; init; } = [];
}

public sealed record LoopExpectation(string TargetId, string Property, JsonElement Expected);
public sealed record LoopRepairTarget(string AutomationId, string ExpectedName, string PageId, string CheckpointId);

public sealed record LoopCheckResult
{
    public required string TargetId { get; init; }
    public required string Property { get; init; }
    public required JsonElement Expected { get; init; }
    public JsonElement? Actual { get; init; }
    public bool Passed { get; init; }
    public string? Error { get; init; }
}

public sealed record LoopCommandEvidence(
    string Operation, IReadOnlyList<string> Arguments, string ResponseFile, string? ErrorFile,
    string ResponseSha256, string? ErrorSha256);

public sealed record LoopActionResult
{
    public required LoopStep Step { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public JsonElement? Before { get; set; }
    public JsonElement? After { get; set; }
    public List<LoopCommandEvidence> Commands { get; init; } = [];
    public List<LoopCheckResult> Checks { get; init; } = [];
    public string? Error { get; set; }
}

public sealed record LoopStateResult
{
    public required string StepId { get; init; }
    public required string PageId { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public string? CaptureStatus { get; set; }
    public string? ScanSha256 { get; set; }
    public int RuleFailureOccurrences { get; set; }
    public Dictionary<string, JsonElement> BeforeControls { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> AfterControls { get; init; } = new(StringComparer.Ordinal);
    public string? Error { get; set; }
}

public sealed record LoopRoundResult
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public AuditTarget? Target { get; set; }
    public IReadOnlyList<JourneySourceStamp> Sources { get; set; } = [];
    public string? SourceDirectory { get; set; }
    public List<LoopActionResult> Actions { get; init; } = [];
    public List<LoopStateResult> States { get; init; } = [];
    public List<ExploreEvent> ScopeEvents { get; init; } = [];
    public string? ActionLogSha256 { get; set; }
    public string? Error { get; set; }
}

public sealed record LoopRepairResult
{
    public required string AutomationId { get; init; }
    public required string ExpectedName { get; init; }
    public required string Directory { get; init; }
    public string Status { get; set; } = "pending";
    public string? ProposalSource { get; set; }
    public RepairProposal? Proposal { get; set; }
    public string? DiffFile { get; set; }
    public string? VerificationRoundId { get; set; }
    public string? Error { get; set; }
}

public sealed record LoopReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required LoopProfile Profile { get; init; }
    public required string PlanSha256 { get; init; }
    public required SurveyToolIdentity Tools { get; init; }
    public string PlanSource { get; init; } = "preset";
    public bool Screenshot { get; init; } = true;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public string ReproductionStatus { get; set; } = "not-run";
    public string VerificationStatus { get; set; } = "not-run";
    public string CaptureStatus { get; set; } = "unknown";
    public List<LoopRoundResult> Rounds { get; init; } = [];
    public List<LoopRepairResult> Repairs { get; init; } = [];
    public string? ReplayOf { get; init; }
    public string? Error { get; set; }
}

public interface ILoopRoundRunner
{
    Task<LoopRoundResult> RunAsync(
        LoopProfile profile, string roundId, string outputDirectory, CancellationToken token);
}

public sealed record LoopProgress(
    string RoundId, string Stage, string? StepId = null, AuditTarget? Target = null, string? Message = null)
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;
}
