using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public static class AuditRules
{
    public static void ValidateSnapshot(JsonElement snapshot, AuditTarget target)
    {
        ValidateIdentity(snapshot, target);
        var status = AuditJson.String(snapshot, "status");
        if (status is not ("captured" or "partial"))
            throw new InvalidDataException("Unknown snapshot completion status.");
        var maximumDepth = AuditJson.Integer(snapshot, "maxDepth");
        var maximumElements = AuditJson.Integer(snapshot, "maxElements");
        var nodes = AuditJson.Array(snapshot, "nodes");
        if (maximumDepth is < 1 or > 50 || maximumElements is < 1 or > 10000
            || nodes.GetArrayLength() == 0 || nodes.GetArrayLength() > maximumElements)
            throw new InvalidDataException("Empty or out-of-budget UIA snapshot.");
        if (AuditJson.Boolean(snapshot, "depthLimitReached") is null
            || AuditJson.Boolean(snapshot, "elementLimitReached") is null)
            throw new InvalidDataException("The snapshot omitted its truncation indicators.");
        if (status == "captured" && (AuditJson.Boolean(snapshot, "depthLimitReached") == true
            || AuditJson.Boolean(snapshot, "elementLimitReached") == true))
            throw new InvalidDataException("A truncated snapshot was labeled complete.");
        _ = AuditJson.Array(snapshot, "issues");
        _ = Timestamp(snapshot, "capturedAtUtc");
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var rootCount = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var id = AuditJson.String(node, "id");
            var depth = AuditJson.Integer(node, "depth");
            var parent = AuditJson.OptionalString(node, "parentId");
            if (id.Length is < 1 or > 256 || depth < 0 || depth > maximumDepth || !depths.TryAdd(id, depth))
                throw new InvalidDataException("Snapshot nodes have invalid or duplicate identities/depths.");
            if (parent is null)
            {
                if (depth != 0)
                    throw new InvalidDataException("The snapshot root has a nonzero depth.");
                rootCount++;
            }
            else if (!depths.TryGetValue(parent, out var parentDepth) || parentDepth != depth - 1)
                throw new InvalidDataException("Snapshot node hierarchy is incomplete or cyclic.");
            _ = AuditJson.String(node, "controlType");
            _ = AuditJson.Array(node, "textRuns");
            _ = AuditJson.Array(node, "issues");
        }
        if (rootCount != 1)
            throw new InvalidDataException("A window snapshot must have exactly one root.");
    }

    public static void ValidateObservation(JsonElement observation, AuditTarget target, AuditOptions options)
    {
        ValidateIdentity(observation, target);
        if (AuditJson.String(observation, "status") is not ("completed" or "partial"))
            throw new InvalidDataException("Unknown observation completion status.");
        var started = Timestamp(observation, "startedAtUtc");
        var ended = Timestamp(observation, "endedAtUtc");
        var requested = AuditJson.Integer(observation, "requestedTabSteps");
        var completed = AuditJson.Integer(observation, "completedTabSteps");
        var maximumEvents = AuditJson.Integer(observation, "maxEvents");
        if (ended < started || requested != options.TabSteps || completed < 0 || completed > 2 * requested
            || AuditJson.Integer(observation, "durationSec") != options.ObserveSeconds
            || maximumEvents is < 1 or > 5000
            || AuditJson.Array(observation, "events").GetArrayLength() > maximumEvents
            || AuditJson.Boolean(observation, "eventLimitReached") is null)
            throw new InvalidDataException("Observation scope, timing or budgets do not match the request.");
        _ = AuditJson.Array(observation, "issues");
        var lastSequence = -1;
        foreach (var point in AuditJson.Array(observation, "focusTrace").EnumerateArray())
        {
            var sequence = AuditJson.Integer(point, "sequence");
            if (sequence <= lastSequence || AuditJson.OptionalString(point, "direction") is not ("initial" or "forward" or "reverse")
                || AuditJson.Boolean(point, "isInTarget") is null)
                throw new InvalidDataException("Focus trace order or scope is invalid.");
            lastSequence = sequence;
        }
    }

    public static IEnumerable<AuditCheckResult> SnapshotResults(JsonElement snapshot)
    {
        yield return Result("capture.scope", "observed", "The requested process and exact window identity were verified.",
            ["snapshot.json"]);
        yield return Result("capture.tree", AuditJson.String(snapshot, "status") == "captured" ? "observed" : "inconclusive",
            $"Captured {AuditJson.Array(snapshot, "nodes").GetArrayLength()} UIA nodes. " +
            "Scope, truncation, privacy withholding and provider errors are preserved in snapshot.json.", ["snapshot.json"]);
        var nodes = AuditJson.Array(snapshot, "nodes").EnumerateArray().ToArray();
        var missing = nodes.Count(node => string.IsNullOrWhiteSpace(AuditJson.OptionalString(node, "automationId")));
        var duplicateIds = nodes.Select(node => AuditJson.OptionalString(node, "automationId"))
            .Where(id => !string.IsNullOrWhiteSpace(id)).GroupBy(id => id, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);
        yield return Result("uia.identifiers", "observed",
            $"{missing} nodes have no AutomationId; {duplicateIds} IDs match multiple nodes in this window. " +
            "This describes testability, not an accessibility violation or a requirement for global ID uniqueness.", ["snapshot.json"]) with
        {
            Details = new Dictionary<string, string>
            {
                ["missingIdNodes"] = missing.ToString(CultureInfo.InvariantCulture),
                ["windowWideDuplicateIds"] = duplicateIds.ToString(CultureInfo.InvariantCulture)
            }
        };
        foreach (var result in TextContrast.Evaluate(snapshot))
            yield return result;
    }

    public static IEnumerable<AuditCheckResult> AxeResults(AccessibilityScan scan, JsonElement? snapshot, AuditTarget target)
    {
        if (scan.ProcessId != target.ProcessId || scan.WindowHandle != target.WindowHandle
            || string.IsNullOrWhiteSpace(scan.EngineVersion) || scan.ResultGroupErrorCounts.Count == 0
            || scan.ResultGroupErrorCounts.Any(count => count < 0) || scan.Findings.Count > scan.RawErrorCount)
            throw new InvalidDataException("Axe returned inconsistent target, version or result counts.");
        if (scan.Findings.Count == 0)
        {
            yield return Result("axe.scan", "no-violations-reported",
                "Axe.Windows returned nonempty scan groups and no rule failures. This is not an all-rules or WCAG pass list.",
                ["axe.json"]);
            yield break;
        }
        var nodes = snapshot is { } data ? AuditJson.Array(data, "nodes").EnumerateArray().ToArray() : [];
        foreach (var finding in scan.Findings)
        {
            if (string.IsNullOrWhiteSpace(finding.RuleId))
                throw new InvalidDataException("Axe returned a finding without a rule ID.");
            if (finding.ElementProperties.TryGetValue("ProcessId", out var pid)
                && (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var findingPid)
                    || findingPid != target.ProcessId))
                throw new InvalidDataException("Axe returned an element from outside the target process.");
            var runtimeId = finding.ElementProperties.GetValueOrDefault("RuntimeId");
            var matches = string.IsNullOrEmpty(runtimeId) ? [] : nodes
                .Where(node => SameRuntimeId(runtimeId, AuditJson.OptionalString(node, "runtimeId"))).Take(2).ToArray();
            yield return Result("axe.scan", "rule-failure", finding.Description, ["axe.json"]) with
            {
                SourceRuleId = finding.RuleId,
                ElementId = matches.Length == 1 ? AuditJson.String(matches[0], "id") : null,
                ElementName = finding.ElementProperties.GetValueOrDefault("Name"),
                Details = new Dictionary<string, string>
                {
                    ["howToFix"] = finding.HowToFix,
                    ["standard"] = finding.Standard,
                    ["automationId"] = finding.AutomationId ?? "(not supplied)",
                    ["runtimeId"] = runtimeId ?? "(not supplied)",
                    ["snapshotAssociation"] = matches.Length == 1 ? "matched within this capture" : "not uniquely matched; snapshots are not atomic"
                }
            };
        }
    }

    public static IEnumerable<AuditCheckResult> ObservationResults(
        JsonElement observation, JsonElement? snapshot, AuditOptions options)
    {
        var events = AuditJson.Array(observation, "events").GetArrayLength();
        yield return Result("events.timeline", AuditJson.String(observation, "status") == "completed" ? "observed" : "inconclusive",
            $"Received {events} scoped UIA events during the recorded interval. No event is not proof of a missing announcement.",
            ["observation.json"]);
        if (options.TabSteps == 0)
            yield break;
        var trace = AuditJson.Array(observation, "focusTrace").EnumerateArray().ToArray();
        var completed = AuditJson.Integer(observation, "completedTabSteps");
        yield return Result("keyboard.path", completed == 2 * options.TabSteps ? "observed" : "inconclusive",
            $"Delivered {completed} of {2 * options.TabSteps} budgeted Tab/Shift+Tab steps. " +
            "The trace records this path only; it does not certify logical order or complete keyboard access.", ["observation.json"]);
        var repeats = trace.Count(point => AuditJson.Boolean(point, "repeat") == true);
        yield return Result("keyboard.repeats", "observed",
            $"The probe recorded {repeats} repeated focus observations. Repeats and normal dialog loops are not keyboard-trap verdicts.",
            ["observation.json"]);
        if (snapshot is not { } data)
        {
            yield return Result("keyboard.unvisited", "inconclusive",
                "No usable baseline tree is available for comparing focus candidates.", ["observation.json"]);
            yield break;
        }
        var visited = trace.Where(point => AuditJson.Boolean(point, "isInTarget") == true)
            .Select(point => NormalizeRuntimeId(AuditJson.OptionalString(point, "runtimeId")))
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var candidates = AuditJson.Array(data, "nodes").EnumerateArray().Where(node =>
            AuditJson.Boolean(node, "isKeyboardFocusable") == true && AuditJson.Boolean(node, "isEnabled") == true
            && AuditJson.Boolean(node, "isOffscreen") == false && AuditJson.Boolean(node, "isPassword") == false).ToArray();
        var unmatched = candidates.Where(node => NormalizeRuntimeId(AuditJson.OptionalString(node, "runtimeId")) is null).ToArray();
        var notVisited = candidates.Where(node => NormalizeRuntimeId(AuditJson.OptionalString(node, "runtimeId")) is { } id
            && !visited.Contains(id)).ToArray();
        yield return Result("keyboard.unvisited", "observed",
            $"{notVisited.Length} baseline focus candidates were not visited by this bounded path; " +
            $"{unmatched.Length} lacked a comparable runtime identity. Neither count proves unreachability.",
            ["snapshot.json", "observation.json"]) with
        {
            Details = new Dictionary<string, string>
            {
                ["notVisitedNodeIds"] = string.Join(", ", notVisited.Select(node => AuditJson.String(node, "id"))),
                ["identityUnavailableNodes"] = unmatched.Length.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    public static AuditCheckResult Result(
        string checkId, string status, string summary, IReadOnlyList<string>? evidence = null) =>
        new() { CheckId = checkId, Status = status, Summary = summary, Evidence = evidence ?? [] };

    public static bool SameRuntimeId(string? left, string? right) =>
        NormalizeRuntimeId(left) is { } normalized && normalized == NormalizeRuntimeId(right);

    private static string? NormalizeRuntimeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private static void ValidateIdentity(JsonElement result, AuditTarget target)
    {
        if (AuditJson.Integer(result, "schemaVersion") != 1 || AuditJson.Integer(result, "processId") != target.ProcessId
            || AuditJson.Long(result, "windowHandle") != target.WindowHandle)
            throw new InvalidDataException("Native audit output does not match the requested window, process or schema.");
    }

    private static DateTimeOffset Timestamp(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || !property.TryGetDateTimeOffset(out var result))
            throw new InvalidDataException($"Missing timestamp '{name}' in native audit output.");
        return result;
    }
}
