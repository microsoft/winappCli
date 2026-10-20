namespace AccessLoop;

public sealed record ExploreProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string ProcessName { get; init; }
    public string? Project { get; init; }
    public required string NavigationRootId { get; init; }
    public required string InitialPageId { get; init; }
    public IReadOnlyList<ExplorePage> Pages { get; init; } = [];
    public ExploreScopeProbe? ScopeProbe { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public int MaxDurationSeconds { get; init; } = 300;
    public int MaxStates { get; init; } = 16;
    public int TreeDepth { get; init; } = 20;
    public int MaxElements { get; init; } = 1000;
}

public sealed record ExplorePage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string NavigationId { get; init; }
    public required string ReadyId { get; init; }
    public ExploreScroll? Scroll { get; init; }
    public IReadOnlyList<ExploreDropdown> Dropdowns { get; init; } = [];
    public IReadOnlyList<ExploreDialog> Dialogs { get; init; } = [];
}

public sealed record ExploreScroll
{
    public required string ContainerId { get; init; }
    public required string TopAnchorId { get; init; }
    public required string BottomAnchorId { get; init; }
    public int MaxPositions { get; init; } = 4;
    public double OverlapFraction { get; init; } = 0.25;
}

public sealed record ExploreDropdown
{
    public required string ControlId { get; init; }
    public IReadOnlyList<string> ItemIds { get; init; } = [];
}

public sealed record ExploreDialog
{
    public required string OpenerId { get; init; }
    public required string RootId { get; init; }
    public required string DismissId { get; init; }
    public string Scope { get; init; } = "same-window";
}

public sealed record ExploreScopeProbe(string PageId, string OpenerId, string WindowTitle);

public sealed record ExploreOptions(
    string Input, string Winapp, string? Output, int? ProcessId, long? WindowHandle,
    bool LaunchOwned, bool AcceptInteractions, bool NoScreenshot, bool ProbeScopeGuard, bool Json);

public sealed record ExploreObservation
{
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string PageId { get; init; }
    public required long WindowHandle { get; init; }
    public bool PageReady { get; init; }
    public string? ControlId { get; init; }
    public double? ScrollPercent { get; init; }
    public double? ViewSizePercent { get; init; }
    public string? ExpansionState { get; init; }
    public IReadOnlyList<string> SelectionAutomationIds { get; init; } = [];
    public bool? DialogPresent { get; init; }
    public string? FocusAutomationId { get; init; }
}

public sealed record ExploreStateResult
{
    public required string Id { get; init; }
    public required string PageId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public ExploreObservation? Before { get; set; }
    public ExploreObservation? After { get; set; }
    public AuditTarget? Target { get; set; }
    public string? ScanSha256 { get; set; }
    public string? CaptureStatus { get; set; }
    public int RuleFailures { get; set; }
    public string? Error { get; set; }
}

public sealed record ExploreInteractionResult
{
    public required string Kind { get; init; }
    public required string ControlId { get; init; }
    public string Status { get; set; } = "not-tested";
    public string? Reason { get; set; }
    public ExploreObservation? Before { get; set; }
    public ExploreObservation? After { get; set; }
    public SurveyRestoration? Restoration { get; set; }
}

public sealed record ExplorePageResult
{
    public required string PageId { get; init; }
    public required string Name { get; init; }
    public string NavigationStatus { get; set; } = "pending";
    public string ScrollStatus { get; set; } = "not-requested";
    public string? ScrollReason { get; set; }
    public List<ExploreInteractionResult> Interactions { get; init; } = [];
    public string? Error { get; set; }
}

public sealed record ExploreEvent(
    DateTimeOffset AtUtc, string Kind, string Message, long? WindowHandle = null, int? ProcessId = null);

public sealed record ExploreGuardProbeResult
{
    public string Status { get; set; } = "not-requested";
    public string? Reason { get; set; }
    public int ActionsAfterBoundaryViolation { get; set; }
}

public sealed record ExploreReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public required AuditTarget Target { get; init; }
    public required string ProfileSha256 { get; init; }
    public required SurveyToolIdentity Tools { get; init; }
    public required ExploreProfile Profile { get; init; }
    public bool OwnedApplication { get; init; }
    public bool Screenshot { get; init; } = true;
    public string Status { get; set; } = "running";
    public List<ExplorePageResult> Pages { get; init; } = [];
    public List<ExploreStateResult> States { get; init; } = [];
    public List<ExploreEvent> Events { get; init; } = [];
    public ExploreGuardProbeResult GuardProbe { get; init; } = new();
    public SurveyRestoration? Restoration { get; set; }
    public SurveyPerformance? Performance { get; set; }
    public string? Error { get; set; }
}

public sealed record ExploreProgress(string Stage, string? PageId, string? StateId, string? Message = null);

public interface IExploreSession
{
    Task ExecuteAsync(
        ExploreProfile profile, ExploreReport report, string outputDirectory,
        bool probeScopeGuard, Action<ExploreProgress>? progress, CancellationToken token);
}
