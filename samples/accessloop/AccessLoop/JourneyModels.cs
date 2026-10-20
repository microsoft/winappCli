namespace AccessLoop;

public sealed record JourneyDefinition
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string NavigationRootId { get; init; }
    public required string InitialPageId { get; init; }
    public required IReadOnlyList<JourneyPageDefinition> Pages { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public int MaxTransitions { get; init; } = 20;
    public int MaxRepairs { get; init; } = 4;
}

public sealed record JourneyPageDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string NavigationId { get; init; }
    public required string ReadyId { get; init; }
    public required IReadOnlyList<string> RequiredControlIds { get; init; }
    public JourneyExercise? Exercise { get; init; }
}

public sealed record JourneyExercise(
    string TargetId, string ExpectedName, string AnchorId, string ResultId,
    string InputText, string InitialResult, string ExpectedResult, int MaxTabs = 5);

public sealed record JourneyCandidate(string PageId, string NavigationId, string? Name, string ControlType);
public sealed record JourneyPlan(IReadOnlyList<string> PageOrder, string Explanation);
public sealed record JourneyRoute(string PageId, string FromPageId, string NavigationId, string ReadyId);
public sealed record JourneySourceStamp(string RelativePath, string Sha256);
public sealed record JourneyCheck(string Id, bool Passed, string Expected, string? Actual);

public sealed record JourneyPageResult
{
    public required string PageId { get; init; }
    public required string Status { get; init; }
    public required string EvidencePath { get; init; }
    public required JourneyRoute Route { get; init; }
    public IReadOnlyList<JourneyCheck> Checks { get; init; } = [];
    public IReadOnlyList<string> ObservedControlIds { get; init; } = [];
    public AuditReport? Audit { get; init; }
    public Observation? Exercise { get; init; }
    public string? Error { get; init; }
}

public sealed record JourneyRound
{
    public required string Phase { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<JourneyPageResult> Pages { get; init; }
    public IReadOnlyList<WorkbenchFinding> Findings { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record JourneyRepair
{
    public required string FindingId { get; init; }
    public required string PageId { get; init; }
    public required string Status { get; init; }
    public required string EvidencePath { get; init; }
    public string? AutomationId { get; init; }
    public string? SourcePath { get; init; }
    public string? Diff { get; init; }
    public bool RolledBack { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public int RemainingFindings { get; init; }
}

public sealed record JourneyReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required string InputSha256 { get; init; }
    public required string WinappVersion { get; init; }
    public string Status { get; set; } = "inconclusive";
    public string VerificationStatus { get; set; } = "inconclusive";
    public string CaptureStatus { get; set; } = "incomplete";
    public string? Error { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string PlanSource { get; set; } = "unplanned";
    public JourneyPlan? Plan { get; set; }
    public IReadOnlyList<JourneyCandidate> Candidates { get; set; } = [];
    public IReadOnlyList<JourneyRoute> FrozenRoutes { get; set; } = [];
    public IReadOnlyList<JourneySourceStamp> OriginalSources { get; set; } = [];
    public IReadOnlyList<JourneyPageDefinition> ExpectedPages { get; init; } = [];
    public List<JourneyRound> Rounds { get; init; } = [];
    public List<JourneyRepair> Repairs { get; init; } = [];
    public IReadOnlyList<WorkbenchFinding> RemainingFindings { get; set; } = [];
    public IReadOnlyList<string> Errors { get; set; } = [];
}

public sealed record JourneyOptions(
    string Input, string Winapp, string Copilot, string? Output,
    bool AcceptNavigation, bool Apply, bool UseCopilotPlan, string? Plan,
    string? Proposals, bool NoScreenshot, bool Json);

public interface IJourneySession : IAsyncDisposable
{
    Task<IReadOnlyList<JourneyCandidate>> DiscoverAsync(JourneyDefinition definition, CancellationToken token);
    Task<JourneyPageResult> VisitAsync(
        JourneyDefinition definition, JourneyPageDefinition page, JourneyRoute route,
        string phase, string evidencePath, CancellationToken token);
}

public interface IJourneyPlatform
{
    Task<IJourneySession> LaunchAsync(JourneyDefinition definition, string evidencePath, CancellationToken token);
    Task<JourneyPlan> PlanAsync(
        JourneyDefinition definition, IReadOnlyList<JourneyCandidate> candidates,
        string evidencePath, CancellationToken token);
    Task<RepairProposal> ProposeAsync(
        SourceCandidate candidate, Scenario scenario, Observation observation,
        string evidencePath, CancellationToken token);
}
