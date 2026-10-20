namespace AccessLoop;

public sealed class JourneyRunner(IJourneyPlatform platform)
{
    public async Task<JourneyReport> RunAsync(
        JourneyInput input, JourneyOptions options, string version, string output, CancellationToken token)
    {
        var definition = input.Definition;
        var report = new JourneyReport
        {
            RunId = Guid.NewGuid().ToString("N"), Name = definition.Name, Project = definition.Project,
            StartedAtUtc = DateTimeOffset.UtcNow, InputSha256 = input.Sha256, WinappVersion = version,
            ExpectedPages = definition.Pages
        };
        AppliedRepair? pending = null;
        try
        {
            if (!options.AcceptNavigation)
                throw new InvalidDataException("--accept-navigation is required before building, launching or navigating the demo.");
            input.Save(output);
            var sources = JourneyInput.CaptureSources(definition.Project);
            report.OriginalSources = sources;
            AuditFiles.WriteNew(Path.Combine(output, "source-baseline.json"), sources);
            IReadOnlyList<JourneyRoute> routes;
            var discover = Path.Combine(output, "discovery");
            Directory.CreateDirectory(discover);
            await using (var session = await platform.LaunchAsync(definition, discover, token))
            {
                report.Candidates = await session.DiscoverAsync(definition, token);
                AuditFiles.WriteNew(Path.Combine(discover, "candidates.json"), report.Candidates);
                JourneyPlan plan;
                if (options.Plan is { } supplied)
                {
                    var bytes = AuditFiles.ReadBytes(supplied, 16 * 1024);
                    AuditFiles.WriteNewBytes(Path.Combine(output, "navigation-plan-input.json"), bytes);
                    plan = JourneyAdvisor.ReadPlan(Path.Combine(output, "navigation-plan-input.json"));
                    report.PlanSource = "reviewed-plan";
                }
                else if (options.UseCopilotPlan)
                {
                    plan = await platform.PlanAsync(definition, report.Candidates, Path.Combine(discover, "planner"), token);
                    report.PlanSource = "copilot";
                }
                else
                {
                    plan = new(definition.Pages.Select(page => page.Id).ToArray(),
                        "Declared order, verified against actual runtime navigation candidates; no AI planner was called.");
                    report.PlanSource = "declared-order";
                }
                report.Plan = JourneyPolicy.ValidatePlan(definition, report.Candidates, plan);
                AuditFiles.WriteNew(Path.Combine(output, "navigation-plan.json"), report.Plan);
                routes = JourneyPolicy.FreezeRoutes(definition, report.Plan);
                input.EnsureSources(sources);
                var baseline = await VisitPagesAsync(session, definition, routes, "baseline", output, token);
                report.Rounds.Add(baseline);
                report.RemainingFindings = baseline.Findings;
            }
            input.EnsureSources(sources);
            report.FrozenRoutes = routes;
            AuditFiles.WriteNew(Path.Combine(output, "routes.json"), routes);
            var original = report.Rounds[0];
            report.CaptureStatus = JourneyPolicy.CaptureStatus(original, definition.Pages.Count);
            var coverage = JourneyPolicy.ValidateCoverage(definition, routes, original);
            if (coverage.Count > 0)
            {
                report.Status = "inconclusive";
                report.Errors = coverage;
                return report;
            }
            if (original.Findings.Any(finding => !finding.StableIdentity))
            {
                report.Status = "needs-review";
                report.Errors = ["The baseline has findings without stable cross-restart identities. Preserve them for manual mapping; no automatic repair was applied."];
                return report;
            }
            if (!options.Apply)
            {
                report.Status = original.Findings.Count == 0 && JourneyPolicy.FunctionChecksPass(original) ? "passed"
                    : original.Findings.Any(finding => finding.Repairable) ? "awaiting-approval" : "needs-review";
                report.VerificationStatus = report.Status == "passed" ? "passed" : "failed";
                if (report.Status == "passed" && report.CaptureStatus != "completed")
                    report.Status = "partial";
                return report;
            }

            var previous = original;
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            while (report.Repairs.Count < definition.MaxRepairs)
            {
                token.ThrowIfCancellationRequested();
                input.EnsureSources(sources);
                var selected = previous.Findings.FirstOrDefault(finding => finding.Repairable && !attempted.Contains(finding.Id));
                if (selected is null)
                    break;
                attempted.Add(selected.Id);
                var page = definition.Pages.Single(item => item.Id == selected.OwnerScenarioName);
                var scenario = JourneyInput.ScenarioFor(definition, page);
                var roundName = $"repair-{report.Repairs.Count + 1:D2}";
                var repairDirectory = Path.Combine(output, roundName);
                Directory.CreateDirectory(repairDirectory);
                SourceCandidate? candidate = null;
                JourneyRepair transaction;
                try
                {
                    var candidates = SourceRepair.Locate(definition.Project, selected.AutomationId!);
                    AuditFiles.WriteNew(Path.Combine(repairDirectory, "source-candidates.json"), candidates);
                    if (candidates.Count != 1)
                        throw new InvalidDataException($"Expected one source candidate for '{selected.AutomationId}', found {candidates.Count}.");
                    candidate = candidates[0];
                    var observation = previous.Pages.Single(item => item.PageId == page.Id).Exercise!;
                    var proposal = await platform.ProposeAsync(candidate, scenario, observation, repairDirectory, token);
                    Program.ValidateProposal(candidate, proposal, scenario);
                    input.EnsureSources(sources);
                    token.ThrowIfCancellationRequested();
                    AuditFiles.WriteNew(Path.Combine(repairDirectory, "proposal.json"), proposal);
                    pending = SourceRepair.Apply(candidate, proposal, scenario.ExpectedName);
                    File.WriteAllBytes(Path.Combine(repairDirectory, "source.original.txt"), pending.OriginalBytes);
                    File.WriteAllText(Path.Combine(repairDirectory, "repair.diff"), pending.Diff);
                    var updatedSources = input.WithPatch(sources, pending);
                    input.EnsureSources(updatedSources);
                    JourneyRound verification;
                    await using (var session = await platform.LaunchAsync(definition, repairDirectory, token))
                        verification = await VisitPagesAsync(session, definition, routes, roundName, output, token);
                    report.Rounds.Add(verification);
                    input.EnsureSources(updatedSources);
                    var errors = JourneyPolicy.VerifyRound(definition, routes, original, previous, verification, selected);
                    var accepted = errors.Count == 0;
                    transaction = new()
                    {
                        FindingId = selected.Id, PageId = page.Id, AutomationId = selected.AutomationId,
                        EvidencePath = roundName, SourcePath = candidate.RelativePath, Diff = pending.Diff,
                        Status = accepted ? "fixed" : "regression", Errors = errors,
                        RemainingFindings = accepted ? verification.Findings.Count : previous.Findings.Count,
                        RolledBack = !accepted
                    };
                    if (!accepted)
                    {
                        SourceRepair.Rollback(pending);
                        pending = null;
                        input.EnsureSources(sources);
                        report.Repairs.Add(transaction);
                        report.Status = "regression";
                        report.VerificationStatus = "failed";
                        report.CaptureStatus = JourneyPolicy.CaptureStatus(verification, definition.Pages.Count);
                        report.Errors = errors;
                        report.RemainingFindings = previous.Findings;
                        return report;
                    }
                    sources = updatedSources;
                    previous = verification;
                    report.RemainingFindings = verification.Findings;
                    report.CaptureStatus = JourneyPolicy.CaptureStatus(verification, definition.Pages.Count);
                    pending = null;
                    report.Repairs.Add(transaction);
                    AuditFiles.WriteNew(Path.Combine(repairDirectory, "repair-result.json"), transaction);
                }
                catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
                {
                    var errors = new List<string> { exception is OperationCanceledException ? "Repair cancelled." : exception.Message };
                    var rolledBack = false;
                    if (pending is not null)
                    {
                        try
                        {
                            SourceRepair.Rollback(pending);
                            pending = null;
                            rolledBack = true;
                        }
                        catch (Exception rollbackError) when (AuditRunner.IsExecutionFailure(rollbackError))
                        {
                            errors.Add("Rollback refused/failed: " + rollbackError.Message);
                        }
                    }
                    transaction = new()
                    {
                        FindingId = selected.Id, PageId = page.Id, AutomationId = selected.AutomationId,
                        EvidencePath = roundName, SourcePath = candidate?.RelativePath,
                        Status = "blocked", Errors = errors, RolledBack = rolledBack,
                        RemainingFindings = previous.Findings.Count
                    };
                    report.Repairs.Add(transaction);
                    AuditFiles.WriteNew(Path.Combine(repairDirectory, "repair-result.json"), transaction);
                    report.Status = "inconclusive";
                    report.Errors = errors;
                    return report;
                }
            }
            input.EnsureSources(sources);
            report.Status = previous.Findings.Count == 0 && JourneyPolicy.FunctionChecksPass(previous) ? "passed" : "needs-review";
            report.VerificationStatus = report.Status == "passed" ? "passed" : "failed";
            if (report.Status == "passed" && report.CaptureStatus != "completed")
                report.Status = "partial";
            if (report.VerificationStatus != "passed")
                report.Errors = [report.Repairs.Count >= definition.MaxRepairs ? "Repair budget reached; remaining findings are retained."
                    : "Remaining findings or functional checks are outside the supported automatic repair cases."];
        }
        catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
        {
            report.Status = "inconclusive";
            report.Error = exception is OperationCanceledException ? "Journey cancelled." : exception.Message;
        }
        finally
        {
            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            if (pending is not null)
            {
                try { SourceRepair.Rollback(pending); }
                catch (Exception error) when (AuditRunner.IsExecutionFailure(error))
                {
                    report.Error = $"{report.Error} Rollback refused/failed: {error.Message}".Trim();
                    report.Status = "inconclusive";
                }
            }
        }
        return report;
    }

    private static async Task<JourneyRound> VisitPagesAsync(
        IJourneySession session, JourneyDefinition definition, IReadOnlyList<JourneyRoute> routes,
        string phase, string output, CancellationToken token)
    {
        var pages = new List<JourneyPageResult>();
        var errors = new List<string>();
        var transitions = 0;
        foreach (var route in routes)
        {
            var page = definition.Pages.Single(item => item.Id == route.PageId);
            var relative = Path.Combine(phase, page.Id);
            try
            {
                token.ThrowIfCancellationRequested();
                transitions += page.Id == definition.InitialPageId ? 1 : 2;
                if (transitions > definition.MaxTransitions)
                    throw new InvalidDataException("Navigation transition budget exhausted; remaining pages are not visited.");
                var result = await session.VisitAsync(definition, page, route, phase, relative, token);
                pages.Add(result with { EvidencePath = relative });
            }
            catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
            {
                var error = exception is OperationCanceledException ? "Page visit cancelled." : exception.Message;
                errors.Add($"{page.Id}: {error}");
                pages.Add(new() { PageId = page.Id, Status = "inconclusive", EvidencePath = relative, Route = route, Error = error });
                break;
            }
        }
        var findings = JourneyPolicy.Findings(definition, pages);
        var round = new JourneyRound
        {
            Phase = phase, Status = "inconclusive", Pages = pages, Findings = findings,
            Error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors)
        };
        var coverage = JourneyPolicy.ValidateCoverage(definition, routes, round);
        round = round with
        {
            Status = coverage.Count != 0 ? "inconclusive"
                : findings.Count == 0 && JourneyPolicy.FunctionChecksPass(round) ? "passed" : "failed",
            Error = coverage.Count != 0 ? string.Join(Environment.NewLine, coverage) : round.Error
        };
        AuditFiles.WriteNew(Path.Combine(output, phase + "-round.json"), round);
        return round;
    }
}
