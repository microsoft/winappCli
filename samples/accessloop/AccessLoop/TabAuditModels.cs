namespace AccessLoop;

internal sealed record TabNavigationSnapshot
{
    public required int SchemaVersion { get; init; }
    public required int ProcessId { get; init; }
    public required long WindowHandle { get; init; }
    public required string RootRuntimeId { get; init; }
    public required string TreeSignature { get; init; }
    public required string Status { get; init; }
    public required int MaxDepth { get; init; }
    public required int MaxElements { get; init; }
    public required bool DepthLimitReached { get; init; }
    public required bool ElementLimitReached { get; init; }
    public required IReadOnlyList<TabNavigationContainer> Containers { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public TabNavigationReceipt? Selection { get; init; }
}

internal sealed record TabNavigationContainer
{
    public required string RuntimeId { get; init; }
    public string? ParentContainerRuntimeId { get; init; }
    public string? AutomationId { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool IsOffscreen { get; init; }
    public required bool Supported { get; init; }
    public string? Reason { get; init; }
    public required IReadOnlyList<TabNavigationItem> Items { get; init; }
}

internal sealed record TabNavigationItem
{
    public required string RuntimeId { get; init; }
    public string? AutomationId { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool IsOffscreen { get; init; }
    public required bool IsSelected { get; init; }
    public required bool Supported { get; init; }
    public string? Reason { get; init; }
}

internal sealed record TabNavigationReceipt(string ContainerRuntimeId, string ItemRuntimeId, string Status);
internal sealed record TabSelection(string ContainerRuntimeId, string ItemRuntimeId, string? AutomationId);
internal sealed record TabAuditSkip(IReadOnlyList<TabSelection> Context, string? ContainerRuntimeId, string? ItemRuntimeId, string Reason);
internal sealed record TabAuditRestoration(string ContainerRuntimeId, string OriginalItemRuntimeId, string Status, string? Error);

internal sealed record TabAuditPage
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public required IReadOnlyList<TabSelection> SelectionPath { get; init; }
    public required string Status { get; init; }
    public required string CaptureStatus { get; init; }
    public required string ScanSha256 { get; init; }
    public required int RuleFailureCount { get; init; }
    public string? Error { get; init; }
}

internal sealed record TabAuditReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "standard-tabs";
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required AuditTarget Target { get; init; }
    public required string WinappVersion { get; init; }
    public required int MaxPages { get; init; }
    public required string Status { get; init; }
    public required string NavigationStatus { get; init; }
    public required string CaptureStatus { get; init; }
    public required IReadOnlyList<TabAuditPage> Pages { get; init; }
    public required IReadOnlyList<TabAuditSkip> Skipped { get; init; }
    public required IReadOnlyList<TabAuditRestoration> Restorations { get; init; }
    public required IReadOnlyList<AuditArtifact> Artifacts { get; init; }
    public string? Error { get; init; }
    public int CapturedPageCount => Pages.Count(page => page.Status == "captured");
    public bool HasAuditResults => Pages.Any(page => page.CaptureStatus != "blocked");
    public int? RuleFailureCount => HasAuditResults ? Pages.Sum(page => page.RuleFailureCount) : null;
    public string CoverageNotice { get; init; } =
        "Only discovered, enabled, on-screen standard Tab/TabItem selection patterns are navigated. " +
        "Nested tab groups are visited within their parent page. Independent groups are visited one at a time, " +
        "not in every combination. Stable UIA structure and selected-state readbacks do not prove application-specific loading is finished. " +
        "Rule failures are occurrences across page scans, not a deduplicated whole-app issue count. " +
        "Restoring tab selection does not undo application side effects. This is not a WCAG compliance verdict.";
}
