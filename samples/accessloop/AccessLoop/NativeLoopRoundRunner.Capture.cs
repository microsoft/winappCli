using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeLoopRoundRunner
{
    private sealed partial class RoundExecution
    {
        private async Task CaptureAsync(LoopStep step, CancellationToken token)
        {
            var directory = NewDirectory(Path.Combine("states", step.Id));
            var state = new LoopStateResult { StepId = step.Id, PageId = step.PageId, Name = step.Name, Directory = directory };
            round.States.Add(state);
            var beforePath = Path.Combine(directory, "controls-before.json");
            var afterPath = Path.Combine(directory, "controls-after.json");
            try
            {
                Require(step.RequiredIds.Count > 0 && step.RequiredIds.Distinct(StringComparer.Ordinal).Count() == step.RequiredIds.Count,
                    "A checkpoint requires its explicit unique control inventory.");
                await ReadRequiredControlsAsync(step, state.BeforeControls, token);
                AuditFiles.WriteNew(beforePath, state.BeforeControls);
                monitor.Check(target, audit: true);
                var audit = await new AuditRunner(auditPlatform).RunAsync(Options(), target, owner.version, directory, token);
                var scanPath = Path.Combine(directory, "scan.json");
                AuditFiles.WriteNew(scanPath, audit);
                AuditReportRenderer.Write(Path.Combine(directory, "report.html"), audit);
                state.ScanSha256 = AuditFiles.Hash(scanPath);
                state.CaptureStatus = audit.Status;
                state.RuleFailureOccurrences = audit.RuleFailureCount;

                monitor.Check(target, audit: true);
                await ReadRequiredControlsAsync(step, state.AfterControls, token);
                AuditFiles.WriteNew(afterPath, state.AfterControls);
                foreach (var (id, before) in state.BeforeControls)
                {
                    Require(state.AfterControls.TryGetValue(id, out var after), $"Required control '{id}' disappeared during capture.");
                    RequireStableControl(id, before.GetProperty("properties"), after.GetProperty("properties"));
                }
                ValidateAudit(step, state, audit);
                state.Status = "captured";
                Emit("checkpoint-captured", step.Id, $"Recorded {audit.RuleFailureCount} raw rule-failure occurrences; capture status remains {audit.Status}.");
            }
            catch (Exception ex)
            {
                state.Status = "blocked";
                state.Error = monitor.Violation?.Message ?? Error(ex);
                throw;
            }
            finally
            {
                state.FinishedAtUtc = DateTimeOffset.UtcNow;
                if (!File.Exists(beforePath)) AuditFiles.WriteNew(beforePath, state.BeforeControls);
                if (!File.Exists(afterPath)) AuditFiles.WriteNew(afterPath, state.AfterControls);
                AuditFiles.WriteNew(Path.Combine(directory, "state.json"), state);
                SaveProgress();
            }
        }

        private async Task ReadRequiredControlsAsync(
            LoopStep step, Dictionary<string, JsonElement> destination, CancellationToken token)
        {
            var tree = await InspectAsync(token);
            RequirePage(tree, step.PageId);
            var page = profile.Pages.Single(item => item.Id == step.PageId);
            foreach (var id in step.RequiredIds.Append(page.ReadyId).Distinct(StringComparer.Ordinal))
            {
                var control = await ControlAsync(tree, id, id == page.ReadyId ? "Text" : null, token);
                destination[id] = control.Response;
                Require(!Flag(control.Properties, "IsOffscreen"), $"Required checkpoint control '{id}' is not visible.");
                _ = Flag(control.Properties, "IsEnabled");
                _ = Flag(control.Properties, "IsPassword");
                Require(control.Properties.TryGetProperty("Name", out var name)
                    && name.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                    $"Required Name metadata for '{id}' is unavailable. A present null Name is retained, not replaced.");
            }
        }

        private static void RequireStableControl(string id, JsonElement before, JsonElement after)
        {
            foreach (var name in new[]
            {
                "AutomationId", "ControlType", "Name", "IsEnabled", "IsPassword", "IsOffscreen", "ClassName",
                "RuntimeId", "HasKeyboardFocus", "IsSelected", "ToggleState", "ExpandCollapseState",
                "ScrollVerticalPercent", "ScrollVerticalViewSize", "ScrollHorizontalPercent", "SelectionAutomationIds"
            })
            {
                var presentBefore = before.TryGetProperty(name, out var left);
                var presentAfter = after.TryGetProperty(name, out var right);
                var same = presentBefore == presentAfter && (!presentBefore || JsonElement.DeepEquals(left, right));
                if (presentBefore && presentAfter && name == "ScrollVerticalPercent" && TryNumber(left, out var first) && TryNumber(right, out var last))
                    same = Near(first, last);
                Require(same, $"Required control '{id}' changed '{name}' during the passive checkpoint. Its scan does not count as verified coverage.");
            }
        }

        private void ValidateAudit(LoopStep step, LoopStateResult state, AuditReport audit)
        {
            Require(audit.Status is "completed" or "partial" && audit.Snapshot is not null && audit.Axe is not null,
                "The checkpoint lacks a valid scoped snapshot and Axe output. " + audit.Error);
            Require(audit.Target.ProcessId == target.ProcessId && audit.Target.WindowHandle == target.WindowHandle
                && audit.Target.ProcessStartedAtUtc == target.ProcessStartedAtUtc && audit.Target.ProcessName == target.ProcessName,
                "The audit changed the original target identity.");
            var snapshot = audit.Snapshot!.Value;
            Require(AuditJson.Boolean(snapshot, "depthLimitReached") == false && AuditJson.Boolean(snapshot, "elementLimitReached") == false
                && !audit.Results.Any(check => check.Status == "blocked" && check.CheckId is "capture.scope" or "capture.tree" or "axe.scan"),
                "Required audit evidence is blocked or the UIA tree is truncated. " + audit.Error);
            var nodes = AuditJson.Array(snapshot, "nodes").EnumerateArray().ToArray();
            var issues = AuditJson.Array(snapshot, "issues").EnumerateArray()
                .Concat(nodes.SelectMany(node => AuditJson.Array(node, "issues").EnumerateArray()));
            foreach (var issue in issues)
                Require(Text(issue, "code") is "privacy_notice" or "privacy_unavailable" or "privacy_withheld" or "property_unsupported",
                    "A non-auxiliary UIA capture error prevents verified checkpoint coverage.");
            foreach (var (id, before) in state.BeforeControls)
            {
                var matches = nodes.Where(node => Text(node, "automationId") == id).ToArray();
                var properties = before.GetProperty("properties");
                Require(matches.Length == 1 && Text(matches[0], "controlType") == Text(properties, "ControlType")
                    && AuditJson.Boolean(matches[0], "isOffscreen") == false,
                    $"Required control '{id}' is missing, ambiguous, hidden or has a different type in the audit snapshot.");
                Require(Text(matches[0], "name") == Text(properties, "Name")
                    || LoopEvidence.SnapshotNameIsWithheld(matches[0]),
                    $"Required control '{id}' changed its observed Name between native properties and the audit snapshot.");
            }
            foreach (var repair in profile.RepairTargets.Where(repair => repair.CheckpointId == step.Id))
            {
                Require(state.BeforeControls.TryGetValue(repair.AutomationId, out var control)
                    && Text(control.GetProperty("properties"), "ControlType") == "Button"
                    && Text(control.GetProperty("properties"), "IsPassword") == "False",
                    "The declared source repair target is not a uniquely witnessed non-password Button.");
            }
        }

        private sealed class AuditAdapter(RoundExecution owner) : IAuditPlatform
        {
            public Task<string> VersionAsync(CancellationToken token) => Task.FromResult(owner.owner.version);
            public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) =>
                throw new InvalidOperationException("A checkpoint never attaches to a replacement process/window.");
            public void VerifyTarget(AuditTarget target) => owner.monitor.Check(target, audit: true);
            public Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
                owner.ExecuteUiAsync(target, arguments, timeout, token, audit: true);
            public async Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
            {
                owner.CheckCommand(target, token, audit: true);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, owner.monitor.StopToken);
                bounded.CancelAfter(TimeSpan.FromSeconds(owner.profile.TimeoutSeconds));
                try
                {
                    var scan = await owner.inner.AxeAsync(target,
                        TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, owner.profile.TimeoutSeconds)), bounded.Token);
                    owner.RecordResponse("axe", ["_axe-worker", Number(target.ProcessId), Number(target.WindowHandle),
                        target.ProcessStartedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        JsonSerializer.Serialize(scan, Json.Options), "");
                    return scan;
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested
                    && bounded.IsCancellationRequested && owner.monitor.Violation is null)
                {
                    throw new TimeoutException($"Axe exceeded the {owner.profile.TimeoutSeconds}-second command timeout.", ex);
                }
                finally { owner.monitor.Check(target, audit: true); }
            }
        }
    }
}
