namespace AccessLoop;

internal sealed class TabAuditRunner(
    IAuditPlatform platform, ITabNavigationClient? navigation = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? timeProvider = null)
{
    public async Task<TabAuditReport> RunAsync(
        AuditOptions options, AuditTarget target, string version, string output, CancellationToken token)
    {
        if (options.Navigation != "standard-tabs" || !options.AcceptNavigation || options.TabSteps != 0
            || options.MaxPages is < 1 or > 100)
            throw new ArgumentException("Standard-tab scans require explicit navigation approval, a 1-100 page budget, and no keyboard probe.");

        var started = DateTimeOffset.UtcNow;
        var pages = new List<TabAuditPage>();
        var skipped = new List<TabAuditSkip>();
        var restorations = new List<TabAuditRestoration>();
        var artifacts = new List<AuditArtifact>();
        var client = navigation ?? new TabNavigationClient(platform, target, options, output, artifacts);
        var clock = timeProvider ?? TimeProvider.System;
        var pause = delay ?? Task.Delay;
        var expectedRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var stopped = false;
        var noTabs = false;
        string? error = null;
        string? originalRoot = null;
        TabNavigationSnapshot? initial = null;

        AuditFiles.WriteNew(Path.Combine(output, "request.json"), new
        {
            options.Navigation, options.AcceptNavigation, options.MaxPages,
            options.Depth, options.MaxElements, options.TimeoutMs, options.ObserveSeconds, options.Screenshot,
            requestedApp = options.App,
            target,
            policy = "Exact SelectionItem.Select only; no keyboard, pointer, scrolling, disclosure or close actions."
        });
        artifacts.Add(AuditFiles.Describe(output, "request.json", "application/json", "Approved standard-tab scan request"));

        try
        {
            initial = await client.ReadAsync(token);
            originalRoot = initial.RootRuntimeId;
            if (initial.Status != "captured")
            {
                skipped.Add(new([], null, null, "Navigation discovery is incomplete or blocked. " + string.Join(" ", initial.Issues)));
                await CaptureAsync([], null);
            }
            else
            {
                var roots = initial.Containers.Where(group => group.ParentContainerRuntimeId is null).ToArray();
                foreach (var group in roots.Where(CanVisit))
                    expectedRoots.Add(group.RuntimeId, Selected(group).RuntimeId);
                foreach (var group in roots)
                {
                    token.ThrowIfCancellationRequested();
                    await VisitAsync(group, []);
                }
                if (pages.Count == 0)
                {
                    noTabs = initial.Containers.Count == 0;
                    if (!noTabs)
                        skipped.Add(new([], null, null, "No supported, enabled, on-screen standard Tab pages could be visited. Only the current page was scanned."));
                    await CaptureAsync([], null);
                }
                var final = await client.ReadAsync(token);
                RequireReady(final, []);
                if (!roots.Select(group => group.RuntimeId).SequenceEqual(
                    final.Containers.Where(group => group.ParentContainerRuntimeId is null).Select(group => group.RuntimeId)))
                    throw new InvalidOperationException("The top-level tab inventory changed during the scan.");
            }
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            stopped = true;
            error = Message(ex);
        }

        var navigationStatus = stopped || skipped.Count > 0 || pages.Count == 0
            || restorations.Any(item => item.Status is not ("restored" or "unchanged")) ? "partial" : noTabs ? "not-applicable" : "completed";
        var captureStatus = pages.Count == 0 || pages.All(page => page.CaptureStatus == "blocked") ? "blocked"
            : pages.Any(page => page.Status != "captured" || page.CaptureStatus != "completed") ? "partial" : "completed";
        return new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedAtUtc = started,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Target = target with { RootRuntimeId = originalRoot },
            WinappVersion = version,
            MaxPages = options.MaxPages,
            Status = captureStatus == "blocked" ? "blocked" : navigationStatus != "partial" && captureStatus == "completed" ? "completed" : "partial",
            NavigationStatus = navigationStatus,
            CaptureStatus = captureStatus,
            Pages = pages,
            Skipped = skipped,
            Restorations = restorations,
            Artifacts = artifacts,
            Error = error
        };

        async Task VisitAsync(TabNavigationContainer observed, IReadOnlyList<TabSelection> ancestors)
        {
            if (ancestors.Count >= 8)
            {
                skipped.Add(new(ancestors, observed.RuntimeId, null, "Nested tab depth exceeds the 8-group navigation limit."));
                return;
            }
            if (!CanVisit(observed))
            {
                skipped.Add(new(ancestors, observed.RuntimeId, null,
                    observed.Reason ?? "The tab group is not a supported, enabled, on-screen single-selection group."));
                return;
            }
            if (pages.Count >= options.MaxPages)
            {
                skipped.Add(new(ancestors, observed.RuntimeId, null, $"The {options.MaxPages}-page capture budget was reached."));
                return;
            }

            var current = await client.ReadAsync(token);
            RequireReady(current, ancestors);
            var group = RequireInventory(current, observed);
            var original = Selected(group);
            var selection = new TabSelection(group.RuntimeId, original.RuntimeId, original.AutomationId);
            var changed = false;
            try
            {
                foreach (var plannedItem in group.Items.OrderByDescending(item => item.IsSelected))
                {
                    token.ThrowIfCancellationRequested();
                    RequireReady(current, ancestors);
                    var liveGroup = RequireInventory(current, group);
                    var item = liveGroup.Items.Single(item => item.RuntimeId == plannedItem.RuntimeId);
                    if (!item.Supported || !item.IsEnabled || item.IsOffscreen)
                    {
                        skipped.Add(new(ancestors, group.RuntimeId, item.RuntimeId,
                            item.Reason ?? "The tab item is unsupported, disabled, offscreen or unrealized; no fallback action is attempted."));
                        continue;
                    }
                    if (pages.Count >= options.MaxPages)
                    {
                        skipped.Add(new(ancestors, group.RuntimeId, item.RuntimeId, $"The {options.MaxPages}-page capture budget was reached."));
                        continue;
                    }
                    selection = new(group.RuntimeId, item.RuntimeId, item.AutomationId);
                    var path = ancestors.Append(selection).ToArray();
                    if (!item.IsSelected)
                    {
                        changed = true;
                        if (ancestors.Count == 0)
                            expectedRoots[group.RuntimeId] = item.RuntimeId;
                        _ = await client.ReadAsync(token, selection, originalRoot);
                        current = await client.ReadAsync(token);
                    }
                    _ = RequireInventory(current, group);
                    current = await SettleAsync(current, path);
                    _ = RequireInventory(current, group);
                    await CaptureAsync(path, current);
                    current = await client.ReadAsync(token);
                    RequireReady(current, path);
                    var children = current.Containers.Where(child => child.ParentContainerRuntimeId == group.RuntimeId).ToArray();
                    foreach (var child in children)
                        await VisitAsync(child, path);
                    current = await client.ReadAsync(token);
                    RequireReady(current, path);
                }
            }
            catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
            {
                stopped = true;
                throw;
            }
            finally
            {
                if (!changed)
                    restorations.Add(new(group.RuntimeId, original.RuntimeId, "unchanged", null));
                else if (stopped || token.IsCancellationRequested)
                    restorations.Add(new(group.RuntimeId, original.RuntimeId, "not-attempted",
                        "Navigation stopped or was cancelled; no further selection, including restoration, is authorized by this run."));
                else
                {
                    try
                    {
                        current = await client.ReadAsync(token);
                        RequireReady(current, ancestors.Append(selection).ToArray());
                        var liveGroup = RequireInventory(current, group);
                        var originalItem = liveGroup.Items.Single(item => item.RuntimeId == original.RuntimeId);
                        if (!originalItem.Supported || !originalItem.IsEnabled || originalItem.IsOffscreen)
                            throw new InvalidOperationException("The original tab cannot be restored automatically (" +
                                (originalItem.Reason ?? "unsupported, disabled or offscreen") +
                                "). Select the original tab manually before another run.");
                        var restore = new TabSelection(group.RuntimeId, original.RuntimeId, original.AutomationId);
                        if (ancestors.Count == 0)
                            expectedRoots[group.RuntimeId] = original.RuntimeId;
                        if (!originalItem.IsSelected)
                        {
                            _ = await client.ReadAsync(token, restore, originalRoot);
                            current = await client.ReadAsync(token);
                        }
                        _ = await SettleAsync(current, ancestors.Append(restore).ToArray());
                        restorations.Add(new(group.RuntimeId, original.RuntimeId, "restored", null));
                    }
                    catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
                    {
                        stopped = true;
                        restorations.Add(new(group.RuntimeId, original.RuntimeId, "blocked", Message(ex)));
                        throw;
                    }
                }
            }
        }

        async Task<TabNavigationSnapshot> SettleAsync(TabNavigationSnapshot current, IReadOnlyList<TabSelection> path)
        {
            var startedAt = clock.GetTimestamp();
            var stableSince = startedAt;
            RequireReady(current, path);
            for (var attempt = 0; attempt < options.TimeoutMs / 250; attempt++)
            {
                if (clock.GetElapsedTime(startedAt).TotalMilliseconds >= options.TimeoutMs)
                    break;
                await pause(TimeSpan.FromMilliseconds(250), token);
                var next = await client.ReadAsync(token);
                RequireReady(next, path);
                var observedAt = clock.GetTimestamp();
                var unchanged = next.TreeSignature == current.TreeSignature && SameSelections(current, next);
                if (!unchanged)
                    stableSince = observedAt;
                current = next;
                // A slow, individually bounded capture can confirm stability without a third sample.
                // Evaluate its evidence before deciding whether another capture may start.
                if (unchanged && clock.GetElapsedTime(stableSince, observedAt) >= TimeSpan.FromMilliseconds(500))
                    return current;
            }
            throw new TimeoutException("The selected tab and bounded UIA structure did not settle within the capture timeout. " +
                "This page was not audited. Retry with a larger --timeout-ms, or use --current-page to capture the current page without tab navigation.");
        }

        async Task CaptureAsync(IReadOnlyList<TabSelection> path, TabNavigationSnapshot? before)
        {
            var id = $"page-{pages.Count + 1:D3}";
            var relative = Path.Combine("pages", id);
            var directory = Path.Combine(output, relative);
            Directory.CreateDirectory(directory);
            var pageOptions = options with
            {
                Navigation = null, AcceptNavigation = false, Output = directory,
                ProcessId = null, WindowHandle = target.WindowHandle, App = null, TabSteps = 0, AcceptInput = false
            };
            var report = await new AuditRunner(platform).RunAsync(pageOptions, target, version, directory, token);
            TabNavigationSnapshot? after = null;
            string? boundaryError = null;
            if (before is not null)
            {
                try
                {
                    after = await client.ReadAsync(token);
                    RequireReady(after, path);
                    if (!SameSelections(before, after))
                        throw new InvalidOperationException("Tab selections or the visible tab inventory changed while page evidence was captured.");
                }
                catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
                {
                    boundaryError = Message(ex);
                    stopped = true;
                    report = report with
                    {
                        Status = report.Status == "blocked" ? "blocked" : "partial",
                        Error = string.Join(Environment.NewLine, new[] { report.Error, boundaryError }.Where(value => value is not null))
                    };
                }
            }
            AuditFiles.WriteNew(Path.Combine(directory, "navigation.json"), new
            {
                selectionPath = path,
                status = boundaryError is not null ? "blocked" : before is null ? "current-page-only" : "verified",
                before, after, error = boundaryError
            });
            report = report with
            {
                Artifacts = report.Artifacts.Append(AuditFiles.Describe(directory, "navigation.json", "application/json",
                    "Tab selections immediately before and after this page audit")).ToArray()
            };
            AuditFiles.WriteNew(Path.Combine(directory, "scan.json"), report);
            AuditReportRenderer.Write(Path.Combine(directory, "report.html"), report);
            var scan = AuditFiles.Describe(output, Path.Combine(relative, "scan.json"), "application/json", "Immutable per-page audit");
            artifacts.Add(scan);
            artifacts.Add(AuditFiles.Describe(output, Path.Combine(relative, "report.html"), "text/html", "Per-page accessibility report"));
            pages.Add(new()
            {
                Id = id, Directory = relative, SelectionPath = path,
                Status = boundaryError is null ? "captured" : "blocked",
                CaptureStatus = report.Status, ScanSha256 = scan.Sha256,
                RuleFailureCount = report.RuleFailureCount, Error = boundaryError ?? report.Error
            });
            if (boundaryError is not null)
                throw new InvalidOperationException(boundaryError);
        }

        void RequireReady(TabNavigationSnapshot snapshot, IReadOnlyList<TabSelection> path)
        {
            token.ThrowIfCancellationRequested();
            if (snapshot.RootRuntimeId != originalRoot || snapshot.Status != "captured")
                throw new InvalidOperationException("Tab navigation scope or discovery became unavailable. " + string.Join(" ", snapshot.Issues));
            var expected = expectedRoots.Concat(path.Select(item => KeyValuePair.Create(item.ContainerRuntimeId, item.ItemRuntimeId)));
            foreach (var pair in expected)
            {
                var group = snapshot.Containers.SingleOrDefault(group => group.RuntimeId == pair.Key);
                if (group is null || !CanVisit(group) || Selected(group).RuntimeId != pair.Value)
                    throw new InvalidOperationException("An expected tab group or selected item changed outside the approved navigation step.");
            }
            foreach (var group in snapshot.Containers.Where(group => group.ParentContainerRuntimeId is null))
            {
                if (initial is not null && !initial.Containers.Any(original => original.RuntimeId == group.RuntimeId
                    && original.ParentContainerRuntimeId is null))
                    throw new InvalidOperationException("An unexpected independent tab group appeared; start a new scan for the changed layout.");
            }
        }
    }

    private static bool CanVisit(TabNavigationContainer group) =>
        group.Supported && group.IsEnabled && !group.IsOffscreen && group.Items.Count(item => item.IsSelected) == 1
        && group.Items.Any(item => item.IsSelected && item.Supported && item.IsEnabled && !item.IsOffscreen);

    private static TabNavigationItem Selected(TabNavigationContainer group) => group.Items.Single(item => item.IsSelected);

    private static TabNavigationContainer RequireInventory(TabNavigationSnapshot snapshot, TabNavigationContainer expected)
    {
        var actual = snapshot.Containers.SingleOrDefault(group => group.RuntimeId == expected.RuntimeId);
        if (actual is null || !CanVisit(actual) || actual.ParentContainerRuntimeId != expected.ParentContainerRuntimeId
            || !actual.Items.Select(item => item.RuntimeId).SequenceEqual(expected.Items.Select(item => item.RuntimeId)))
            throw new InvalidOperationException("A tab group or its item inventory changed; stale runtime identities are not reused.");
        return actual;
    }

    private static bool SameSelections(TabNavigationSnapshot left, TabNavigationSnapshot right) =>
        SelectionVector(left).SequenceEqual(SelectionVector(right), StringComparer.Ordinal);

    private static IEnumerable<string> SelectionVector(TabNavigationSnapshot snapshot) =>
        snapshot.Containers.OrderBy(group => group.RuntimeId, StringComparer.Ordinal).Select(group =>
            group.RuntimeId + ":" + string.Join("|", group.Items.Select(item =>
                $"{item.RuntimeId},{item.IsSelected},{item.IsEnabled},{item.IsOffscreen},{item.Supported}")));

    private static string Message(Exception exception) =>
        exception is OperationCanceledException ? "Tab scan cancelled. No additional navigation or restoration was attempted." : exception.Message;

    internal static int ExitCode(TabAuditReport report) => report.Status != "completed" ? 2 : report.RuleFailureCount > 0 ? 1 : 0;
}
