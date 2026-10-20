using System.Security.Cryptography;
using System.Text;

namespace AccessLoop;

public static class WorkbenchPolicy
{
    private static readonly HashSet<string> CheckIds = new(StringComparer.Ordinal)
    {
        "accessible-name", "target-enabled", "target-visible", "keyboard-reachable", "keyboard-activation"
    };

    public static IReadOnlyList<WorkbenchFinding> BuildCatalog(IReadOnlyList<ScenarioResult> results)
    {
        var catalog = CollectFindings(results);
        return catalog.Select(finding =>
        {
            try
            {
                var owner = RepairOwners(finding, results, catalog)[0];
                return finding with { Repairable = true, OwnerScenarioName = owner.Scenario.Name };
            }
            catch (InvalidDataException ex)
            {
                return finding with { Repairable = false, RepairReason = ex.Message };
            }
        }).ToArray();
    }

    public static string SuiteStatus(IReadOnlyList<ScenarioResult> results)
    {
        if (SuiteError(results) is not null)
            return "inconclusive";
        return results.Any(result => result.Observation.Scan!.Findings.Count > 0
            || result.Observation.Checks.Any(check => !check.Passed)) ? "failed" : "passed";
    }

    public static ScenarioResult FindRepairOwner(WorkbenchFinding selected, IReadOnlyList<ScenarioResult> before) =>
        RepairOwners(selected, before, CollectFindings(before))[0];

    public static RegressionResult Verify(
        WorkbenchFinding selected, IReadOnlyList<ScenarioResult> before, IReadOnlyList<ScenarioResult> after)
    {
        IReadOnlyList<WorkbenchFinding> remaining;
        try
        {
            remaining = BuildCatalog(after);
        }
        catch (InvalidDataException ex)
        {
            return new(false, "inconclusive", [$"Verification: {ex.Message}"], []);
        }
        var errors = new List<string>();
        RegressionResult Inconclusive() => new(false, "inconclusive",
            errors.Distinct(StringComparer.Ordinal).ToArray(), remaining);

        if (SuiteError(before) is { } baselineError)
            errors.Add($"Baseline: {baselineError}");
        if (SuiteError(after) is { } afterError)
            errors.Add($"Verification: {afterError}");
        if (errors.Count > 0)
            return Inconclusive();

        var afterByName = after.ToDictionary(result => result.Scenario.Name, StringComparer.Ordinal);
        if (before.Count != after.Count || before.Any(result => !afterByName.ContainsKey(result.Scenario.Name)))
        {
            errors.Add("Verification must rerun every baseline scenario exactly once, with no missing or extra scenarios.");
            return Inconclusive();
        }
        foreach (var original in before)
        {
            var replay = afterByName[original.Scenario.Name];
            if (original.Scenario != replay.Scenario)
                errors.Add($"Scenario '{original.Scenario.Name}' changed. Restore the original scenario inputs and rerun.");
            var originalScan = original.Observation.Scan!;
            var replayScan = replay.Observation.Scan!;
            if (originalScan.Engine != replayScan.Engine || originalScan.EngineVersion != replayScan.EngineVersion
                || originalScan.Checkpoint != replayScan.Checkpoint)
                errors.Add($"Scenario '{original.Scenario.Name}' used a different scan engine, version, or checkpoint.");
        }
        if (errors.Count > 0)
            return Inconclusive();

        var baseline = CollectFindings(before);
        IReadOnlyList<ScenarioResult> owners;
        try
        {
            owners = RepairOwners(selected, before, baseline);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(ex.Message);
            return Inconclusive();
        }
        if (IdentityError(remaining) is { } identityError)
        {
            errors.Add($"Verification: {identityError}");
            return Inconclusive();
        }

        // Use the fresh baseline, not occurrence metadata supplied by the selection.
        var current = baseline.Single(finding => finding.Id == selected.Id);
        foreach (var scenarioName in current.Occurrences.Select(occurrence => occurrence.ScenarioName)
            .Distinct(StringComparer.Ordinal))
        {
            if (afterByName[scenarioName].Observation.VerifiedTargets?.Contains(current.AutomationId!, StringComparer.Ordinal) != true)
                errors.Add($"Scenario '{scenarioName}' did not verify target '{current.AutomationId}' before and after keyboard replay. A missing target is not a fix.");
        }
        if (errors.Count > 0)
            return Inconclusive();

        foreach (var original in before)
        {
            var replayChecks = afterByName[original.Scenario.Name].Observation.Checks
                .ToDictionary(check => check.RuleId, StringComparer.Ordinal);
            foreach (var check in original.Observation.Checks.Where(check => check.Passed))
            {
                if (!replayChecks[check.RuleId].Passed)
                    errors.Add($"Scenario '{original.Scenario.Name}' now fails assertion '{check.RuleId}', which passed in the baseline.");
            }
        }
        foreach (var owner in owners)
        {
            if (!afterByName[owner.Scenario.Name].Observation.Checks.Single(check => check.RuleId == "accessible-name").Passed)
                errors.Add($"Scenario '{owner.Scenario.Name}' still fails the intended accessible-name assertion for '{current.AutomationId}'.");
        }

        var unresolved = remaining.SingleOrDefault(finding => finding.Id == current.Id);
        if (unresolved is not null)
            errors.Add($"Selected finding '{current.Id}' is still present in: {string.Join(", ", unresolved.Occurrences.Select(occurrence => occurrence.ScenarioName).Distinct(StringComparer.Ordinal))}.");
        var baselineById = baseline.ToDictionary(finding => finding.Id, StringComparer.Ordinal);
        foreach (var finding in remaining.Where(finding => finding.Id != current.Id))
        {
            baselineById.TryGetValue(finding.Id, out var original);
            foreach (var occurrences in finding.Occurrences.GroupBy(occurrence => occurrence.ScenarioName, StringComparer.Ordinal))
            {
                var previousCount = original?.Occurrences.Count(occurrence => occurrence.ScenarioName == occurrences.Key) ?? 0;
                if (occurrences.Count() > previousCount)
                    errors.Add($"Scenario '{occurrences.Key}' has {occurrences.Count()} occurrence(s) of '{finding.RuleId}' on '{finding.AutomationId}', up from {previousCount}.");
            }
        }
        return new(errors.Count == 0, errors.Count == 0 ? "fixed" : "regression",
            errors.Distinct(StringComparer.Ordinal).ToArray(), remaining);
    }

    private static IReadOnlyList<ScenarioResult> RepairOwners(
        WorkbenchFinding selected, IReadOnlyList<ScenarioResult> before, IReadOnlyList<WorkbenchFinding> catalog)
    {
        if (SuiteError(before) is { } suiteError)
            throw new InvalidDataException($"A complete baseline is required before repair. {suiteError}");
        if (!selected.StableIdentity)
            throw new InvalidDataException(IdentityError([selected]));
        var current = catalog.SingleOrDefault(finding => finding.Id == selected.Id
            && finding.Project == NormalizeProject(selected.Project)
            && finding.RuleId == selected.RuleId && finding.AutomationId == selected.AutomationId);
        if (current is null)
            throw new InvalidDataException($"Selected finding '{selected.Id}' is not present in this baseline. Rescan and select a current finding.");
        if (IdentityError(catalog) is { } identityError)
            throw new InvalidDataException(identityError);
        if (current.RuleId is not ("NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace"))
            throw new InvalidDataException($"Rule '{current.RuleId}' requires manual review; only missing accessible names support a literal XAML Name proposal.");

        var owners = before.Where(result => NormalizeProject(result.Scenario.Project) == current.Project
            && result.Scenario.TargetId == current.AutomationId).ToArray();
        if (owners.Length == 0)
            throw new InvalidDataException($"No scenario targets '{current.AutomationId}' in this project. Add a scenario with the intended name and keyboard checks before repair.");
        if (owners.Select(owner => owner.Scenario.ExpectedName).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException($"Scenarios targeting '{current.AutomationId}' disagree on expectedName. Resolve the conflicting expectations before repair.");
        foreach (var owner in owners)
        {
            if (current.Occurrences.Count(occurrence => occurrence.ScenarioName == owner.Scenario.Name) != 1)
                throw new InvalidDataException($"Scenario '{owner.Scenario.Name}' must contain exactly one occurrence of the selected finding.");
            if (owner.Observation.Scan!.Findings.Count(finding => finding.AutomationId == current.AutomationId) != 1)
                throw new InvalidDataException($"Scenario '{owner.Scenario.Name}' must have exactly one Axe.Windows finding on target '{current.AutomationId}' before a name proposal. Review the additional rules on that target.");
            if (owner.Observation.Checks.Single(check => check.RuleId == "accessible-name").Passed)
                throw new InvalidDataException($"Scenario '{owner.Scenario.Name}' does not have a failing accessible-name assertion for the selected target.");
            if (owner.Observation.Checks.Any(check => check.RuleId != "accessible-name" && !check.Passed))
                throw new InvalidDataException($"Scenario '{owner.Scenario.Name}' must pass target-enabled, target-visible, keyboard-reachable, and keyboard-activation before a name repair.");
        }
        return owners;
    }

    private static IReadOnlyList<WorkbenchFinding> CollectFindings(IReadOnlyList<ScenarioResult> results)
    {
        var groups = new Dictionary<(string Project, string AutomationId, string RuleId), List<FindingOccurrence>>();
        var ambiguous = new HashSet<(string Project, string ScenarioName, string AutomationId)>();
        var catalog = new List<WorkbenchFinding>();
        foreach (var result in results)
        {
            if (result.Observation.Scan?.Findings is not { } findings)
                continue;
            var project = NormalizeProject(result.Scenario.Project);
            foreach (var elements in findings.Where(finding => !string.IsNullOrWhiteSpace(finding.AutomationId))
                .GroupBy(finding => finding.AutomationId!, StringComparer.Ordinal))
            {
                // Runtime IDs are useful only for detecting different elements inside this one scan.
                if (elements.GroupBy(finding => finding.RuleId, StringComparer.Ordinal).Any(rule => rule.Count() > 1)
                    || elements.Select(finding => finding.ElementProperties.GetValueOrDefault("RuntimeId"))
                        .Where(runtimeId => !string.IsNullOrEmpty(runtimeId)).Distinct(StringComparer.Ordinal).Count() > 1)
                    ambiguous.Add((project, result.Scenario.Name, elements.Key));
            }
            foreach (var finding in findings)
            {
                var occurrence = new FindingOccurrence(result.Scenario.Name, finding);
                if (string.IsNullOrWhiteSpace(finding.AutomationId))
                {
                    catalog.Add(new($"AL-SESSION-{Guid.NewGuid():N}".ToUpperInvariant(), project, finding.RuleId,
                        finding.AutomationId, false, [occurrence]));
                    continue;
                }
                var key = (project, finding.AutomationId, finding.RuleId);
                if (!groups.TryGetValue(key, out var occurrences))
                    groups[key] = occurrences = [];
                occurrences.Add(occurrence);
            }
        }
        foreach (var (key, occurrences) in groups)
        {
            var stable = occurrences.All(occurrence => !ambiguous.Contains((key.Project, occurrence.ScenarioName, key.AutomationId)));
            catalog.Add(new(StableId(key.Project, key.AutomationId, key.RuleId), key.Project,
                key.RuleId, key.AutomationId, stable, occurrences.ToArray()));
        }
        return catalog.OrderBy(finding => finding.Project, StringComparer.Ordinal)
            .ThenBy(finding => finding.AutomationId, StringComparer.Ordinal)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal).ToArray();
    }

    private static string? IdentityError(IReadOnlyList<WorkbenchFinding> catalog)
    {
        var finding = catalog.FirstOrDefault(finding => !finding.StableIdentity);
        if (finding is null)
            return null;
        var problem = string.IsNullOrWhiteSpace(finding.AutomationId)
            ? $"Rule '{finding.RuleId}' in scenario '{finding.Occurrences.FirstOrDefault()?.ScenarioName}' has no AutomationId and only a scan-local identity."
            : $"AutomationId '{finding.AutomationId}' identifies multiple runtime elements in a scenario in '{finding.Project}'.";
        return $"{problem} Assign unique AutomationIds and rescan; unrelated repairs cannot be accepted without a safe no-regression comparison.";
    }

    private static string? SuiteError(IReadOnlyList<ScenarioResult> results)
    {
        if (results.Count == 0)
            return "No scenarios completed.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var scenario = result.Scenario;
            try
            {
                scenario.Validate();
                NormalizeProject(scenario.Project);
            }
            catch (InvalidDataException ex)
            {
                return $"Scenario '{scenario.Name}' is invalid: {ex.Message}";
            }
            if (!names.Add(scenario.Name))
                return $"Scenario name '{scenario.Name}' is duplicated; each scenario must run exactly once.";
            var observation = result.Observation;
            if (observation.Error is not null || observation.Status is not ("passed" or "failed")
                || string.IsNullOrWhiteSpace(observation.Phase))
                return $"Scenario '{scenario.Name}' is incomplete: {observation.Error ?? observation.Status}.";
            if (observation.Scan is not { } scan)
                return $"Scenario '{scenario.Name}' has no successful Axe.Windows scan.";
            if (scan.Engine != "Axe.Windows" || string.IsNullOrWhiteSpace(scan.EngineVersion) || scan.Checkpoint != "ready")
                return $"Scenario '{scenario.Name}' has no valid Axe.Windows engine, version, or ready checkpoint.";
            if (observation.ProcessId is not > 0 || scan.ProcessId != observation.ProcessId || scan.WindowHandle == 0)
                return $"Scenario '{scenario.Name}' has a scan without its owned process and window.";
            if (scan.ResultGroupErrorCounts is not { Count: > 0 } || scan.ResultGroupErrorCounts.Any(count => count < 0))
                return $"Scenario '{scenario.Name}' has no complete scan result groups.";
            var rawCount = scan.ResultGroupErrorCounts.Sum(count => (long)count);
            if (scan.Findings is null || rawCount < scan.Findings.Count || (rawCount > 0 && scan.Findings.Count == 0)
                || scan.Findings.Any(finding => string.IsNullOrWhiteSpace(finding.RuleId)))
                return $"Scenario '{scenario.Name}' has inconsistent scan error counts or missing finding details.";
            if (observation.Checks is null || observation.Checks.Count != CheckIds.Count
                || !CheckIds.SetEquals(observation.Checks.Select(check => check.RuleId)))
                return $"Scenario '{scenario.Name}' must record exactly accessible-name, target-enabled, target-visible, keyboard-reachable, and keyboard-activation.";
        }
        return null;
    }

    private static string NormalizeProject(string project)
    {
        try
        {
            return Path.GetFullPath(project).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidDataException($"Invalid project path '{project}'.", ex);
        }
    }

    private static string StableId(params string[] identity)
    {
        var input = string.Concat(identity.Select(part => $"{part.Length}:{part}"));
        return "AL-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }
}
