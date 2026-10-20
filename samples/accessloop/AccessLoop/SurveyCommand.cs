using System.Text.Json;

namespace AccessLoop;

public static class SurveyCommand
{
    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var options = SurveyOptionsParser.Parse(args);
        var executable = ResolveExecutable(options.Winapp);
        var platform = new NativeAuditPlatform(executable);
        var version = await platform.VersionAsync(token);
        var tools = new SurveyToolIdentity(version, AuditFiles.Hash(executable), AuditFiles.Hash(typeof(Program).Assembly.Location));
        SurveyState state;
        SurveyProfileFile input;
        SurveyStore store;
        ISurveySession session;
        IDisposable? locked = null;
        try
        {
            if (options.Resume is { } resume)
            {
                store = new SurveyStore(resume);
                locked = store.Lock();
                (state, input) = store.Load();
                if (options.RetryPages.Any(id => !state.Pages.Any(page => page.Page.Id == id)))
                    throw new InvalidDataException("A requested retry page is outside the saved page inventory.");
                session = new NativeSurveySession(input.Profile, platform, state.Target, version, state.Screenshot);
                session.Preflight();
                SurveyRunner.EnsureCompatible(state, session.Target, session.GetContext(), tools);
            }
            else
            {
                input = SurveyProfileFile.Read(options.Profile!);
                var auditOptions = new AuditOptions(options.ProcessId, options.WindowHandle, executable, null,
                    input.Profile.TreeDepth, 2000, input.Profile.TimeoutSeconds * 1000, 0, 0, false, !options.NoScreenshot);
                var target = await platform.AttachAsync(auditOptions, token);
                if (!target.ProcessName.Equals(input.Profile.ProcessName, StringComparison.Ordinal))
                    throw new InvalidDataException("The selected process does not match this navigation profile.");
                session = new NativeSurveySession(input.Profile, platform, target, version, !options.NoScreenshot);
                session.Preflight();
                var context = session.GetContext();
                var output = Path.GetFullPath(options.Output!);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                AuditFiles.CreateNewDirectory(output);
                store = new SurveyStore(output);
                locked = store.Lock();
                state = new()
                {
                    RunId = Guid.NewGuid().ToString("N"), Name = input.Profile.Name,
                    StartedAtUtc = DateTimeOffset.UtcNow, Target = target,
                    ProfileSha256 = input.Sha256, Tools = tools, Context = context, Screenshot = !options.NoScreenshot
                };
                store.Initialize(input, state);
            }
            var runner = new SurveyRunner(store, session, input.Profile, progress =>
            {
                if (options.Json)
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "survey-progress", progress }, Json.Options));
                else
                    Console.Error.WriteLine($"[{progress.CapturedPages}/{progress.TotalPages}] {progress.Stage}" +
                        (progress.PageName is null ? "" : $": {progress.PageName}") +
                        (progress.Message is null ? "" : $" — {progress.Message}"));
            });
            await runner.RunAsync(state, options, token);
            var latest = state.Pages.Select(page => page.Attempts.LastOrDefault()).Where(attempt => attempt is not null).ToArray();
            var summary = new
            {
                state.RunId, state.Status,
                expectedPages = state.Pages.Count,
                capturedPages = state.Pages.Count(page => page.Status == "captured"),
                pendingPages = state.Pages.Count(page => page.Status == "pending"),
                blockedPages = state.Pages.Count(page => page.Status == "blocked"),
                completeCaptures = latest.Count(attempt => attempt!.Status == "captured" && attempt.CaptureStatus == "completed"),
                partialCaptures = latest.Count(attempt => attempt!.Status == "captured" && attempt.CaptureStatus == "partial"),
                ruleFailureOccurrences = latest.Where(attempt => attempt!.Status == "captured").Sum(attempt => attempt!.RuleFailures),
                report = Path.Combine(store.Root, "survey-report.html"),
                stateFile = Path.Combine(store.Root, "survey-state.json")
            };
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(summary, Json.Options));
            else
            {
                Console.WriteLine($"Survey {state.Status}: {summary.capturedPages}/{summary.expectedPages} pages captured; " +
                    $"{summary.partialCaptures} partial captures; {summary.ruleFailureOccurrences} rule-failure occurrences.");
                Console.WriteLine($"Report: {summary.report}");
                Console.WriteLine("Navigation coverage, capture completeness and accessibility findings are separate; this is not a compliance verdict.");
            }
            return SurveyRunner.ExitCode(state);
        }
        finally { locked?.Dispose(); }
    }

    internal static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.IndexOfAny(['\\', '/']) >= 0)
        {
            var absolute = Path.GetFullPath(executable);
            if (!File.Exists(absolute))
                throw new FileNotFoundException("The native WinAppCLI executable was not found.", absolute);
            return absolute;
        }
        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Native WinAppCLI was not found. Pass --winapp <absolute-path-to-winapp.exe>.");
    }
}
