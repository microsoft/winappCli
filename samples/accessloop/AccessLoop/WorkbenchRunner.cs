namespace AccessLoop;

public sealed class WorkbenchRunner(ScenarioRunner runner)
{
    public async Task<RunReport> RunAsync(
        SuiteInput input, WorkbenchOptions options, string winappVersion,
        string output, CancellationToken cancellationToken)
    {
        var report = new RunReport
        {
            SuiteName = input.Name,
            InputPath = input.InputPath,
            Inputs = input.SaveSnapshots(output),
            WinappVersion = winappVersion,
            Status = "inconclusive"
        };
        AppliedRepair? applied = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappings = options.Command == "repair" ? CaptureMappings(input) : [];
            report.Before = await RunCasesAsync(input, "before", output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            input.EnsureUnchanged();
            report.SuiteStatus = report.Before.Count == input.Cases.Count
                ? WorkbenchPolicy.SuiteStatus(report.Before) : "inconclusive";
            report.Status = report.SuiteStatus;
            if (report.Before.Count != input.Cases.Count)
                report.Error = "The run stopped before every scenario completed; no repair can be accepted.";
            report.Findings = WorkbenchPolicy.BuildCatalog(report.Before);
            report.RemainingFindings = report.Findings;
            PrintFindings(report);
            if (options.Command == "check" || report.SuiteStatus == "inconclusive")
                return report;

            if (report.Findings.Count == 0)
            {
                if (options.Finding is not null)
                {
                    report.Status = "needs-review";
                    report.Error = "The selected finding is not present in the fresh baseline; no source was edited.";
                }
                else if (report.SuiteStatus != "passed")
                {
                    report.Error = "No Axe.Windows finding is available for repair. Review the failing scenario assertions.";
                }
                return report;
            }
            if (options.Finding is null && report.Findings.Count > 1)
            {
                report.Status = "awaiting-selection";
                report.Error = "Choose a finding ID from this report and rerun repair with --finding <ID>.";
                return report;
            }
            var selected = options.Finding is null ? report.Findings[0]
                : report.Findings.SingleOrDefault(f => f.Id.Equals(options.Finding, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                report.Status = "needs-review";
                report.Error = $"Finding '{options.Finding}' is not present in the fresh baseline. Review the current inventory.";
                return report;
            }
            report.SelectedFindingId = selected.Id;
            ScenarioResult owner;
            try
            {
                owner = WorkbenchPolicy.FindRepairOwner(selected, report.Before);
            }
            catch (InvalidDataException ex)
            {
                report.Status = "needs-review";
                report.Error = ex.Message;
                return report;
            }
            var mapping = mappings.Single(m =>
                m.Project.Equals(owner.Scenario.Project, StringComparison.OrdinalIgnoreCase)
                && m.AutomationId == selected.AutomationId);
            Json.Write(Path.Combine(output, "source-candidates.json"), mapping.Candidates);
            if (mapping.Error is not null || mapping.Candidates.Count != 1)
            {
                report.Status = "needs-review";
                report.Error = mapping.Error ?? $"Found {mapping.Candidates.Count} source candidates; a unique static Button is required.";
                return report;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Propose: {selected.Id} in {owner.Scenario.Name}.");
            report.RepairSource = options.Proposal is null ? "copilot" : "reviewed-proposal";
            var proposal = options.Proposal is null
                ? await CopilotAdvisor.ProposeAsync(options.Copilot, mapping.Candidates,
                    owner.Scenario, owner.Observation, output, cancellationToken)
                : CopilotAdvisor.ReadProposal(Path.GetFullPath(options.Proposal));
            var candidate = mapping.Candidates[0];
            Program.ValidateProposal(candidate, proposal, owner.Scenario);
            Json.Write(Path.Combine(output, "proposal.json"), proposal);
            if (!options.Apply)
            {
                report.Status = "awaiting-approval";
                report.Error = $"Review proposal.json, then rerun repair --finding {selected.Id} --proposal <file> --apply.";
                return report;
            }

            input.EnsureUnchanged();
            cancellationToken.ThrowIfCancellationRequested();
            var original = File.ReadAllBytes(candidate.FullPath);
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original))
                .Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source changed before the repair backup; capture a fresh baseline.");
            File.WriteAllBytes(Path.Combine(output, "source.original.txt"), original);
            applied = SourceRepair.Apply(candidate, proposal, owner.Scenario.ExpectedName);
            report.Status = "verifying";
            report.PatchApplied = true;
            File.WriteAllText(Path.Combine(output, "repair.diff"), applied.Diff);
            Json.Write(Path.Combine(output, "report.json"), report);
            Console.WriteLine("Verify: rebuild and rerun every scenario, including unrelated existing findings.");
            report.After = await RunCasesAsync(input, "after", output, cancellationToken, selected);
            cancellationToken.ThrowIfCancellationRequested();
            input.EnsureUnchanged();
            var verdict = WorkbenchPolicy.Verify(selected, report.Before, report.After);
            report.Status = verdict.Status;
            report.VerificationErrors = verdict.Errors;
            report.RemainingFindings = verdict.RemainingFindings;
            report.SuiteStatus = verdict.Status != "inconclusive" && report.After.Count == input.Cases.Count
                ? WorkbenchPolicy.SuiteStatus(report.After) : "inconclusive";
            if (!verdict.Accepted)
            {
                SourceRepair.Rollback(applied);
                report.PatchRolledBack = true;
                report.PatchApplied = false;
            }
        }
        catch (Exception ex) when (ScenarioRunner.IsExecutionFailure(ex))
        {
            report.Status = "inconclusive";
            report.SuiteStatus = "inconclusive";
            report.Error = ex is OperationCanceledException ? "Execution cancelled." : ex.Message;
            if (applied is not null && !report.PatchRolledBack)
            {
                try
                {
                    SourceRepair.Rollback(applied);
                    report.PatchRolledBack = true;
                    report.PatchApplied = false;
                }
                catch (Exception rollbackError) when (ScenarioRunner.IsExecutionFailure(rollbackError))
                {
                    report.Error += $" Rollback refused/failed: {rollbackError.Message}. Inspect repair.diff before editing.";
                }
            }
        }
        return report;
    }

    private async Task<IReadOnlyList<ScenarioResult>> RunCasesAsync(
        SuiteInput input, string phase, string output, CancellationToken token, WorkbenchFinding? selected = null)
    {
        var results = new List<ScenarioResult>();
        foreach (var (item, index) in input.Cases.Select((item, index) => (item, index)))
        {
            if (token.IsCancellationRequested)
                break;
            var relative = Path.Combine(phase, $"{index + 1:D2}");
            Console.WriteLine($"{phase} [{index + 1}/{input.Cases.Count}]: {item.Scenario.Name}");
            var required = selected is { AutomationId: not null }
                && selected.Occurrences.Any(o => o.ScenarioName == item.Scenario.Name)
                ? new[] { selected.AutomationId } : [];
            var observation = await runner.RunAsync(item.Scenario, phase, Path.Combine(output, relative), token, required);
            results.Add(new(item.Scenario, relative, observation));
        }
        return results;
    }

    private static IReadOnlyList<SourceMapping> CaptureMappings(SuiteInput input)
    {
        var mappings = new List<SourceMapping>();
        foreach (var item in input.Cases)
        {
            var scenario = item.Scenario;
            if (mappings.Any(m => m.Project.Equals(scenario.Project, StringComparison.OrdinalIgnoreCase) && m.AutomationId == scenario.TargetId))
                continue;
            try
            {
                mappings.Add(new(scenario.Project, scenario.TargetId,
                    SourceRepair.Locate(scenario.Project, scenario.TargetId), null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                mappings.Add(new(scenario.Project, scenario.TargetId, [], $"Source mapping failed: {ex.Message}"));
            }
        }
        return mappings;
    }

    private static void PrintFindings(RunReport report)
    {
        Console.WriteLine($"Suite: {report.SuiteStatus}; {report.Findings.Count} distinct finding(s).");
        foreach (var finding in report.Findings)
            Console.WriteLine($"  {finding.Id}  {finding.RuleId}  {finding.AutomationId ?? "(no AutomationId)"}  [{(finding.Repairable ? "proposal available" : "manual review")}]");
    }

    private sealed record SourceMapping(string Project, string AutomationId, IReadOnlyList<SourceCandidate> Candidates, string? Error);
}
