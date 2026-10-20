namespace AccessLoop;

public static class JourneyCommand
{
    public static JourneyOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != "journey" || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Use journey <journey.json> --accept-navigation. See --help.");
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index++)
        {
            var key = args[index];
            if (key is "--accept-navigation" or "--apply" or "--copilot-plan" or "--no-screenshot" or "--json")
            {
                if (!flags.Add(key))
                    throw new ArgumentException($"Duplicate option: {key}.");
                continue;
            }
            if (key is not ("--winapp" or "--copilot" or "--output" or "--plan" or "--proposals")
                || ++index >= args.Count || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, args[index]))
                throw new ArgumentException($"Unknown, duplicate or incomplete journey option: {key}.");
        }
        if (!flags.Contains("--accept-navigation"))
            throw new ArgumentException("--accept-navigation is required: this command builds and launches your source project, navigates approved pages and performs their declared keyboard exercises.");
        if (flags.Contains("--copilot-plan") && values.ContainsKey("--plan"))
            throw new ArgumentException("Choose either --copilot-plan or a reviewed --plan, not both.");
        if (values.ContainsKey("--proposals") && !flags.Contains("--apply"))
            throw new ArgumentException("--proposals is only used with --apply.");
        return new(args[1], values.GetValueOrDefault("--winapp", "winapp"), values.GetValueOrDefault("--copilot", "copilot"),
            values.GetValueOrDefault("--output"), true, flags.Contains("--apply"), flags.Contains("--copilot-plan"),
            values.GetValueOrDefault("--plan"), values.GetValueOrDefault("--proposals"),
            flags.Contains("--no-screenshot"), flags.Contains("--json"));
    }

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var options = Parse(args);
        var input = JourneyInput.Load(options.Input);
        var version = await new NativeAuditPlatform(options.Winapp).VersionAsync(token);
        var output = Path.GetFullPath(options.Output ?? Path.Combine(Path.GetDirectoryName(input.Path)!,
            ".accessloop", "journey-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        var projectPrefix = Path.TrimEndingDirectorySeparator(input.ProjectRoot) + Path.DirectorySeparatorChar;
        if (output.Equals(input.ProjectRoot, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Put journey evidence outside the source project's directory.");
        if (options.Plan is not null)
            _ = JourneyAdvisor.ReadPlan(options.Plan);
        if (options.Proposals is not null)
            AuditFiles.EnsureDirectory(options.Proposals);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        AuditFiles.CreateNewDirectory(output);
        var platform = new NativeJourneyPlatform(options with { Output = output }, version);
        var report = await new JourneyRunner(platform).RunAsync(input, options, version, output, token);
        AuditFiles.WriteNew(Path.Combine(output, "journey-report.json"), report);
        var html = Path.Combine(output, "journey-report.html");
        JourneyReportRenderer.Write(html, report);
        if (options.Json)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                report.RunId, report.Status, report.VerificationStatus, report.CaptureStatus,
                expectedPages = report.ExpectedPages.Count,
                fixedFindings = report.Repairs.Count(repair => repair.Status == "fixed"),
                remainingFindings = report.RemainingFindings.Count,
                report = html,
                reportJson = Path.Combine(output, "journey-report.json")
            }, Json.Options));
        else
        {
            Console.WriteLine($"Journey: {report.Status}; fixed: {report.Repairs.Count(repair => repair.Status == "fixed")}; remaining: {report.RemainingFindings.Count}.");
            Console.WriteLine($"Required verification: {report.VerificationStatus}; capture completeness: {report.CaptureStatus}.");
            Console.WriteLine($"Declared pages: {report.ExpectedPages.Count}. Complete evidence and unchanged fixed checks are required before passing.");
            if (report.Error is not null)
                Console.Error.WriteLine(report.Error);
            foreach (var error in report.Errors)
                Console.Error.WriteLine(error);
            Console.WriteLine($"Evidence: {html}");
        }
        return Program.ExitCode(report.Status);
    }
}

public sealed class NativeJourneyPlatform(JourneyOptions options, string version) : IJourneyPlatform
{
    public async Task<IJourneySession> LaunchAsync(JourneyDefinition definition, string evidencePath, CancellationToken token)
    {
        Directory.CreateDirectory(evidencePath);
        var session = await new WinappLauncher(options.Winapp).LaunchProjectAsync(
            definition.Project, definition.TimeoutSeconds, evidencePath, token);
        try
        {
            return new JourneyPageSession(session, new NativeAuditPlatform(options.Winapp), version,
                options.Output ?? throw new InvalidDataException("A journey evidence root is required."), !options.NoScreenshot);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public Task<JourneyPlan> PlanAsync(JourneyDefinition definition, IReadOnlyList<JourneyCandidate> candidates,
        string evidencePath, CancellationToken token) =>
        JourneyAdvisor.PlanAsync(options.Copilot, definition, candidates, evidencePath, token);

    public Task<RepairProposal> ProposeAsync(SourceCandidate candidate, Scenario scenario, Observation observation,
        string evidencePath, CancellationToken token)
    {
        if (options.Proposals is null)
            return CopilotAdvisor.ProposeAsync(options.Copilot, [candidate], scenario, observation, evidencePath, token);
        token.ThrowIfCancellationRequested();
        var path = AuditFiles.ResolveArtifact(options.Proposals, scenario.TargetId + ".json");
        return Task.FromResult(CopilotAdvisor.ReadProposal(path));
    }
}
