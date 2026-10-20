using System.Diagnostics;

namespace AccessLoop;

public sealed record SurveyProgress(
    string Stage, string? PageId, string? PageName, int CapturedPages, int TotalPages, string? Message = null);

public sealed class SurveyRunner(
    SurveyStore store, ISurveySession session, SurveyProfile profile, Action<SurveyProgress>? progress = null)
{
    public async Task RunAsync(SurveyState state, SurveyOptions options, CancellationToken token)
    {
        if (!options.AcceptNavigation || (options.Resume is not null && !options.ConfirmUnchanged))
            throw new InvalidDataException("Navigation and resume-state confirmation are required before running a survey.");
        if (state.Invocations.Count >= 200)
            throw new InvalidDataException("The survey invocation limit was reached; start a new job.");
        var invocation = new SurveyInvocation
        {
            Number = state.Invocations.Count + 1, StartedAtUtc = DateTimeOffset.UtcNow,
            Resuming = options.Resume is not null, OperatorConfirmedUnchangedState = options.ConfirmUnchanged,
            RetryPages = options.RetryPages
        };
        state.Invocations.Add(invocation);
        var invocationClock = Stopwatch.StartNew();
        state.Error = null;
        try
        {
            session.Preflight();
            EnsureCompatible(state, session.Target, session.GetContext(), state.Tools);
            if (state.Inventory is null)
            {
                state.Status = "discovering";
                state.OriginalNavigation ??= await session.ReadNavigationStateAsync(token);
                store.Save(state);
                Emit("discovering", null, "Expanding only the navigation groups declared by the profile.");
                var inventory = await session.DiscoverAsync(state.OriginalNavigation, token);
                store.FreezeInventory(state, inventory, profile);
            }
            else
            {
                Emit("validating-inventory", null);
                await session.ValidateInventoryAsync(state.Inventory, token);
            }
            var expected = state.Pages.Select(page => page.Page.Id).ToHashSet(StringComparer.Ordinal);
            if (options.RetryPages.Any(id => !expected.Contains(id)))
                throw new InvalidDataException("A requested retry page is not in the frozen page inventory.");
            foreach (var page in state.Pages)
            {
                foreach (var attempt in page.Attempts.Where(attempt => attempt.Status == "running"))
                {
                    attempt.Status = "interrupted";
                    attempt.FinishedAtUtc = DateTimeOffset.UtcNow;
                    attempt.Error = "The previous invocation ended before post-navigation verification was recorded.";
                    page.Status = "blocked";
                }
            }
            var work = state.Pages.Select((page, index) => (Page: page, Index: index))
                .Where(item => options.RetryPages.Count > 0 ? options.RetryPages.Contains(item.Page.Page.Id, StringComparer.Ordinal)
                    : item.Page.Status is "pending" or "blocked").ToArray();
            state.Status = "running";
            store.Save(state);
            var attempted = 0;
            foreach (var item in work)
            {
                token.ThrowIfCancellationRequested();
                session.Preflight();
                EnsureCompatible(state, session.Target, session.GetContext(), state.Tools);
                if (invocationClock.Elapsed > TimeSpan.FromMinutes(30))
                {
                    Pause("The 30-minute invocation budget was reached. Completed pages remain available for resume.");
                    break;
                }
                var directory = store.CreateAttempt(item.Page, item.Index, out var attempt);
                store.Save(state);
                Emit("navigating", item.Page.Page);
                try
                {
                    var watch = Stopwatch.StartNew();
                    attempt.Before = await session.NavigateAsync(item.Page.Page, token);
                    attempt.NavigationMilliseconds = watch.ElapsedMilliseconds;
                    if (!SurveyStore.Ready(profile, item.Page.Page, attempt.Before))
                        throw new InvalidDataException("The expected navigation selection and content heading were not both confirmed.");
                    store.Save(state, render: false);
                    Emit("scanning", item.Page.Page);
                    watch.Restart();
                    var report = await session.ScanAsync(directory, token);
                    attempt.ScanMilliseconds = watch.ElapsedMilliseconds;
                    var scanPath = Path.Combine(directory, "scan.json");
                    var verified = AuditFiles.ReadVerified(scanPath);
                    if (verified.RunId != report.RunId || verified.Target.ProcessId != state.Target.ProcessId
                        || verified.Target.WindowHandle != state.Target.WindowHandle
                        || verified.Target.ProcessStartedAtUtc != state.Target.ProcessStartedAtUtc)
                        throw new InvalidDataException("Page evidence does not match this survey's fixed target.");
                    attempt.ScanSha256 = AuditFiles.Hash(scanPath);
                    attempt.CaptureStatus = verified.Status;
                    attempt.RuleFailures = verified.RuleFailureCount;
                    attempt.Nodes = verified.Snapshot is { } snapshot ? AuditJson.Array(snapshot, "nodes").GetArrayLength() : 0;
                    Emit("verifying-page", item.Page.Page);
                    attempt.After = await session.VerifyPageAsync(item.Page.Page, token);
                    if (!SurveyStore.Ready(profile, item.Page.Page, attempt.After))
                        throw new InvalidDataException("The selected page or its heading changed during capture; the report is preserved but not counted as verified page coverage.");
                    if (verified.Status == "blocked" || verified.Error is not null)
                        throw new InvalidOperationException("The page audit had an execution error: " + (verified.Error ?? verified.Status));
                    attempt.Status = "captured";
                    item.Page.Status = "captured";
                }
                catch (SurveyBlockedException exception)
                {
                    Block(item.Page, attempt, exception.Message);
                    Pause($"{exception.Code}: {exception.Message}");
                    break;
                }
                catch (OperationCanceledException)
                {
                    attempt.Status = "interrupted";
                    attempt.Error = "Cancelled before this attempt completed; resume will use a new attempt directory.";
                    item.Page.Status = "blocked";
                    Pause("Cancelled. Completed pages and earlier attempt evidence were retained.");
                    break;
                }
                catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
                {
                    Block(item.Page, attempt, exception.Message);
                    Emit("page-blocked", item.Page.Page, exception.Message);
                }
                finally
                {
                    attempt.FinishedAtUtc = DateTimeOffset.UtcNow;
                    store.Save(state);
                }
                attempted++;
                Emit("page-finished", item.Page.Page, attempt.Status);
                if (options.StopAfter is { } stop && attempted >= stop && state.Pages.Any(page => page.Status != "captured"))
                {
                    Pause($"Paused after {attempted} page attempt(s), as requested by --stop-after.");
                    break;
                }
            }
            if (state.Status != "paused")
            {
                if (state.Pages.Count > 0 && state.Pages.All(page => page.Status == "captured"))
                {
                    state.Status = "finished";
                    invocation.Status = "finished";
                }
                else
                    Pause("Pending or blocked pages remain. Resume to continue or explicitly retry a page.");
            }
        }
        catch (OperationCanceledException)
        {
            Pause("Cancelled. The same job can be resumed after environment and evidence checks.");
        }
        catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
        {
            Pause(exception is SurveyBlockedException blocked ? $"{blocked.Code}: {blocked.Message}" : exception.Message);
        }
        finally
        {
            if (state.OriginalNavigation is not null)
            {
                Emit("restoring-navigation", null);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { invocation.Restoration = await session.RestoreAsync(state.OriginalNavigation, cleanup.Token); }
                catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
                {
                    invocation.Restoration = new("incomplete", exception.Message);
                }
            }
            invocation.Performance = session.Performance;
            invocation.FinishedAtUtc = DateTimeOffset.UtcNow;
            store.Save(state);
            Emit(state.Status, null, state.Error);
        }

        void Pause(string reason)
        {
            state.Status = "paused";
            state.Error = reason;
            invocation.Status = "paused";
            invocation.Reason = reason;
        }

        void Emit(string stage, SurveyPageDefinition? page, string? message = null) =>
            progress?.Invoke(new(stage, page?.Id, page?.Name, state.Pages.Count(item => item.Status == "captured"), state.Pages.Count, message));
    }

    private static void Block(SurveyPageState page, SurveyAttempt attempt, string error)
    {
        page.Status = "blocked";
        attempt.Status = "blocked";
        attempt.Error = error;
    }

    public static void EnsureCompatible(SurveyState state, AuditTarget target, SurveyContext context, SurveyToolIdentity tools)
    {
        if (state.Target.ProcessId != target.ProcessId || state.Target.WindowHandle != target.WindowHandle
            || state.Target.ProcessStartedAtUtc != target.ProcessStartedAtUtc || state.Target.ProcessName != target.ProcessName)
            throw new SurveyBlockedException("target-changed", "The original app process/window changed. Start a new survey; previous reports are retained.", false);
        if (state.Context.Application != context.Application)
            throw new SurveyBlockedException("application-changed", "The installed app version or binary changed. Start a new survey.", false);
        if (state.Context.Environment != context.Environment)
            throw new SurveyBlockedException("environment-changed", "Window size, DPI, OS, UI culture or system contrast/theme settings changed. Restore the original environment or start a new survey.", true);
        if (state.Tools != tools)
            throw new SurveyBlockedException("tools-changed", "The native CLI or AccessLoop runner changed. Start a new survey rather than mixing tool versions.", false);
    }

    public static int ExitCode(SurveyState state)
    {
        if (state.Status == "paused")
            return 3;
        if (state.Status != "finished" || state.Pages.Any(page => page.Attempts.LastOrDefault()?.CaptureStatus != "completed"))
            return 2;
        return state.Pages.Sum(page => page.Attempts.Last().RuleFailures) > 0 ? 1 : 0;
    }
}
