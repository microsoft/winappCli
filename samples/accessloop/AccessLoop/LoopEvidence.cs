using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public sealed record LoopVerifiedState(LoopStateResult State, AuditReport Scan);
public sealed record LoopNameCheck(
    string AutomationId, string Expected, string? Actual, bool Passed, string CheckpointId)
{
    public string EvidenceSource { get; init; } = "snapshot-and-control-witness";
}
public sealed record LoopFindingOccurrence(
    string CheckpointId, string PageId, string StateDirectory, int ResultIndex,
    string? AutomationId, string? ControlType, string? ElementName, string RuleId, string Description);
public sealed record LoopFindingGroup(
    string Key, string RuleId, string? AutomationId, string Description, bool MappedTarget,
    IReadOnlyList<LoopFindingOccurrence> Occurrences);
public sealed record LoopVerifiedRound(
    LoopRoundResult Round, IReadOnlyList<LoopVerifiedState> States,
    IReadOnlyList<LoopFindingGroup> Findings, IReadOnlyList<LoopNameCheck> NameChecks,
    string CaptureStatus);

public static class LoopEvidence
{
    public static LoopVerifiedRound VerifyRound(LoopProfile profile, LoopRoundResult round, string reportRoot)
    {
        LoopInput.Validate(profile);
        var directory = BoundDirectory(reportRoot, round.Directory);
        var planPath = AuditFiles.ResolveArtifact(reportRoot, "plan.json");
        Require(AuditFiles.Hash(planPath) == LoopInput.PlanHash(profile), "The frozen preset plan changed.");
        Require(round.Status == "completed" && round.Target is not null && round.FinishedAtUtc is not null
            && round.StartedAtUtc != default && round.FinishedAtUtc >= round.StartedAtUtc
            && round.Error is null, "The route did not complete with an identified owned target.");
        var target = round.Target ?? throw new InvalidDataException("The round has no target identity.");
        Require(target.ProcessName == profile.ProcessName, "The recorded process does not match the preset.");
        if (round.Sources is null || round.Actions is null || round.States is null)
            throw new InvalidDataException("The round is missing its source, action or state records.");
        Require(round.Sources.Count > 0, "The round has no source-version inventory.");
        var archive = round.SourceDirectory is not null
            ? BoundDirectory(reportRoot, round.SourceDirectory)
            : throw new InvalidDataException("The round has no archived source version for reproduction.");
        var archivedProject = AuditFiles.ResolveArtifact(archive, Path.GetFileName(profile.Project));
        Require(SameSources(round.Sources, JourneyInput.CaptureSources(archivedProject)),
            "The archived sources no longer match this round.");
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repair in profile.RepairTargets)
            if (SourceRepair.Locate(archivedProject, repair.AutomationId).Count == 1)
                mapped.Add(repair.AutomationId);

        var actionsPath = AuditFiles.ResolveArtifact(directory, "actions.json");
        Require(round.ActionLogSha256 is not null && AuditFiles.Hash(actionsPath) == round.ActionLogSha256,
            "The actual action log is missing or changed.");
        using var actionsDocument = AuditFiles.ParseStrict(AuditFiles.ReadBytes(actionsPath, AuditFiles.MaxJsonBytes));
        var actions = AuditFiles.Deserialize<List<LoopActionResult>>(actionsDocument.RootElement);
        Require(actions.Count == profile.Steps.Count && round.Actions.Count == actions.Count,
            "The action log does not contain the complete declared route.");
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var planned = profile.Steps[index];
            if (action is null || action.Step is null || action.Commands is null || action.Checks is null)
                throw new InvalidDataException("An action is missing its declaration, commands or checks.");
            Require(Equal(action, round.Actions[index]) && Equal(action.Step, planned),
                "Recorded actions or acceptance inputs differ from the frozen route.");
            Require(action.Status == "completed" && action.Error is null && action.FinishedAtUtc is not null
                && action.StartedAtUtc >= round.StartedAtUtc && action.FinishedAtUtc >= action.StartedAtUtc
                && action.FinishedAtUtc <= round.FinishedAtUtc, $"Step '{planned.Id}' did not complete.");
            Require(action.Commands.Count > 0, $"Step '{planned.Id}' has no native execution evidence.");
            foreach (var command in action.Commands)
            {
                var response = BoundFile(directory, command.ResponseFile);
                Require(AuditFiles.Hash(response) == command.ResponseSha256, "A native command response changed.");
                if (command.ErrorFile is not null)
                    Require(command.ErrorSha256 is not null && AuditFiles.Hash(BoundFile(directory, command.ErrorFile)) == command.ErrorSha256,
                        "A native command diagnostic changed.");
                else
                    Require(command.ErrorSha256 is null, "A diagnostic hash has no corresponding file.");
            }
            Require(action.Checks.All(check => check.Passed
                && MatchesExpected(check.Property, check.Expected, check.Actual)), $"Step '{planned.Id}' has an unverified assertion.");
            foreach (var expectation in planned.Expectations)
            {
                var checks = action.Checks.Where(check => check.TargetId == expectation.TargetId
                    && check.Property == expectation.Property && JsonElement.DeepEquals(check.Expected, expectation.Expected)).ToArray();
                Require(checks.Length == 1, $"Step '{planned.Id}' did not record its exact declared expectation.");
            }
            if (planned.Kind == "key")
            {
                var before = Properties(action.Before ?? throw new InvalidDataException("A keyboard step has no real before-input witness."));
                Require(Text(before, "AutomationId") == planned.TargetId && Text(before, "ControlType") == planned.ControlType
                    && Text(before, "HasKeyboardFocus") == "True" && Text(before, "IsEnabled") == "True"
                    && Text(before, "IsPassword") == "False", "Keyboard reachability was not witnessed before input.");
                Require(action.Commands.Any(command => command.Operation == "send-keys")
                    && !action.Commands.Any(command => command.Operation is "focus" or "invoke" or "replay" or "set-value"),
                    "A keyboard step must use reviewed native input without a focus/invoke fallback.");
                VerifyKeyInput(planned, action, directory, target);
            }
        }

        var checkpoints = profile.Steps.Where(step => step.Kind == "scan").ToArray();
        Require(round.States.Count == checkpoints.Length
            && round.States.Select(state => state.StepId).Distinct(StringComparer.Ordinal).Count() == checkpoints.Length,
            "A checkpoint is missing or duplicated.");
        var states = new List<LoopVerifiedState>();
        var occurrences = new List<(string Key, bool Mapped, LoopFindingOccurrence Occurrence)>();
        var names = new List<LoopNameCheck>();
        foreach (var checkpoint in checkpoints)
        {
            var state = round.States.SingleOrDefault(item => item.StepId == checkpoint.Id)
                ?? throw new InvalidDataException("The declared checkpoint was not captured.");
            Require(state.PageId == checkpoint.PageId && state.Name == checkpoint.Name && state.Status == "captured"
                && state.Error is null && state.FinishedAtUtc is not null
                && state.StartedAtUtc >= round.StartedAtUtc && state.FinishedAtUtc <= round.FinishedAtUtc,
                "Checkpoint identity or completion is not verified.");
            var folder = BoundDirectory(directory, state.Directory);
            var scanPath = AuditFiles.ResolveArtifact(folder, "scan.json");
            Require(state.ScanSha256 is not null && AuditFiles.Hash(scanPath) == state.ScanSha256,
                "Checkpoint scan.json changed.");
            var scan = AuditFiles.ReadVerified(scanPath);
            Require(scan.Status is "completed" or "partial" && scan.Snapshot is not null && scan.Axe is not null
                && scan.StartedAtUtc >= state.StartedAtUtc && scan.FinishedAtUtc <= state.FinishedAtUtc
                && SameTarget(scan.Target, target), "The checkpoint lacks correctly bound audit evidence.");
            Require(state.CaptureStatus == scan.Status && state.RuleFailureOccurrences == scan.RuleFailureCount,
                "Cached checkpoint status/counts disagree with actual evidence.");
            var snapshot = scan.Snapshot ?? throw new InvalidDataException("The checkpoint has no snapshot.");
            Require(AuditJson.Boolean(snapshot, "depthLimitReached") == false
                && AuditJson.Boolean(snapshot, "elementLimitReached") == false
                && !scan.Results.Any(result => result.Status == "blocked" && result.CheckId is "capture.tree" or "axe.scan"),
                "Required snapshot/Axe evidence is blocked or truncated.");
            var nodes = AuditJson.Array(snapshot, "nodes").EnumerateArray().ToArray();
            var page = profile.Pages.Single(item => item.Id == checkpoint.PageId);
            foreach (var id in checkpoint.RequiredIds.Append(page.ReadyId).Distinct(StringComparer.Ordinal))
            {
                if (!state.BeforeControls.TryGetValue(id, out var before)
                    || !state.AfterControls.TryGetValue(id, out var after))
                    throw new InvalidDataException($"Required checkpoint control '{id}' has no before/after witnesses.");
                var left = Properties(before);
                var right = Properties(after);
                Require(Text(left, "AutomationId") == id && Text(right, "AutomationId") == id
                    && Text(left, "ControlType") is { } type && type == Text(right, "ControlType")
                    && Text(left, "IsOffscreen") == "False" && Text(right, "IsOffscreen") == "False",
                    $"Required checkpoint control '{id}' was not stably visible.");
                foreach (var property in new[] { "Name", "IsEnabled", "IsPassword" })
                    Require(SameProperty(left, right, property), $"Required control '{id}' changed during capture.");
                var matches = nodes.Where(node => Text(node, "automationId") == id).ToArray();
                Require(matches.Length == 1 && Text(matches[0], "controlType") == Text(left, "ControlType")
                    && AuditJson.Boolean(matches[0], "isOffscreen") == false,
                    $"Required control '{id}' is missing, ambiguous or mismatched in the audit snapshot.");
            }
            foreach (var repair in profile.RepairTargets.Where(item => item.CheckpointId == checkpoint.Id))
            {
                var node = nodes.Single(item => Text(item, "automationId") == repair.AutomationId);
                Require(Text(node, "controlType") == "Button" && AuditJson.Boolean(node, "isPassword") == false,
                    "A source repair target lacks unambiguous non-password Button semantics.");
                var witness = Properties(state.AfterControls[repair.AutomationId]);
                var withheld = SnapshotNameIsWithheld(node);
                var actual = Text(witness, "Name");
                Require((withheld || Text(node, "name") == actual) && Text(witness, "IsPassword") == "False",
                    "The target name cannot be corroborated by its state witness.");
                names.Add(new(repair.AutomationId, repair.ExpectedName, actual, actual == repair.ExpectedName, checkpoint.Id)
                {
                    EvidenceSource = withheld ? "before-after-control-witness; snapshot-name-privacy-withheld" : "snapshot-and-control-witness"
                });
            }
            for (var index = 0; index < scan.Results.Count; index++)
            {
                var result = scan.Results[index];
                if (result.Status != "rule-failure")
                    continue;
                var matches = nodes.Where(node => Text(node, "id") == result.ElementId).ToArray();
                var node = matches.Length == 1 ? matches[0] : (JsonElement?)null;
                var id = node is { } element ? Text(element, "automationId") : null;
                var type = node is { } typed ? Text(typed, "controlType") : null;
                var known = id is not null && mapped.Contains(id) && type == "Button"
                    && profile.RepairTargets.Any(repair => repair.AutomationId == id && repair.PageId == checkpoint.PageId)
                    && nodes.Count(element => Text(element, "automationId") == id) == 1
                    && state.BeforeControls.ContainsKey(id) && state.AfterControls.ContainsKey(id);
                var rule = result.SourceRuleId ?? result.CheckId;
                var occurrence = new LoopFindingOccurrence(checkpoint.Id, checkpoint.PageId, folder, index,
                    id, type, result.ElementName, rule, result.Summary);
                var key = known ? $"{checkpoint.PageId}:{id}:{rule}" : $"{checkpoint.Id}:occurrence-{index}";
                occurrences.Add((key, known, occurrence));
            }
            states.Add(new(state, scan));
        }
        var findings = occurrences.GroupBy(item => item.Key, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            return new LoopFindingGroup(group.Key, first.Occurrence.RuleId, first.Occurrence.AutomationId,
                first.Occurrence.Description, first.Mapped, group.Select(item => item.Occurrence).ToArray());
        }).ToArray();
        Require(names.Count == profile.RepairTargets.Count, "Required accessible-name contracts are incomplete.");
        return new(round, states, findings, names, states.All(state => state.Scan.Status == "completed") ? "completed" : "partial");
    }

    public static bool MatchesExpected(string property, JsonElement expected, JsonElement? actual)
    {
        if (actual is not { } value)
            return false;
        if (property != "ScrollVerticalPercent")
            return JsonElement.DeepEquals(expected, value);
        if (expected.ValueKind != JsonValueKind.Number || !expected.TryGetDouble(out var desired) || !double.IsFinite(desired))
            return false;
        double observed;
        var valid = value.ValueKind == JsonValueKind.Number
            ? value.TryGetDouble(out observed)
            : double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                NumberStyles.Float, CultureInfo.InvariantCulture, out observed);
        return valid && double.IsFinite(observed) && Math.Abs(observed - desired) <= 0.1;
    }

    public static bool SameSources(IReadOnlyList<JourneySourceStamp> expected, IReadOnlyList<JourneySourceStamp> actual) =>
        expected.Count == actual.Count && expected.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() == expected.Count
        && actual.All(item => expected.Count(original => original.RelativePath == item.RelativePath && original.Sha256 == item.Sha256) == 1);

    public static bool SnapshotNameIsWithheld(JsonElement node) =>
        node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Null
        && AuditJson.Boolean(node, "isPassword") == false
        && AuditJson.Array(node, "issues").EnumerateArray().Any(issue => Text(issue, "code") == "privacy_withheld");

    private static void VerifyKeyInput(LoopStep step, LoopActionResult action, string root, AuditTarget target)
    {
        var keys = step.Keys?.ToLowerInvariant() ?? throw new InvalidDataException("The frozen step has no key.");
        var invocations = action.Commands.Where(command => command.Operation == "send-keys").ToArray();
        Require(invocations.Length == 1
            && invocations[0].Arguments.SequenceEqual(["send-keys", keys, "--via", "send-input"], StringComparer.Ordinal),
            "The native invocation changed the frozen key, transport, or added corrective focus.");
        using var response = AuditFiles.ParseStrict(AuditFiles.ReadBytes(
            BoundFile(root, invocations[0].ResponseFile), 1024 * 1024));
        var sent = response.RootElement;
        Require(Text(sent, "keys") == keys && Text(sent, "via") == "send-input"
            && AuditJson.Integer(sent, "actionCount") == 1 && AuditJson.Long(sent, "hwnd") == target.WindowHandle
            && (!sent.TryGetProperty("target", out var correctiveFocus) || correctiveFocus.ValueKind == JsonValueKind.Null)
            && AuditJson.Array(sent, "warnings").GetArrayLength() == 0,
            "Native input did not confirm the exact single key and original window without corrective focus.");
    }

    public static JsonElement Properties(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object ? properties
            : throw new InvalidDataException("A native control witness must contain its original properties object.");

    public static string BoundFile(string root, string file) =>
        AuditFiles.ResolveArtifact(root, Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file)));

    public static string BoundDirectory(string root, string directory)
    {
        var full = Path.GetFullPath(directory);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        Require(full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "An evidence directory escapes its declared root.");
        AuditFiles.EnsureDirectory(full);
        return full;
    }

    private static bool SameTarget(AuditTarget scan, AuditTarget target) =>
        scan.ProcessId == target.ProcessId && scan.WindowHandle == target.WindowHandle
        && scan.ProcessName == target.ProcessName && scan.ProcessStartedAtUtc == target.ProcessStartedAtUtc
        && (target.RootRuntimeId is null || AuditRules.SameRuntimeId(scan.RootRuntimeId, target.RootRuntimeId));

    private static string? Text(JsonElement element, string name) => AuditJson.OptionalString(element, name);

    private static bool SameProperty(JsonElement left, JsonElement right, string property) =>
        left.TryGetProperty(property, out var first) && right.TryGetProperty(property, out var second)
        && JsonElement.DeepEquals(first, second);

    private static bool Equal<T>(T left, T right) =>
        JsonSerializer.Serialize(left, Json.Options) == JsonSerializer.Serialize(right, Json.Options);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
