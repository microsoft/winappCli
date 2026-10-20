using System.Text.Json;

namespace AccessLoop;

public static class ExploreCommand
{
    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args, CancellationToken token,
        Func<IAuditPlatform, AuditTarget, string, bool, IExploreSession> createSession,
        Action<string, ExploreReport> render)
    {
        var options = ExploreOptionsParser.Parse(args);
        var input = ExploreProfileFile.Read(options.Input);
        if (options.LaunchOwned && input.ProjectPath is null)
            throw new InvalidDataException("--launch-owned requires a local project in the profile.");
        if (options.ProbeScopeGuard && input.Profile.ScopeProbe is null)
            throw new InvalidDataException("This profile does not declare an owned scope-guard test window.");
        var output = Path.GetFullPath(options.Output!);
        if (input.ProjectPath is { } project)
        {
            var projectRoot = Path.GetDirectoryName(project)!;
            if (output.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(Path.TrimEndingDirectorySeparator(projectRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Store exploration evidence outside the owned source project's directory.");
        }
        var executable = SurveyCommand.ResolveExecutable(options.Winapp);
        var platform = new NativeAuditPlatform(executable);
        var version = await platform.VersionAsync(token);
        var tools = new SurveyToolIdentity(version, AuditFiles.Hash(executable), AuditFiles.Hash(typeof(Program).Assembly.Location));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        AuditFiles.CreateNewDirectory(output);
        AuditFiles.WriteNewBytes(Path.Combine(output, "profile.json"), input.Bytes);
        IAppSession? owned = null;
        try
        {
            if (options.LaunchOwned)
            {
                var launch = Path.Combine(output, "launch");
                Directory.CreateDirectory(launch);
                owned = await new WinappLauncher(executable).LaunchProjectAsync(
                    input.ProjectPath!, input.Profile.TimeoutSeconds, launch, token);
            }
            var auditOptions = new AuditOptions(owned?.ProcessId ?? options.ProcessId, options.WindowHandle,
                executable, null, input.Profile.TreeDepth, input.Profile.MaxElements,
                input.Profile.TimeoutSeconds * 1000, 0, 0, false, !options.NoScreenshot);
            var target = await platform.AttachAsync(auditOptions, token);
            if (!target.ProcessName.Equals(input.Profile.ProcessName, StringComparison.Ordinal))
                throw new InvalidDataException("The selected process does not match the approved exploration profile.");
            var report = new ExploreReport
            {
                RunId = Guid.NewGuid().ToString("N"), Name = input.Profile.Name, Target = target,
                ProfileSha256 = input.Sha256, Tools = tools, Profile = input.Profile,
                OwnedApplication = options.LaunchOwned, Screenshot = !options.NoScreenshot
            };
            AuditFiles.WriteNew(Path.Combine(output, "explore-request.json"), new
            {
                report.RunId, report.StartedAtUtc, report.Target, report.ProfileSha256, report.Tools,
                report.OwnedApplication, report.Screenshot,
                effects = "Only declared navigation, bounded scrolling, disclosure and safe dialog dismissal. No option selection, settings changes, AI calls or source repair.",
                options.ProbeScopeGuard
            });
            SaveCheckpoint(output, report);
            var session = createSession(platform, target, version, !options.NoScreenshot);
            try
            {
                await session.ExecuteAsync(input.Profile, report, output, options.ProbeScopeGuard, progress =>
                {
                    SaveCheckpoint(output, report);
                    if (options.Json)
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "explore-progress", progress }, Json.Options));
                    else
                        Console.Error.WriteLine($"[{progress.Stage}] {progress.PageId} {progress.StateId} {progress.Message}".TrimEnd());
                }, token);
            }
            catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
            {
                report.Status = "paused";
                report.Error = exception is OperationCanceledException ? "Exploration cancelled; completed state evidence was retained." : exception.Message;
            }
            finally
            {
                report.FinishedAtUtc ??= DateTimeOffset.UtcNow;
                SaveCheckpoint(output, report);
                if (session is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (session is IDisposable disposable)
                    disposable.Dispose();
            }
            AuditFiles.WriteNew(Path.Combine(output, "explore-report.json"), report);
            var html = Path.Combine(output, "explore-report.html");
            render(html, report);
            var captured = report.States.Where(state => state.Status == "captured").ToArray();
            var summary = new
            {
                report.RunId, report.Status,
                expectedPages = report.Profile.Pages.Count,
                reachedPages = report.Pages.Count(page => page.NavigationStatus == "captured"),
                capturedStates = captured.Length,
                partialCaptures = captured.Count(state => state.CaptureStatus == "partial"),
                ruleFailureOccurrences = captured.Sum(state => state.RuleFailures),
                guardProbe = report.GuardProbe.Status,
                report = html,
                reportJson = Path.Combine(output, "explore-report.json")
            };
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(summary, Json.Options));
            else
            {
                Console.WriteLine($"Exploration {report.Status}: {summary.reachedPages}/{summary.expectedPages} pages, {summary.capturedStates} captured states.");
                Console.WriteLine($"Rule-failure occurrences: {summary.ruleFailureOccurrences}; partial captures: {summary.partialCaptures}.");
                if (report.Error is not null)
                    Console.Error.WriteLine(report.Error);
                Console.WriteLine($"Report: {html}");
                Console.WriteLine("Declared state coverage and accessibility findings are separate; this is not whole-app compliance.");
            }
            return ExitCode(report);
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    public static int ExitCode(ExploreReport report)
    {
        if (report.Status == "paused")
            return 3;
        if (report.Status != "finished" || report.States.Count == 0
            || report.States.Any(state => state.Status != "captured" || state.CaptureStatus != "completed")
            || report.GuardProbe.Status is not ("not-requested" or "stopped")
            || report.GuardProbe.ActionsAfterBoundaryViolation != 0
            || report.Restoration?.Status is not ("restored-navigation" or "restored")
            || !HasDeclaredCoverage(report))
            return 2;
        return report.States.Sum(state => state.RuleFailures) > 0 ? 1 : 0;
    }

    private static bool HasDeclaredCoverage(ExploreReport report)
    {
        if (report.Pages.Count != report.Profile.Pages.Count
            || report.Pages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count() != report.Pages.Count)
            return false;
        foreach (var declared in report.Profile.Pages)
        {
            var page = report.Pages.SingleOrDefault(item => item.PageId == declared.Id);
            if (page?.NavigationStatus != "captured"
                || !report.States.Any(state => state.PageId == declared.Id && state.Kind == "base")
                || (declared.Scroll is not null && page.ScrollStatus != "end-observed"))
                return false;
            foreach (var dropdown in declared.Dropdowns)
                if (!page.Interactions.Any(item => item.Kind == "dropdown" && item.ControlId == dropdown.ControlId
                    && item.Status == "captured"))
                    return false;
            foreach (var dialog in declared.Dialogs)
                if (!page.Interactions.Any(item => item.Kind == "dialog" && item.ControlId == dialog.RootId
                    && item.Status == "captured"))
                    return false;
        }
        return true;
    }

    private static void SaveCheckpoint(string output, ExploreReport report)
    {
        var temporary = Path.Combine(output, $"explore-progress-{Guid.NewGuid():N}.json");
        try
        {
            AuditFiles.WriteNew(temporary, report);
            File.Move(temporary, Path.Combine(output, "explore-progress.json"), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
