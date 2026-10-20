using System.Text.RegularExpressions;

namespace AccessLoop;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"]
            or ["scan", "--help"] or ["assessment-template", "--help"] or ["assess", "--help"]
            or ["journey", "--help"] or ["survey", "--help"] or ["explore", "--help"] or ["loop", "--help"])
        {
            Console.WriteLine("""
                AccessLoop - scan workflows, choose a finding, and verify one repair at a time.

                accessloop loop <preset.json> --accept-actions --output <new-directory> [--winapp <exe>]
                                [--apply [--copilot <exe> | --proposals <reviewed-folder>]] [--no-screenshot] [--json]
                accessloop loop --replay <run-directory> [--round <round-id>] --accept-actions
                                --output <new-directory> [--winapp <exe>] [--json]

                loop copies your source demo into a disposable workspace, builds and launches only that copy,
                executes a preset route with real keyboard checks and per-state audits, then reproduces it
                in a new owned process. Every action keeps actual observations and native command evidence.
                --apply additionally approves bounded, reviewed-name source repairs and same-route rebuild/retest.
                Without --proposals, repair requests send a bounded source snippet and finding to Copilot.
                The original fixture is never edited. Archived source versions allow later --replay (baseline by default).
                Route planning is preset, not autonomous AI. Name verification, capture completeness and
                whole-app accessibility are different results. Partial capture remains partial.

                accessloop explore <profile.json> (--launch-owned | --pid <PID> | --window <HWND>)
                                   --accept-interactions --output <new-directory> [--winapp <exe>]
                                   [--no-screenshot] [--probe-scope-guard] [--json]

                explore audits declared pages, finite scroll positions, dropdown disclosure and known dialogs.
                --accept-interactions does not approve selecting options, feature-setting changes or source repairs.
                --launch-owned explicitly builds, launches and closes the profile's local source demo.
                Without it, the command only attaches and never owns the existing application's lifecycle.
                --probe-scope-guard is an owned-demo-only, explicitly declared auxiliary-window safety exercise.
                Each state keeps separate immutable audit evidence. Unknown scope/coverage remains blocked or partial.
                Exploration exit codes: 0 finished/complete/no failures; 1 complete with rule failures;
                                        2 partial/incomplete; 3 paused. Start a fresh run after an interruption.

                accessloop survey <profile.json> (--pid <PID> | --window <HWND>) --accept-navigation
                                  --output <new-directory> [--winapp <exe>] [--stop-after <pages>] [--no-screenshot] [--json]
                accessloop survey --resume <run-directory> --accept-navigation --confirm-unchanged
                                  [--winapp <exe>] [--retry-page <ID> ...] [--stop-after <pages>] [--json]

                survey tests an existing app without source, build, restart or settings-value changes.
                Only profiled navigation groups and internal pages may be operated. No AI calls or source repairs.
                Ctrl+C or --stop-after pauses; resume checks original process/window, app/tool versions and environment.
                --confirm-unchanged declares that app settings/test data did not change; identity checks cannot prove that.
                Each retry writes a new attempt directory; previous reports and raw evidence are never overwritten.
                Survey exit codes: 0 finished/no failures/complete capture; 1 finished/has failures/complete capture;
                                   2 partial capture or preflight error; 3 paused. None is whole-app WCAG compliance.

                accessloop journey <journey.json> --accept-navigation [--winapp <exe>] [--output <new-directory>]
                                   [--copilot-plan | --plan <json>] [--apply] [--copilot <exe>]
                                   [--proposals <reviewed-folder>] [--no-screenshot] [--json]

                journey builds a source-owned demo, discovers approved navigation and audits every declared page.
                --accept-navigation approves launch/navigation AND the declared real-keyboard exercises.
                --apply separately approves supported Name-only edits and full frozen-route regression after each edit.
                Without --copilot-plan the declared visit order is used (no AI planner).
                --proposals supplies reviewed <TargetAutomationId>.json files instead of calling Copilot for repairs.
                Capture completeness and required repair verification are separate. Privacy-withheld fields stay partial;
                missing pages/controls, truncation, timeouts and changed acceptance block repair verification.

                accessloop scan (--app <name|title|PID> | --pid <PID> | --window <HWND>) [--winapp <exe>] [--output <new-directory>]
                                [--depth 1..50] [--max-elements 1..10000] [--timeout-ms 1000..120000]
                                [--no-screenshot] [--observe-seconds 1..60]
                                [--tab-steps 1..30 --accept-input] [--json]
                                [--navigation standard-tabs --accept-navigation [--max-pages 1..100]]
                accessloop assessment-template <scan.json> --output <new-file> --author <name>
                                               [--source human|ai] [--model <model-for-ai>]
                accessloop assess <scan.json> --assessment <file> --output <new-directory>

                scan attaches to an already-running window; it never builds, closes or restarts the app.
                Default scan is read-only. --tab-steps explicitly opts into input in BOTH directions.
                A probe budget records observations, not logical order or a keyboard-trap verdict.
                --navigation standard-tabs discovers standard Tab/TabItem controls and selects their pages
                without an agent or profile, then writes per-page audits and an aggregate report.
                --accept-navigation approves tab-selection side effects; --max-pages defaults to 20.
                Disabled, offscreen, unsupported or blocked tabs remain explicit coverage gaps.
                This mode does not click buttons, open dialogs or combine with --tab-steps.
                It attempts to restore tab selection, not app state. Assess individual pages' scan.json files.
                Automatic scan.json stays unchanged when supplementary assessments are attached.
                Audit exit codes: 0 completed/no rule failures; 1 completed/has rule failures;
                                  2 blocked or partially executed. Zero is not a compliance verdict.

                accessloop check  <suite.json|scenario.json> [--winapp <exe>] [--output <new-directory>]
                accessloop repair <suite.json|scenario.json> [--finding <ID>] [--apply]
                                  [--proposal <json>] [--copilot <exe>] [--winapp <exe>] [--output <new-directory>]

                Multiple findings require --finding. repair only proposes by default.
                --apply authorizes one constrained XAML Name edit, then reruns every scenario.
                Existing unrelated failures may remain; new regressions reject the patch.
                A fixed finding does not mean the whole suite is clean. Read both statuses.
                Exit codes: 0 check passed / selected repair fixed; 1 failed / regression;
                            2 inconclusive/error; 3 selection, approval, or manual review required.
                """);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args[0] == "scan")
                return await AuditCommand.ScanAsync(args, cancellation.Token);
            if (args[0] == "loop")
                return await LoopCommand.ExecuteAsync(args, cancellation.Token);
            if (args[0] == "survey")
                return await SurveyCommand.ExecuteAsync(args, cancellation.Token);
            if (args[0] == "explore")
                return await ExploreCommand.ExecuteAsync(args, cancellation.Token,
                    (platform, target, version, screenshot) => new NativeExploreSession(platform, target, version, screenshot),
                    ExploreReportRenderer.Write);
            if (args[0] == "journey")
                return await JourneyCommand.ExecuteAsync(args, cancellation.Token);
            if (args[0] == "_axe-worker")
                return await NativeAuditPlatform.RunAxeWorkerAsync(args, cancellation.Token);
            if (args[0] is "assessment-template" or "assess")
                return AuditCommand.Assessment(args);
            var options = WorkbenchOptions.Parse(args);
            var input = SuiteInput.Load(options.Input);
            var output = options.Output is null
                ? Path.Combine(Path.GetDirectoryName(input.InputPath)!, ".accessloop",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}")
                : Path.GetFullPath(options.Output);
            if (Directory.Exists(output) || File.Exists(output))
                throw new IOException($"Evidence directory already exists: {output}. Choose a new directory.");
            var version = await CheckVersionAsync(options.Winapp, cancellation.Token);
            Directory.CreateDirectory(output);
            var runner = new WorkbenchRunner(new ScenarioRunner(new WinappLauncher(options.Winapp), new AxeWindowsScanner()));
            var report = await runner.RunAsync(input, options, version, output, cancellation.Token);
            Json.Write(Path.Combine(output, "report.json"), report);
            ReportRenderer.Write(Path.Combine(output, "report.html"), report);
            Console.WriteLine($"AccessLoop: {report.Status}; suite: {report.SuiteStatus}; remaining findings: {report.RemainingFindings.Count}");
            if (report.Error is not null)
                Console.WriteLine(report.Error);
            foreach (var error in report.VerificationErrors)
                Console.WriteLine(error);
            foreach (var result in report.Before.Concat(report.After).Where(r => r.Observation.Error is not null))
                Console.WriteLine($"{result.Observation.Phase}/{result.Scenario.Name}: {result.Observation.Error}");
            Console.WriteLine($"Evidence: {Path.Combine(output, "report.html")}");
            return ExitCode(report.Status);
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            if (args[0] is "survey" or "explore" or "loop" && args.Contains("--json", StringComparer.Ordinal))
                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = new { code = ex is SurveyBlockedException blocked ? blocked.Code : args[0] + "-error",
                        message = ex is OperationCanceledException ? "Execution cancelled." : ex.Message }
                }, Json.Options));
            else
                Console.Error.WriteLine($"Inconclusive: {(ex is OperationCanceledException ? "Execution cancelled." : ex.Message)}");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public static int ExitCode(string status) => status switch
    {
        "passed" or "fixed" => 0,
        "failed" or "regression" => 1,
        "awaiting-selection" or "awaiting-approval" or "needs-review" => 3,
        _ => 2
    };

    public static void ValidateProposal(SourceCandidate candidate, RepairProposal proposal, Scenario scenario)
    {
        if (proposal.RelativePath != candidate.RelativePath
            || !string.Equals(proposal.SourceSha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase)
            || proposal.AutomationId != scenario.TargetId || proposal.AccessibleName != scenario.ExpectedName
            || string.IsNullOrWhiteSpace(proposal.Explanation))
            throw new InvalidDataException("Proposal must match the original source, exact target, and frozen expected accessible name.");
    }

    private static async Task<string> CheckVersionAsync(string executable, CancellationToken token)
    {
        var result = await ProcessCommand.RunAsync(executable, ["--version"], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20), token);
        var match = Regex.Match(result.Stdout, @"\d+\.\d+\.\d+");
        if (result.ExitCode != 0 || !match.Success || !Version.TryParse(match.Value, out var version)
            || version < new Version(0, 6, 0))
            throw new InvalidOperationException("AccessLoop requires native WinAppCLI 0.6.0 or newer. Pass --winapp <path-to-newer-winapp.exe>.");
        return result.Stdout.Trim();
    }
}

public sealed record WorkbenchOptions(string Command, string Input, string Winapp, string Copilot,
    string? Output, string? Proposal, string? Finding, bool Apply)
{
    public static WorkbenchOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("check" or "repair"))
            throw new ArgumentException("Use check <input.json> or repair <input.json>. See --help.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--apply" && !apply)
            {
                apply = true;
                continue;
            }
            if (key is not ("--winapp" or "--copilot" or "--output" or "--proposal" or "--finding")
                || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i])
                || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, args[i]))
                throw new ArgumentException($"Unknown, duplicate, or incomplete option: {key}.");
        }
        if (args[0] == "check" && (apply || values.ContainsKey("--proposal")
            || values.ContainsKey("--copilot") || values.ContainsKey("--finding")))
            throw new ArgumentException("Repair options are not valid for check.");
        return new(args[0], args[1], values.GetValueOrDefault("--winapp", "winapp"),
            values.GetValueOrDefault("--copilot", "copilot"), values.GetValueOrDefault("--output"),
            values.GetValueOrDefault("--proposal"), values.GetValueOrDefault("--finding"), apply);
    }
}
