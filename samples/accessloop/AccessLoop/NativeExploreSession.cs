using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeExploreSession : IExploreSession
{
    internal const double PercentTolerance = 0.1;
    private const double ViewSizeTolerance = 0.05;
    private readonly IAuditPlatform inner;
    private readonly AuditTarget target;
    private readonly string winappVersion;
    private readonly bool screenshot;
    private readonly ExploreScopeMonitor monitor;
    private readonly GuardedPlatform platform;
    private ExploreProfile profile = null!;
    private ExploreReport report = null!;
    private string output = null!;
    private Action<ExploreProgress>? progress;
    private readonly Stopwatch elapsed = new();
    private int nativeCommands, cleanupCommands, sequence;
    private long nativeMilliseconds, auditMilliseconds;
    private bool executed, cleaningUp, overlayOpen, originalObserved;
    private string? currentPage;

    public NativeExploreSession(IAuditPlatform platform, AuditTarget target, string winappVersion, bool screenshot)
        : this(platform, target, winappVersion, screenshot, new SurveyNativeContext(), new ExploreNativeWindowSource()) { }

    internal NativeExploreSession(
        IAuditPlatform platform, AuditTarget target, string winappVersion, bool screenshot,
        ISurveyNativeContext native, IExploreWindowSource windows)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(target);
        inner = platform;
        this.target = target;
        this.winappVersion = winappVersion;
        this.screenshot = screenshot;
        monitor = new(platform, native, windows);
        this.platform = new(this);
    }

    public async Task ExecuteAsync(
        ExploreProfile profile, ExploreReport report, string outputDirectory, bool probeScopeGuard,
        Action<ExploreProgress>? progress, CancellationToken token)
    {
        if (executed)
            throw new InvalidOperationException("An exploration session is single-use; existing evidence is never replayed or overwritten.");
        executed = true;
        using var scopeLifetime = monitor;
        this.report = report;
        this.progress = progress;
        output = Path.GetFullPath(outputDirectory);
        this.profile = JsonSerializer.Deserialize<ExploreProfile>(JsonSerializer.Serialize(profile, Json.Options), Json.Options)!;
        elapsed.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(profile.MaxDurationSeconds, 1, 3600)));
        using var running = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, monitor.StopToken);
        var ordinaryComplete = false;
        try
        {
            ValidateRequest();
            AuditFiles.EnsureDirectory(output);
            InitializePages();
            token.ThrowIfCancellationRequested();
            monitor.Start(target);
            var initial = await InspectAsync(target, running.Token);
            RequirePage(initial, this.profile.InitialPageId);
            currentPage = this.profile.InitialPageId;
            originalObserved = true;
            foreach (var page in this.profile.Pages)
                _ = await NavigationAsync(initial, page, running.Token);

            foreach (var page in this.profile.Pages)
            {
                var result = report.Pages.Single(item => item.PageId == page.Id);
                progress?.Invoke(new("page", page.Id, null, page.Name));
                try
                {
                    RequireStateBudget();
                    await NavigateAsync(page, running.Token);
                    var baseline = await CaptureAsync(page, "base", page.Name, target,
                        ct => ReadBaseAsync(page, ct), running.Token);
                    result.NavigationStatus = baseline.Status == "captured" ? "captured" : "blocked";
                    if (baseline.Status != "captured")
                        throw new InvalidOperationException(baseline.Error ?? "The base page state was not verified.");
                    if (page.Scroll is not null)
                        await ExploreScrollAsync(page, result, baseline, running.Token);
                    foreach (var dropdown in page.Dropdowns)
                    {
                        RequireStateBudget();
                        await ExploreDropdownAsync(page, dropdown,
                            result.Interactions.Single(item => item.Kind == "dropdown" && item.ControlId == dropdown.ControlId), running.Token);
                        if (overlayOpen)
                            throw new InvalidOperationException("The dropdown could not be safely closed; no underlying-page actions are permitted.");
                    }
                    foreach (var dialog in page.Dialogs)
                    {
                        RequireStateBudget();
                        await ExploreDialogAsync(page, dialog,
                            result.Interactions.Single(item => item.Kind == "dialog" && item.ControlId == dialog.RootId), running.Token);
                        if (overlayOpen)
                            throw new InvalidOperationException("The dialog could not be safely closed; no underlying-page actions are permitted.");
                    }
                }
                catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
                {
                    result.Error = Error(ex);
                    if (result.NavigationStatus == "pending") result.NavigationStatus = "blocked";
                    if (page.Scroll is not null && result.ScrollStatus == "pending")
                    {
                        result.ScrollStatus = ex is ExploreBudgetException ? "limit-reached" : "blocked";
                        result.ScrollReason = Error(ex);
                    }
                    if (monitor.Violation is not null || ex is OperationCanceledException or ExploreBudgetException || overlayOpen)
                        throw;
                    // A bounded unsupported page does not authorize any fallback action.
                }
            }
            ordinaryComplete = CoverageComplete();
            report.Status = ordinaryComplete ? "finished" : "partial";
            if (!ordinaryComplete)
                report.Error = "One or more declared page, scroll or interaction states remain incomplete. See their explicit reasons; no compliance result is inferred.";
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            report.Status = monitor.Violation is not null || ex is ExploreStoppedException || token.IsCancellationRequested ? "paused" : "partial";
            report.Error = deadline.IsCancellationRequested && !token.IsCancellationRequested && monitor.Violation is null
                ? "The maximum run duration was reached. Uncaptured states remain incomplete." : Error(ex);
        }
        catch (Exception ex)
        {
            monitor.Stop("unexpected-error", $"Unexpected executor failure ({ex.GetType().Name}); no automatic restoration is authorized.");
            report.Status = "paused";
            report.Error = ex.Message;
            throw;
        }
        finally
        {
            // Cleanup is independently bounded, but never bypasses the latched scope guard.
            report.Restoration = await RestorePageAsync();
        }

        try
        {
            if (probeScopeGuard)
            {
                if (!report.OwnedApplication || this.profile.ScopeProbe is null)
                {
                    report.GuardProbe.Status = "not-tested";
                    report.GuardProbe.Reason = "The deliberate boundary probe requires an owned application and a declared scopeProbe.";
                }
                else if (ordinaryComplete && report.Restoration?.Status == "restored-navigation" && monitor.Violation is null
                    && !running.IsCancellationRequested)
                    await ProbeAsync(running.Token);
                else
                {
                    report.GuardProbe.Status = "not-tested";
                    report.GuardProbe.Reason = "The ordinary state path did not finish safely; the deliberate probe was not triggered.";
                }
            }
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            report.GuardProbe.Status = "not-tested";
            report.GuardProbe.Reason = Error(ex);
            report.Status = token.IsCancellationRequested ? "paused" : "partial";
            report.Error = Error(ex);
            if (monitor.Violation is not null)
            {
                report.Status = "paused";
                report.Error = monitor.Violation.Message;
                report.Restoration = ProbeRestoration(stopped: true);
            }
        }
        finally
        {
            if (originalObserved && monitor.Violation is null)
            {
                try { monitor.Check(target); }
                catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex)) { monitor.Stop("scope-unavailable", ex.Message); }
            }
            monitor.Dispose();
            if (monitor.Violation is { } stopped && report.GuardProbe.Status != "stopped")
            {
                report.Status = "paused";
                report.Error = stopped.Message;
            }
            foreach (var page in report.Pages)
            {
                if (page.NavigationStatus == "pending")
                {
                    page.NavigationStatus = "blocked";
                    page.Error ??= report.Error ?? "The page was not reached within the approved run bounds.";
                }
                if (page.ScrollStatus == "pending")
                {
                    page.ScrollStatus = "blocked";
                    page.ScrollReason ??= report.Error ?? "The declared scroll path was not reached.";
                }
            }
            foreach (var item in monitor.Events)
                report.Events.Add(item);
            report.Performance = new(nativeCommands, nativeMilliseconds, auditMilliseconds);
            report.FinishedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void ValidateRequest()
    {
        if (profile.TimeoutSeconds is < 1 or > 120 || profile.MaxDurationSeconds is < 1 or > 3600
            || profile.MaxStates is < 1 or > 1000 || profile.TreeDepth is < 1 or > 100
            || profile.MaxElements is < 1 or > 10000 || profile.Pages.Count is < 1 or > 100)
            throw new InvalidDataException("The exploration command, time, state or tree budgets are invalid.");
        if (target.ProcessId <= 0 || target.WindowHandle <= 0 || target.ProcessStartedAtUtc == default
            || !string.Equals(target.ProcessName, profile.ProcessName, StringComparison.OrdinalIgnoreCase)
            || report.Target != target || JsonSerializer.Serialize(report.Profile, Json.Options) != JsonSerializer.Serialize(profile, Json.Options))
            throw new InvalidDataException("The report, approved profile and original target must agree before any interaction.");
        if (profile.Pages.Select(page => page.Id).Distinct(StringComparer.Ordinal).Count() != profile.Pages.Count
            || profile.Pages.Count(page => page.Id == profile.InitialPageId) != 1)
            throw new InvalidDataException("Page identifiers must be unique and include exactly one initial page.");
    }

    private void InitializePages()
    {
        if (report.Pages.Count != 0 || report.States.Count != 0)
            throw new InvalidOperationException("Use a new exploration report; a completed or partial report is immutable evidence, not a resume instruction.");
        foreach (var page in profile.Pages)
        {
            var result = new ExplorePageResult { PageId = page.Id, Name = page.Name, ScrollStatus = page.Scroll is null ? "not-requested" : "pending" };
            if (page.Scroll is not null)
                result.Interactions.Add(new() { Kind = "scroll", ControlId = page.Scroll.ContainerId });
            result.Interactions.AddRange(page.Dropdowns.Select(dropdown => new ExploreInteractionResult { Kind = "dropdown", ControlId = dropdown.ControlId }));
            result.Interactions.AddRange(page.Dialogs.Select(dialog => new ExploreInteractionResult { Kind = "dialog", ControlId = dialog.RootId }));
            report.Pages.Add(result);
        }
    }

    private bool CoverageComplete() => report.Pages.Count == profile.Pages.Count
        && report.Pages.All(page => page.NavigationStatus == "captured" && page.ScrollStatus is "end-observed" or "not-requested"
            && page.Interactions.All(interaction => interaction.Status == "captured") && page.Error is null)
        && report.States.Count > 0 && report.States.All(state => state.Status == "captured");

    private void CheckBudget()
    {
        if (cleaningUp)
        {
            if (++cleanupCommands > 48) throw new ExploreBudgetException("The bounded safe-cleanup command budget was exhausted.");
        }
        else if (elapsed.Elapsed >= TimeSpan.FromSeconds(profile.MaxDurationSeconds)
            || nativeCommands >= 128 + profile.MaxStates * 100 + profile.Pages.Count * 40)
            throw new ExploreBudgetException("The bounded run duration or native-command budget was exhausted.");
    }

    private void RequireStateBudget()
    {
        if (report.States.Count >= profile.MaxStates)
            throw new ExploreBudgetException("The maximum captured-state budget was reached; further declared states were not tested.");
    }

    private async Task<SurveyRestoration> RestorePageAsync()
    {
        if (!originalObserved)
            return new("not-attempted", "The initial page was not safely observed; no page restoration was attempted.");
        if (monitor.Violation is not null)
            return new("not-attempted", "A scope/environment stop is latched. No input, focus restoration or page restoration was attempted.");
        if (overlayOpen)
            return new("incomplete", "A declared overlay could not be safely closed. Underlying-page navigation was not attempted.");
        cleaningUp = true;
        cleanupCommands = 0;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var initial = profile.Pages.Single(page => page.Id == profile.InitialPageId);
            await NavigateAsync(initial, cleanup.Token);
            return new("restored-navigation",
                "The originally observed page is ready again. Control restoration is recorded per interaction; focus and application-data rollback are not claimed.");
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            if (report.Status == "finished")
            {
                report.Status = monitor.Violation is not null ? "paused" : "partial";
                report.Error = "The original page could not be verified after restoration: " + Error(ex);
            }
            return new("incomplete", Error(ex));
        }
        finally { cleaningUp = false; }
    }

    private async Task ProbeAsync(CancellationToken token)
    {
        var probe = profile.ScopeProbe!;
        var page = profile.Pages.Single(item => item.Id == probe.PageId);
        if (report.Restoration is { } ordinary)
            report.Events.Add(new(DateTimeOffset.UtcNow, "ordinary-restoration",
                $"{ordinary.Status}: {ordinary.Message} This outcome precedes the optional scope probe.",
                target.WindowHandle, target.ProcessId));
        report.Restoration = ProbeRestoration(stopped: false);
        await NavigateAsync(page, token);
        var tree = await InspectAsync(target, token);
        RequirePage(tree, page.Id);
        var opener = await ButtonAsync(tree, probe.OpenerId, token);
        report.GuardProbe.Status = "not-tested";
        report.GuardProbe.Reason = "The approved probe opener was triggered; a real native boundary violation is required.";
        var triggered = false;
        try
        {
            // Deliberately no provisional-popup allowance for the auxiliary scope probe.
            await UiAsync(target, ["invoke", opener.Selector], token, input: true,
                issuing: () => triggered = true);
            for (var poll = 0; poll < 20 && monitor.Violation is null; poll++)
            {
                await Task.Delay(100, token);
                monitor.Check(target);
            }
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex) && triggered && monitor.Violation is not null) { }
        if (triggered && monitor.Violation is { Kind: "window-boundary-changed" } boundary
            && monitor.ViolationWindow is { } observedWindow && observedWindow.ProcessId == target.ProcessId
            && observedWindow.Handle != target.WindowHandle && observedWindow.Title == probe.WindowTitle
            && report.GuardProbe.ActionsAfterBoundaryViolation == 0)
        {
            report.GuardProbe.Status = "stopped";
            report.GuardProbe.Reason = "The declared probe title and exact original-process ownership were observed at an unapproved window boundary. No later input or restoration was sent. " + boundary.Message;
            report.Restoration = ProbeRestoration(stopped: true);
        }
        else
        {
            report.GuardProbe.Status = "not-tested";
            report.GuardProbe.Reason = monitor.Violation is null
                ? "No qualifying native window-boundary event was observed. The probe did not pass."
                : "The run stopped for a different interruption; timing alone does not prove the deliberate probe.";
            report.Status = monitor.Violation is null ? "partial" : "paused";
            report.Error = report.GuardProbe.Reason;
            report.Restoration = ProbeRestoration(stopped: monitor.Violation is not null);
        }
        // No actions follow the probe, including a second original-page restoration.
    }

    private static SurveyRestoration ProbeRestoration(bool stopped) => new(
        stopped ? "not-attempted" : "incomplete",
        "The ordinary path's restoration preceded optional probe navigation and is not a final restoration outcome. "
        + (stopped
            ? "A scope stop is latched; no post-boundary page, focus or control restoration was attempted. "
            : "Final page, focus and control restoration after the optional probe has not been verified. ")
        + "Owned-process disposal is lifecycle cleanup, not accessibility restoration.");

    private static string Error(Exception ex) => ex is OperationCanceledException ? "Execution was cancelled; existing evidence was retained." : ex.Message;
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b, double tolerance = PercentTolerance) => Math.Abs(a - b) <= tolerance;
    private sealed class ExploreBudgetException(string message) : InvalidOperationException(message);

    private sealed class GuardedPlatform(NativeExploreSession owner) : IAuditPlatform
    {
        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult(owner.winappVersion);
        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) =>
            throw new InvalidOperationException("Exploration never reattaches to a replacement process or window.");
        public void VerifyTarget(AuditTarget target) => owner.monitor.Check(target);
        public Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
            ExecuteAsync(target, ct => owner.inner.UiAsync(target, arguments, timeout, ct), token, audit: true);
        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token) =>
            ExecuteAsync(target, ct => owner.inner.AxeAsync(target, timeout, ct), token, audit: true);

        public async Task<T> ExecuteAsync<T>(AuditTarget target, Func<CancellationToken, Task<T>> operation,
            CancellationToken token, bool input = false, bool audit = false, bool popup = false, bool closePopup = false, Action? issuing = null)
        {
            token.ThrowIfCancellationRequested();
            owner.monitor.Check(target, input, audit);
            owner.CheckBudget();
            using var guarded = CancellationTokenSource.CreateLinkedTokenSource(token, owner.monitor.StopToken);
            if (popup) owner.monitor.BeginPopupTransition(TimeSpan.FromSeconds(Math.Min(10, owner.profile.TimeoutSeconds)));
            if (closePopup) owner.monitor.BeginPopupClose();
            guarded.Token.ThrowIfCancellationRequested();
            owner.nativeCommands++;
            var clock = Stopwatch.StartNew();
            try
            {
                issuing?.Invoke();
                return await operation(guarded.Token);
            }
            finally
            {
                owner.nativeMilliseconds += clock.ElapsedMilliseconds;
                owner.monitor.Check(target);
            }
        }
    }
}
