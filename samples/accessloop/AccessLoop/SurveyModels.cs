namespace AccessLoop;

public sealed record SurveyProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string ProcessName { get; init; }
    public required string NavigationPaneId { get; init; }
    public required string MenuRootId { get; init; }
    public required string ItemClassName { get; init; }
    public required string HeadingAutomationId { get; init; }
    public required IReadOnlyList<string> GroupIds { get; init; }
    public required IReadOnlyList<string> ExcludedIds { get; init; }
    public IReadOnlyDictionary<string, string[]> HeadingAliases { get; init; } = new Dictionary<string, string[]>();
    public IReadOnlyList<string> HeadingWithoutIdPageIds { get; init; } = [];
    public double HeadingWithoutIdTopFraction { get; init; } = 0.25;
    public int MaxPages { get; init; } = 60;
    public int TreeDepth { get; init; } = 30;
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record SurveyPageDefinition(string Id, string Name, IReadOnlyList<string> GroupIds);
public sealed record SurveyExcluded(string Id, string Name, string Reason);
public sealed record SurveyNavigationState(string? SelectedPageId, IReadOnlyDictionary<string, bool> ExpandedGroups);
public sealed record SurveyInventory(
    IReadOnlyList<SurveyPageDefinition> Pages, IReadOnlyList<SurveyExcluded> Excluded,
    IReadOnlyList<string> NavigationIds, SurveyNavigationState OriginalNavigation);

public sealed record SurveyAppIdentity(
    string ProcessName, string ExecutablePath, string ExecutableSha256,
    string ProductVersion, string? CompanionSha256 = null);
public sealed record SurveyEnvironment(
    int WindowWidth, int WindowHeight, uint Dpi, string OsVersion,
    string UiCulture, bool HighContrast, bool? AppsUseLightTheme);
public sealed record SurveyContext(SurveyAppIdentity Application, SurveyEnvironment Environment);
public sealed record SurveyToolIdentity(string WinappVersion, string NativeSha256, string RunnerSha256);
public sealed record SurveyPageReady(bool Selected, bool HeadingMatched, string? Heading, DateTimeOffset ObservedAtUtc)
{
    public string EvidenceKind { get; init; } = "automation-id-and-name";
}
public sealed record SurveyRestoration(string Status, string Message);
public sealed record SurveyPerformance(int NativeCommands, long NativeMilliseconds, long AuditMilliseconds);

public sealed record SurveyAttempt
{
    public required int Number { get; init; }
    public required string Directory { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public SurveyPageReady? Before { get; set; }
    public SurveyPageReady? After { get; set; }
    public string? ScanSha256 { get; set; }
    public string? CaptureStatus { get; set; }
    public int RuleFailures { get; set; }
    public int Nodes { get; set; }
    public long NavigationMilliseconds { get; set; }
    public long ScanMilliseconds { get; set; }
    public string? Error { get; set; }
}

public sealed record SurveyPageState
{
    public required SurveyPageDefinition Page { get; init; }
    public string Status { get; set; } = "pending";
    public List<SurveyAttempt> Attempts { get; init; } = [];
}

public sealed record SurveyInvocation
{
    public required int Number { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required bool Resuming { get; init; }
    public bool OperatorConfirmedUnchangedState { get; init; }
    public IReadOnlyList<string> RetryPages { get; init; } = [];
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public string? Reason { get; set; }
    public SurveyRestoration? Restoration { get; set; }
    public SurveyPerformance? Performance { get; set; }
}

public sealed record SurveyState
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required AuditTarget Target { get; init; }
    public required string ProfileSha256 { get; init; }
    public required SurveyToolIdentity Tools { get; init; }
    public required SurveyContext Context { get; init; }
    public bool Screenshot { get; init; } = true;
    public string Status { get; set; } = "created";
    public string? Error { get; set; }
    public int Revision { get; set; }
    public SurveyNavigationState? OriginalNavigation { get; set; }
    public SurveyInventory? Inventory { get; set; }
    public string? InventorySha256 { get; set; }
    public List<SurveyPageState> Pages { get; init; } = [];
    public List<SurveyInvocation> Invocations { get; init; } = [];
}

public sealed record SurveyOptions(
    string? Profile, string? Resume, int? ProcessId, long? WindowHandle,
    string Winapp, string? Output, bool AcceptNavigation, bool ConfirmUnchanged,
    int? StopAfter, IReadOnlyList<string> RetryPages, bool NoScreenshot, bool Json);

public interface ISurveySession
{
    AuditTarget Target { get; }
    SurveyContext GetContext();
    void Preflight();
    Task<SurveyNavigationState> ReadNavigationStateAsync(CancellationToken token);
    Task<SurveyInventory> DiscoverAsync(SurveyNavigationState original, CancellationToken token);
    Task ValidateInventoryAsync(SurveyInventory inventory, CancellationToken token);
    Task<SurveyPageReady> NavigateAsync(SurveyPageDefinition page, CancellationToken token);
    Task<SurveyPageReady> VerifyPageAsync(SurveyPageDefinition page, CancellationToken token);
    Task<AuditReport> ScanAsync(string outputDirectory, CancellationToken token);
    Task<SurveyRestoration> RestoreAsync(SurveyNavigationState original, CancellationToken token);
    SurveyPerformance Performance { get; }
}

public sealed class SurveyBlockedException(string code, string message, bool canResume) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool CanResume { get; } = canResume;
}
