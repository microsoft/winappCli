using System.Text.Json;

namespace AccessLoop;

public static class LoopCommand
{
    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var options = LoopOptions.Parse(args);
        var winapp = SurveyCommand.ResolveExecutable(options.Winapp);
        var version = await new NativeAuditPlatform(winapp).VersionAsync(token);
        var tools = new SurveyToolIdentity(version, AuditFiles.Hash(winapp), AuditFiles.Hash(typeof(Program).Assembly.Location));
        if (options.Proposals is { } proposalDirectory)
            AuditFiles.EnsureDirectory(proposalDirectory);
        LoopInput? input = null;
        LoopReport? previous = null;
        LoopVerifiedRound? recorded = null;
        LoopProfile profile;
        string originalProject;
        var screenshot = !options.NoScreenshot;
        if (options.Replay is { } replayRoot)
        {
            var directory = Path.GetFullPath(replayRoot);
            var previousPath = AuditFiles.ResolveArtifact(directory, "loop-report.json");
            using var document = AuditFiles.ParseStrict(AuditFiles.ReadBytes(previousPath, AuditFiles.MaxJsonBytes));
            previous = AuditFiles.Deserialize<LoopReport>(document.RootElement);
            if (previous.SchemaVersion != 1 || previous.PlanSource != "preset"
                || previous.Directory is null || previous.Profile is null || previous.Tools is null || previous.Rounds is null
                || Path.GetFullPath(previous.Directory) != directory
                || previous.PlanSha256 != AuditFiles.Hash(AuditFiles.ResolveArtifact(directory, "plan.json")))
                throw new InvalidDataException("The selected loop report is not bound to its frozen preset.");
            if (previous.Tools.NativeSha256 != tools.NativeSha256 || previous.Tools.RunnerSha256 != tools.RunnerSha256)
                throw new InvalidDataException("Replay requires the recorded native and runner binaries; tool changes need a new baseline.");
            var round = previous.Rounds.SingleOrDefault(item => item.Id == options.Round)
                ?? throw new InvalidDataException("The requested recorded round does not exist.");
            recorded = LoopEvidence.VerifyRound(previous.Profile, round, directory);
            var source = round.SourceDirectory ?? throw new InvalidDataException("Recorded sources are unavailable.");
            originalProject = AuditFiles.ResolveArtifact(source, Path.GetFileName(previous.Profile.Project));
            profile = previous.Profile;
            screenshot = previous.Screenshot;
        }
        else
        {
            input = LoopInput.Read(options.Input!);
            profile = input.Profile;
            originalProject = profile.Project;
        }
        foreach (var target in profile.RepairTargets)
            if (SourceRepair.Locate(originalProject, target.AutomationId).Count != 1)
                throw new InvalidDataException($"Declared target '{target.AutomationId}' must map to one supported static XAML Button before starting this source-owned loop.");
        var output = Path.GetFullPath(options.Output);
        var sourceRoot = Path.GetDirectoryName(originalProject)!;
        if (output.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Keep loop evidence outside the input source project.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        AuditFiles.CreateNewDirectory(output);
        var workspace = LoopSourceWorkspace.Copy(originalProject, Path.Combine(output, "workspace"));
        profile = profile with { Project = workspace };
        var plan = LoopInput.PlanBytes(profile);
        AuditFiles.WriteNewBytes(Path.Combine(output, "plan.json"), plan);
        if (input is not null)
            AuditFiles.WriteNewBytes(Path.Combine(output, "input.json"), input.OriginalBytes);
        var report = new LoopReport
        {
            RunId = Guid.NewGuid().ToString("N"), Name = profile.Name, Directory = output,
            Profile = profile, PlanSha256 = LoopInput.PlanHash(profile), Tools = tools,
            Screenshot = screenshot, ReplayOf = previous is null ? null : $"{previous.Directory}#{options.Round}"
        };
        AuditFiles.WriteNew(Path.Combine(output, "loop-request.json"), new
        {
            report.RunId, report.StartedAtUtc, report.PlanSource, report.PlanSha256, report.Tools,
            report.Screenshot, report.ReplayOf,
            sourceInput = originalProject,
            workingCopy = workspace,
            effects = "Build and launch only the owned source copy. Execute its preset keyboard, navigation, scrolling and in-memory test-state actions. Source repairs require --apply and never change the original fixture.",
            apply = options.Apply,
            proposalSource = options.Apply ? options.Proposals is null ? "copilot" : "reviewed-file" : "not-requested"
        });
        var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
        void Progress(LoopProgress value)
        {
            var line = JsonSerializer.Serialize(new { type = "loop-progress", progress = value }, compact);
            File.AppendAllText(Path.Combine(output, "loop-events.jsonl"), line + Environment.NewLine);
            if (options.Json)
                Console.Error.WriteLine(line);
            else
                Console.Error.WriteLine($"[{value.RoundId}/{value.Stage}] {value.StepId} {value.Message}".TrimEnd());
        }
        var native = new NativeLoopRoundRunner(winapp, version, screenshot, Progress);
        Action? verifyInput = input is null ? null : input.EnsureUnchanged;
        var runner = new LoopRunner(native, Progress, verifyInput);
        LoopRepairAdvisor Advisor() => async (candidate, target, evidence, folder, cancellation) =>
        {
            if (options.Proposals is { } proposals)
                return CopilotAdvisor.ReadProposal(AuditFiles.ResolveArtifact(proposals, target.AutomationId + ".json"));
            var node = AuditJson.Array(evidence.Snapshot ?? throw new InvalidDataException("A repair needs its checkpoint snapshot."), "nodes")
                .EnumerateArray().Single(item => AuditJson.OptionalString(item, "automationId") == target.AutomationId);
            return await CopilotAdvisor.ProposeNameAsync(options.Copilot, candidate, target.ExpectedName,
                AuditJson.OptionalString(node, "name"),
                evidence.Axe ?? throw new InvalidDataException("A real Axe scan is required for a Name proposal."),
                folder, cancellation);
        };
        if (recorded is not null)
            await runner.ReplayAsync(report, recorded, token);
        else
            await runner.RunAsync(report, options.Apply, options.Proposals is null ? "copilot" : "reviewed-file",
                options.Apply ? Advisor() : null, token);
        AuditFiles.WriteNew(Path.Combine(output, "loop-report.json"), report);
        var html = Path.Combine(output, "loop-report.html");
        LoopReportRenderer.Write(html, report);
        var summary = new
        {
            report.RunId, report.Status, report.PlanSource,
            report.ReproductionStatus, report.VerificationStatus, report.CaptureStatus,
            rounds = report.Rounds.Count,
            fixedTargets = report.Repairs.Count(repair => repair.Status == "fixed"),
            workingCopy = workspace,
            report = html,
            reportJson = Path.Combine(output, "loop-report.json"),
            report.Error
        };
        if (options.Json)
            Console.WriteLine(JsonSerializer.Serialize(summary, Json.Options));
        else
        {
            Console.WriteLine($"Loop: {report.Status}; reproduction: {report.ReproductionStatus}; verification: {report.VerificationStatus}; capture: {report.CaptureStatus}.");
            if (report.Error is not null)
                Console.Error.WriteLine(report.Error);
            Console.WriteLine($"Owned source copy: {workspace}");
            Console.WriteLine($"Report: {html}");
        }
        return report.Status switch { "completed" => 0, "failed" => 1, _ => 2 };
    }
}
