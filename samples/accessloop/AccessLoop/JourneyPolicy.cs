using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static class JourneyPolicy
{
    public static JourneyPlan ValidatePlan(
        JourneyDefinition definition, IReadOnlyList<JourneyCandidate> candidates, JourneyPlan plan)
    {
        if (candidates.Count != definition.Pages.Count || candidates.Select(candidate => candidate.PageId)
            .Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new InvalidDataException("All declared navigation entries must be uniquely observed before planning.");
        foreach (var page in definition.Pages)
        {
            var matches = candidates.Where(candidate => candidate.PageId == page.Id && candidate.NavigationId == page.NavigationId).ToArray();
            if (matches.Length != 1 || matches[0].ControlType is not ("Button" or "ListItem" or "TabItem"))
                throw new InvalidDataException($"No approved runtime navigation candidate for '{page.Id}'.");
        }
        if (plan.PageOrder is null || plan.PageOrder.Count != definition.Pages.Count
            || plan.PageOrder.Distinct(StringComparer.Ordinal).Count() != definition.Pages.Count
            || plan.PageOrder.Any(id => !definition.Pages.Any(page => page.Id == id)))
            throw new InvalidDataException("The plan must visit every declared page exactly once and cannot add or remove scope.");
        AuditFiles.Text(plan.Explanation, "plan explanation", 2000);
        return plan;
    }

    public static IReadOnlyList<JourneyRoute> FreezeRoutes(JourneyDefinition definition, JourneyPlan plan) =>
        plan.PageOrder.Select(id =>
        {
            var page = definition.Pages.Single(page => page.Id == id);
            return new JourneyRoute(id, definition.InitialPageId, page.NavigationId, page.ReadyId);
        }).ToArray();

    public static IReadOnlyList<WorkbenchFinding> Findings(JourneyDefinition definition, IReadOnlyList<JourneyPageResult> pages)
    {
        var groups = new Dictionary<(string Rule, string Id), List<FindingOccurrence>>();
        var result = new List<WorkbenchFinding>();
        foreach (var page in pages)
        {
            if (page.Audit?.Axe is not { } scan)
                continue;
            foreach (var finding in scan.Findings)
            {
                var id = finding.AutomationId;
                if (string.IsNullOrWhiteSpace(id))
                {
                    result.Add(new($"J-LOCAL-{page.PageId}-{result.Count}", definition.Project, finding.RuleId, null,
                        false, [new(page.PageId, finding)]) { RepairReason = "No cross-restart AutomationId; manual source mapping is required." });
                    continue;
                }
                var key = (finding.RuleId, id);
                if (!groups.TryGetValue(key, out var occurrences))
                    groups[key] = occurrences = [];
                occurrences.Add(new(page.PageId, finding));
            }
        }
        foreach (var (key, occurrences) in groups)
        {
            var duplicate = occurrences.GroupBy(item => item.ScenarioName, StringComparer.Ordinal).Any(group => group.Count() != 1);
            var owner = definition.Pages.SingleOrDefault(page => page.Exercise?.TargetId == key.Id);
            var ownerResult = owner is null ? null : pages.SingleOrDefault(page => page.PageId == owner.Id);
            var source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(definition.Project).ToUpperInvariant() + "\n" + key.Rule + "\n" + key.Id)))[..16];
            var repairable = !duplicate && ownerResult?.Exercise is { } exercise
                && ScenarioRunner.CanRepair(exercise, key.Id)
                && key.Rule is "NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace";
            result.Add(new("J-" + source, definition.Project, key.Rule, key.Id, !duplicate, occurrences.ToArray())
            {
                Repairable = repairable,
                OwnerScenarioName = owner?.Id,
                RepairReason = repairable ? null : duplicate ? "Duplicate element identity in a page."
                    : "No supported Name-only source case with passing fixed behavioral guards."
            });
        }
        return result.OrderBy(finding => finding.AutomationId, StringComparer.Ordinal)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ValidateCoverage(
        JourneyDefinition definition, IReadOnlyList<JourneyRoute> routes, JourneyRound round)
    {
        var errors = new List<string>();
        if (round.Pages.Count != definition.Pages.Count || round.Pages.Select(page => page.PageId)
            .Distinct(StringComparer.Ordinal).Count() != definition.Pages.Count)
        {
            errors.Add("Every declared page must be visited exactly once; missing or duplicate pages cannot count as completed.");
            if (round.Pages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count() != round.Pages.Count)
                return errors;
        }
        foreach (var expected in definition.Pages)
        {
            var page = round.Pages.SingleOrDefault(page => page.PageId == expected.Id);
            if (page is null)
            {
                errors.Add($"Page '{expected.Id}' was not visited.");
                continue;
            }
            var route = routes.SingleOrDefault(item => item.PageId == expected.Id);
            if (route is null || route != page.Route)
                errors.Add($"Page '{expected.Id}' did not replay its frozen route.");
            if (page.Error is not null || page.Status is not ("passed" or "failed")
                || page.Audit is null || !HasRequiredAuditEvidence(page.Audit) || page.Audit.Axe is null)
                errors.Add($"Page '{expected.Id}' has incomplete evidence: {page.Error ?? page.Audit?.Error ?? page.Audit?.Status ?? page.Status}.");
            if (expected.RequiredControlIds.Any(id => !page.ObservedControlIds.Contains(id, StringComparer.Ordinal)))
                errors.Add($"Page '{expected.Id}' lost a required control.");
            if (page.Checks.Count == 0 || page.Checks.Any(check => !check.Passed))
                errors.Add($"Page '{expected.Id}' failed its ready/control-preservation checks.");
            if (expected.Exercise is not null && (page.Exercise is null || page.Exercise.Error is not null
                || page.Exercise.Checks.Count != 5))
                errors.Add($"Page '{expected.Id}' has incomplete functional checks.");
        }
        return errors;
    }

    public static IReadOnlyList<string> VerifyRound(
        JourneyDefinition definition, IReadOnlyList<JourneyRoute> routes,
        JourneyRound original, JourneyRound previous, JourneyRound after, WorkbenchFinding selected)
    {
        var errors = ValidateCoverage(definition, routes, after).ToList();
        foreach (var baseline in original.Pages)
        {
            var current = after.Pages.SingleOrDefault(page => page.PageId == baseline.PageId);
            if (current is null)
                continue;
            if (baseline.ObservedControlIds.Any(id => !current.ObservedControlIds.Contains(id, StringComparer.Ordinal)))
                errors.Add($"Page '{baseline.PageId}' lost a baseline AutomationId. Removing UI is not a repair.");
            if (baseline.Audit?.Axe is { } beforeScan && current.Audit?.Axe is { } afterScan
                && (beforeScan.Engine != afterScan.Engine || beforeScan.EngineVersion != afterScan.EngineVersion
                    || beforeScan.Checkpoint != afterScan.Checkpoint || baseline.Audit.CatalogVersion != current.Audit.CatalogVersion))
                errors.Add($"Page '{baseline.PageId}' changed its scan engine, rule catalog or checkpoint.");
        }
        foreach (var baseline in previous.Pages)
        {
            var current = after.Pages.SingleOrDefault(page => page.PageId == baseline.PageId);
            if (current is null)
                continue;
            foreach (var check in baseline.Exercise?.Checks.Where(check => check.Passed) ?? [])
                if (current.Exercise?.Checks.SingleOrDefault(item => item.RuleId == check.RuleId)?.Passed != true)
                    errors.Add($"Page '{baseline.PageId}' regressed fixed check '{check.RuleId}'.");
        }
        if (after.Findings.Any(finding => !finding.StableIdentity))
            errors.Add("A finding lacks stable cross-restart identity; no-regression comparison is inconclusive.");
        if (after.Findings.Any(finding => finding.Id == selected.Id))
            errors.Add("The selected rule still fails.");
        foreach (var occurrence in selected.Occurrences)
        {
            var page = after.Pages.SingleOrDefault(page => page.PageId == occurrence.ScenarioName);
            if (selected.AutomationId is null || page?.ObservedControlIds.Contains(selected.AutomationId, StringComparer.Ordinal) != true)
                errors.Add($"The selected control disappeared from '{occurrence.ScenarioName}'.");
        }
        var owner = after.Pages.SingleOrDefault(page => page.PageId == selected.OwnerScenarioName);
        if (owner?.Exercise?.Checks.SingleOrDefault(check => check.RuleId == "accessible-name")?.Passed != true)
            errors.Add("The fixed Name does not match the frozen intended wording.");
        foreach (var finding in after.Findings)
        {
            var existing = previous.Findings.SingleOrDefault(item => item.Id == finding.Id);
            foreach (var occurrence in finding.Occurrences.GroupBy(item => item.ScenarioName, StringComparer.Ordinal))
                if (occurrence.Count() > (existing?.Occurrences.Count(item => item.ScenarioName == occurrence.Key) ?? 0))
                    errors.Add($"New or increased rule failure '{finding.RuleId}' in '{occurrence.Key}'.");
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool FunctionChecksPass(JourneyRound round) =>
        round.Pages.All(page => page.Exercise is null || page.Exercise.Checks.All(check => check.Passed));

    public static bool HasRequiredAuditEvidence(AuditReport audit)
    {
        if (audit.Error is not null || audit.Axe is null)
            return false;
        if (audit.Status == "completed")
            return true;
        if (audit.Status != "partial" || audit.Snapshot is not { } snapshot
            || AuditJson.OptionalString(snapshot, "status") != "partial"
            || AuditJson.Boolean(snapshot, "depthLimitReached") != false
            || AuditJson.Boolean(snapshot, "elementLimitReached") != false)
            return false;
        var privacyGap = false;
        var issues = AuditJson.Array(snapshot, "issues").EnumerateArray().Concat(
            AuditJson.Array(snapshot, "nodes").EnumerateArray()
                .SelectMany(node => AuditJson.Array(node, "issues").EnumerateArray()));
        foreach (var issue in issues)
        {
            var code = AuditJson.OptionalString(issue, "code");
            if (code == "privacy_unavailable")
                privacyGap = true;
            if (code is not ("privacy_notice" or "privacy_unavailable" or "privacy_withheld" or "property_unsupported"))
                return false;
        }
        return privacyGap;
    }

    public static string CaptureStatus(JourneyRound round, int expectedPages) =>
        round.Pages.Count != expectedPages || round.Pages.Any(page => page.Audit is null) ? "incomplete"
            : round.Pages.Any(page => page.Audit!.Status != "completed") ? "partial" : "completed";
}
