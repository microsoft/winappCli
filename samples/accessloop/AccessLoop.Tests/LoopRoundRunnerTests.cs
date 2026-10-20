using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed partial class LoopRoundRunnerTests
{
    private string root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Environment.CurrentDirectory, ".loop-round-test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    private static LoopStep Navigate(string page) => new()
    {
        Id = "navigate-" + page, PageId = page, Name = "Prepare " + page, Kind = "navigate",
        TargetId = page switch { "basics" => "NavBasics", "scrolling" => "NavScrolling", _ => "NavDialogs" }, ControlType = "TabItem"
    };
    private static LoopStep Scan(string page, string id, params string[] required) =>
        new() { Id = id, PageId = page, Name = id, Kind = "scan", RequiredIds = required };
    private static LoopStep Key(string id, string target, string type, string keys, params LoopExpectation[] expectations) =>
        new() { Id = id, PageId = "basics", Name = id, Kind = "key", TargetId = target, ControlType = type, Keys = keys, Expectations = expectations };
    private static LoopExpectation Expect(string id, string property, object? value) =>
        new(id, property, JsonSerializer.SerializeToElement(value, Json.Options));

    private static Task<LoopRoundResult> RunAsync(Fixture fixture, LoopProfile? profile = null, CancellationToken token = default) =>
        fixture.Runner().RunAsync(profile ?? fixture.Profile(), "recorded", fixture.Output, token);

    [TestMethod]
    public async Task OwnedRoundCapturesRealNullNameFindingsAndHashBoundActionEvidence()
    {
        var fixture = new Fixture(root);
        var round = await RunAsync(fixture);
        Assert.AreEqual("completed", round.Status, round.Error);
        Assert.AreEqual(1, fixture.LaunchCount);
        Assert.AreEqual(fixture.Project, fixture.LaunchedProject);
        Assert.AreEqual(1, fixture.AttachCount);
        Assert.AreEqual(1, fixture.DisposeCount);
        Assert.IsTrue(fixture.MonitorStoppedBeforeDisposal);
        Assert.AreEqual(fixture.Target, round.Target);
        Assert.AreEqual(2, round.Actions.Count);
        Assert.IsTrue(round.Actions.All(action => action.Status == "completed"));
        Assert.AreEqual("captured", round.States.Single().Status);
        var state = round.States.Single();
        Assert.AreEqual(JsonValueKind.Null, state.BeforeControls["BasicIconButton"].GetProperty("properties").GetProperty("Name").ValueKind);
        Assert.IsTrue(state.RuleFailureOccurrences > 0, "Real rule failures remain observations, not route execution failures.");
        var scan = AuditFiles.ReadVerified(Path.Combine(state.Directory, "scan.json"));
        Assert.AreEqual(scan.RuleFailureCount, state.RuleFailureOccurrences);
        Assert.AreEqual(AuditFiles.Hash(Path.Combine(state.Directory, "scan.json")), state.ScanSha256);
        Assert.AreEqual(AuditFiles.Hash(Path.Combine(fixture.Output, "actions.json")), round.ActionLogSha256);
        Assert.AreEqual(File.ReadAllText(Path.Combine(fixture.Output, "actions.json")), File.ReadAllText(Path.Combine(fixture.Output, "actions-progress.json")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "actions-progress.pending.json")));
        Assert.IsTrue(round.Sources.Count > 0);
        Assert.IsNull(round.SourceDirectory);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Output, "sources")));
        CollectionAssert.AreEqual(round.Sources.ToArray(), JourneyInput.CaptureSources(fixture.Project).ToArray());
        var commands = round.Actions.SelectMany(action => action.Commands).ToArray();
        foreach (var command in commands)
        {
            Assert.IsTrue(Path.IsPathFullyQualified(command.ResponseFile));
            Assert.IsTrue(command.ResponseFile.StartsWith(fixture.Output + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.AreEqual(command.ResponseSha256, AuditFiles.Hash(command.ResponseFile));
        }
        var indexes = commands.Select(command => int.Parse(Path.GetFileName(command.ResponseFile).Split('-')[0])).ToArray();
        CollectionAssert.AreEqual(indexes.Order().ToArray(), indexes);
        var started = fixture.Progress.Single(item => item.Stage == "round-started");
        Assert.AreEqual(fixture.Target, started.Target);
        Assert.IsTrue(started.AtUtc <= fixture.Progress.First(item => item.Stage == "step-started").AtUtc);
        Assert.AreEqual("round-finished", fixture.Progress.Last().Stage);
    }

    [TestMethod]
    public async Task FullPresetExecutesActualKeyboardToggleDisclosureScrollDialogAndRestoration()
    {
        var fixture = new Fixture(root);
        var profile = fixture.CompletePreset();
        var archive = Path.Combine(root, "parent-source-archive");
        _ = LoopSourceWorkspace.Copy(profile.Project, archive);
        var round = await RunAsync(fixture, profile);
        Assert.AreEqual("completed", round.Status,
            $"{round.Actions.LastOrDefault()?.Step.Id}: {round.Error}; {JsonSerializer.Serialize(round.Actions.LastOrDefault()?.Checks, Json.Options)}");
        Assert.AreEqual(profile.Steps.Count, round.Actions.Count);
        Assert.AreEqual(profile.Steps.Count(step => step.Kind == "scan"), round.States.Count);
        Assert.IsTrue(round.States.All(state => state.Status == "captured"));
        Assert.AreEqual("basics", fixture.Page);
        Assert.IsFalse(fixture.Guides);
        Assert.IsFalse(fixture.Expanded);
        Assert.IsFalse(fixture.Dialog);
        Assert.AreEqual(0d, fixture.Percent);
        Assert.AreEqual("Preview refreshed", fixture.BasicStatus);
        Assert.AreEqual("Layout saved", fixture.ScrollStatus);
        foreach (var action in round.Actions.Where(action => action.Step.Kind == "key"))
        {
            Assert.IsFalse(action.Commands.Any(command => command.Operation is "focus" or "invoke" or "replay" or "set-value"));
            var before = action.Before!.Value;
            Assert.AreEqual("canonical-" + action.Step.TargetId, before.GetProperty("elementId").GetString());
            Assert.AreEqual(action.Step.TargetId, before.GetProperty("properties").GetProperty("AutomationId").GetString());
            Assert.AreEqual(action.Step.ControlType, before.GetProperty("properties").GetProperty("ControlType").GetString());
            Assert.AreEqual("True", before.GetProperty("properties").GetProperty("HasKeyboardFocus").GetString());
            Assert.IsFalse(before.TryGetProperty("reviewedFlow", out _));
            var input = action.Commands.Single(command => command.Operation == "send-keys");
            CollectionAssert.AreEqual(new[] { "send-keys", action.Step.Keys!.ToLowerInvariant(), "--via", "send-input" }, input.Arguments.ToArray());
            var preceding = action.Commands.Take(action.Commands.IndexOf(input)).ToArray();
            using var witness = JsonDocument.Parse(File.ReadAllBytes(preceding.Last(command => command.Operation == "get-property").ResponseFile));
            Assert.IsTrue(JsonElement.DeepEquals(before, witness.RootElement));
            using var focused = JsonDocument.Parse(File.ReadAllBytes(preceding.Single(command => command.Operation == "get-focused").ResponseFile));
            Assert.IsTrue(focused.RootElement.GetProperty("hasFocus").GetBoolean());
            Assert.AreEqual(action.Step.TargetId, focused.RootElement.GetProperty("element").GetProperty("automationId").GetString());
            using var response = JsonDocument.Parse(File.ReadAllBytes(input.ResponseFile));
            Assert.AreEqual(action.Step.Keys.ToLowerInvariant(), response.RootElement.GetProperty("keys").GetString());
            Assert.AreEqual("send-input", response.RootElement.GetProperty("via").GetString());
            Assert.AreEqual(1, response.RootElement.GetProperty("actionCount").GetInt32());
            Assert.AreEqual(fixture.Target.WindowHandle, response.RootElement.GetProperty("hwnd").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, response.RootElement.GetProperty("target").ValueKind);
            Assert.AreEqual(0, response.RootElement.GetProperty("warnings").GetArrayLength());
        }
        Assert.AreEqual(0, Directory.GetFiles(fixture.Output, "*flow*.json", SearchOption.AllDirectories).Length);
        round.SourceDirectory = archive;
        File.WriteAllBytes(Path.Combine(root, "plan.json"), LoopInput.PlanBytes(profile));
        var verified = LoopEvidence.VerifyRound(profile, round, root);
        Assert.AreEqual(2, verified.NameChecks.Count);
        Assert.IsTrue(verified.NameChecks.All(check => !check.Passed));
        Assert.IsTrue(verified.Findings.All(finding => finding.MappedTarget));
    }

    [TestMethod]
    public async Task FocusMismatchStopsWithoutRefocusingOrInvokingContent()
    {
        var fixture = new Fixture(root);
        var profile = fixture.Profile(Navigate("basics"), Key("wrong-focus", "BasicIconButton", "Button", "Enter"),
            Key("never-run", "BasicIconButton", "Button", "Space"));
        var round = await RunAsync(fixture, profile);
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(2, round.Actions.Count);
        Assert.AreEqual("False", round.Actions[1].Before!.Value.GetProperty("properties").GetProperty("HasKeyboardFocus").GetString());
        Assert.IsFalse(round.Actions[1].Commands.Any(command => command.Operation is "send-keys" or "replay" or "focus" or "invoke"));
        Assert.IsFalse(fixture.Calls.Any(call => call.Input && call.Arguments.Contains("canonical-BasicIconButton")));
        Assert.AreEqual("NavBasics", fixture.Focus);
        Assert.AreEqual(1, fixture.DisposeCount);
    }

    [TestMethod]
    public async Task DeliveredTabDoesNotPassUnlessTheDeclaredFocusExpectationIsObserved()
    {
        var fixture = new Fixture(root) { IgnoreTab = true };
        var propertyReads = 0;
        fixture.BeforeCommand = async (call, token) =>
        {
            if (call.Arguments is ["get-property", "PreviewGuidesToggle"] && ++propertyReads == 2)
                await Task.Delay(TimeSpan.FromSeconds(3), token);
        };
        var profile = fixture.Profile(Navigate("basics"),
            Key("tab", "NavBasics", "TabItem", "Tab", Expect("PreviewGuidesToggle", "HasKeyboardFocus", "True")),
            Key("no-space", "PreviewGuidesToggle", "Button", "Space"));
        var round = await RunAsync(fixture, profile);
        Assert.AreEqual("blocked", round.Status);
        var failed = round.Actions.Last();
        Assert.AreEqual("tab", failed.Step.Id);
        Assert.AreEqual("False", failed.Checks.Single().Actual!.Value.GetString());
        Assert.IsFalse(failed.Checks.Single().Passed);
        StringAssert.Contains(failed.Checks.Single().Error, "last successfully observed value");
        Assert.AreEqual(1, fixture.Calls.Count(call => call.Arguments[0] == "send-keys"));
    }

    [TestMethod]
    public async Task AGuardViolationAfterInputPreservesResponseAndStopsEveryLaterCommand()
    {
        var fixture = new Fixture(root);
        var countAtBoundary = 0;
        fixture.AfterCommand = call =>
        {
            if (call.Arguments[0] != "send-keys") return;
            countAtBoundary = fixture.Calls.Count;
            fixture.Windows.Foreground = 9999;
            fixture.Windows.Signal("foreground", 9999);
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"),
            Key("tab", "NavBasics", "TabItem", "Tab"), Key("space", "PreviewGuidesToggle", "Button", "Space")));
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(countAtBoundary, fixture.Calls.Count);
        Assert.IsTrue(round.ScopeEvents.Any(item => item.Kind == "foreground-changed"));
        Assert.IsTrue(round.Actions.Last().Commands.Any(command => command.Operation == "send-keys" && File.Exists(command.ResponseFile)));
        Assert.IsTrue(fixture.MonitorStoppedBeforeDisposal);
        Assert.AreEqual(1, fixture.DisposeCount);
    }

    [TestMethod]
    [DataRow("Name", "BasicsReady")]
    [DataRow("IsEnabled", "True")]
    [DataRow("IsOffscreen", "False")]
    [DataRow("IsSelected", "True")]
    [DataRow("HasKeyboardFocus", "True")]
    [DataRow("ToggleState", "Off")]
    [DataRow("ExpandCollapseState", "Collapsed")]
    public async Task ScalarExpectationsKeepExactNativeStrings(string property, string expected)
    {
        var fixture = new Fixture(root);
        var id = property switch
        {
            "IsSelected" or "HasKeyboardFocus" => "NavBasics",
            "ToggleState" => "PreviewGuidesToggle", "ExpandCollapseState" => "DensityCombo", _ => "BasicsReady"
        };
        var check = new LoopStep { Id = "assert", PageId = "basics", Name = "Exact assertion", Kind = "assert", Expectations = [Expect(id, property, expected)] };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), check));
        Assert.AreEqual("completed", round.Status, round.Error);
        Assert.AreEqual(JsonValueKind.String, round.Actions.Last().Checks.Single().Actual!.Value.ValueKind);
        Assert.AreEqual(expected, round.Actions.Last().Checks.Single().Actual!.Value.GetString());
    }

    [TestMethod]
    public async Task SelectionArrayIsNotFlattenedOrSubstitutedWithAName()
    {
        var fixture = new Fixture(root);
        var step = new LoopStep
        {
            Id = "selection", PageId = "basics", Name = "Selection", Kind = "assert",
            Expectations = [Expect("DensityCombo", "SelectionAutomationIds", new[] { "DensityStandard" })]
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), step));
        Assert.AreEqual("completed", round.Status, round.Error);
        var actual = round.Actions.Last().Checks.Single().Actual!.Value;
        Assert.AreEqual(JsonValueKind.Array, actual.ValueKind);
        Assert.AreEqual("DensityStandard", actual[0].GetString());
    }

    [TestMethod]
    public async Task FailedReadOnlyAssertionRetainsActualValuesAndStopsLaterInput()
    {
        var fixture = new Fixture(root);
        var step = new LoopStep
        {
            Id = "wrong", PageId = "basics", Name = "Wrong assertion", Kind = "assert",
            Expectations = [Expect("PreviewGuidesToggle", "ToggleState", "On")]
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), step, Key("later", "NavBasics", "TabItem", "Tab")));
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(2, round.Actions.Count);
        Assert.AreEqual("Off", round.Actions.Last().Checks.Single().Actual!.Value.GetString());
        Assert.IsNotNull(round.Actions.Last().After);
        Assert.IsFalse(fixture.Calls.Any(call => call.Arguments[0] == "send-keys"));
    }

    [TestMethod]
    public async Task NonzeroNativeInputOutputsAreWrittenAndHashedBeforeFailure()
    {
        var fixture = new Fixture(root) { NonzeroInput = true };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), Key("tab", "NavBasics", "TabItem", "Tab")));
        Assert.AreEqual("blocked", round.Status);
        var command = round.Actions.Last().Commands.Single(command => command.Operation == "send-keys");
        CollectionAssert.AreEqual(new[] { "send-keys", "tab", "--via", "send-input" }, command.Arguments.ToArray());
        Assert.AreEqual(command.ResponseSha256, AuditFiles.Hash(command.ResponseFile));
        Assert.IsNotNull(command.ErrorFile);
        Assert.AreEqual(command.ErrorSha256, AuditFiles.Hash(command.ErrorFile));
        StringAssert.Contains(File.ReadAllText(command.ErrorFile), "input_delivery_failed");
        Assert.AreEqual("NavBasics", fixture.Focus);
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("no-axe")]
    [DataRow("wrong-window")]
    [DataRow("missing-control")]
    [DataRow("changed-name")]
    public async Task IncompleteOrMismatchedAuditCannotCountAsCaptured(string defect)
    {
        var fixture = new Fixture(root) { TruncateSnapshot = defect == "truncated", FailAxe = defect == "no-axe" };
        fixture.EditSnapshot = snapshot =>
        {
            if (defect == "wrong-window") snapshot["windowHandle"] = 999;
            var nodes = snapshot["nodes"]!.AsArray();
            var button = nodes.Single(node => node!["automationId"]!.GetValue<string>() == "BasicIconButton")!;
            if (defect == "missing-control") button["automationId"] = "DifferentButton";
            if (defect == "changed-name") button["name"] = "A name never observed by get-property";
        };
        var round = await RunAsync(fixture);
        Assert.AreEqual("blocked", round.Status);
        var state = round.States.Single();
        Assert.AreEqual("blocked", state.Status);
        Assert.IsNotNull(state.ScanSha256);
        Assert.IsTrue(File.Exists(Path.Combine(state.Directory, "report.html")));
        Assert.IsTrue(File.Exists(Path.Combine(state.Directory, "controls-before.json")));
        Assert.IsTrue(File.Exists(Path.Combine(state.Directory, "controls-after.json")));
    }

    [TestMethod]
    public async Task AuxiliaryPrivacyGapsStayPartialWhileRequiredCheckpointEvidenceCanBeCaptured()
    {
        var fixture = new Fixture(root) { PartialSnapshot = true };
        var round = await RunAsync(fixture);
        Assert.AreEqual("completed", round.Status, round.Error);
        Assert.AreEqual("captured", round.States.Single().Status);
        Assert.AreEqual("partial", round.States.Single().CaptureStatus);
    }

    [TestMethod]
    public async Task PassiveCaptureCannotVerifyAControlThatChangedDuringTheScan()
    {
        var fixture = new Fixture(root);
        fixture.AfterCommand = call => { if (call.Arguments[0] == "audit-snapshot") fixture.Guides = true; };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), Scan("basics", "capture", "BasicIconButton", "PreviewGuidesToggle")));
        Assert.AreEqual("blocked", round.Status);
        var state = round.States.Single();
        Assert.AreEqual("Off", state.BeforeControls["PreviewGuidesToggle"].GetProperty("properties").GetProperty("ToggleState").GetString());
        Assert.AreEqual("On", state.AfterControls["PreviewGuidesToggle"].GetProperty("properties").GetProperty("ToggleState").GetString());
        Assert.AreEqual("blocked", state.Status);
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("unknown-password")]
    [DataRow("disabled")]
    [DataRow("offscreen")]
    [DataRow("wrong-type")]
    [DataRow("ambiguous")]
    public async Task UnsafeKeyboardTargetNeverReachesInput(string defect)
    {
        var fixture = new Fixture(root) { Focus = "BasicIconButton" };
        fixture.EditProperties = (id, properties) =>
        {
            if (id != "BasicIconButton") return;
            if (defect == "password") properties["IsPassword"] = "True";
            if (defect == "unknown-password") properties.Remove("IsPassword");
            if (defect == "disabled") properties["IsEnabled"] = "False";
            if (defect == "offscreen") properties["IsOffscreen"] = "True";
            if (defect == "wrong-type") properties["ControlType"] = "Edit";
        };
        if (defect == "ambiguous")
            fixture.EditTree = tree =>
            {
                var button = Fixture.Find(tree, "BasicIconButton")!;
                button["children"]!.AsArray().Add(new JsonObject
                {
                    ["automationId"] = "BasicIconButton", ["selector"] = "duplicate", ["type"] = "Button",
                    ["isOffscreen"] = false, ["hasMoreChildren"] = false, ["children"] = new JsonArray()
                });
            };
        var round = await RunAsync(fixture, fixture.Profile(Key("unsafe", "BasicIconButton", "Button", "Enter")));
        Assert.AreEqual("blocked", round.Status);
        Assert.IsFalse(fixture.Calls.Any(call => call.Input));
        Assert.AreEqual(1, fixture.DisposeCount);
    }

    [TestMethod]
    public async Task SelectionTabMustNotFallBackToInvokePattern()
    {
        var fixture = new Fixture(root);
        fixture.EditResponse = (call, response) => { if (call.Arguments[0] == "invoke") response["pattern"] = "InvokePattern"; };
        var round = await RunAsync(fixture);
        Assert.AreEqual("blocked", round.Status);
        Assert.IsFalse(fixture.Calls.Any(call => call.Arguments[0] == "focus"));
        Assert.AreEqual(0, round.States.Count);
    }

    [TestMethod]
    public async Task ExplicitDisclosureAndInvokeUseOnlyThePrescribedPatterns()
    {
        var fixture = new Fixture(root);
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"),
            new() { Id = "open", PageId = "basics", Name = "Expand", Kind = "expand-collapse", TargetId = "DensityCombo", ControlType = "ComboBox", ExpansionState = "Expanded" },
            new() { Id = "close", PageId = "basics", Name = "Collapse", Kind = "expand-collapse", TargetId = "DensityCombo", ControlType = "ComboBox", ExpansionState = "Collapsed" },
            new()
            {
                Id = "refresh", PageId = "basics", Name = "Invoke", Kind = "invoke", TargetId = "BasicIconButton", ControlType = "Button",
                Expectations = [Expect("BasicsActionStatus", "Name", "Preview refreshed")]
            }));
        Assert.AreEqual("completed", round.Status, round.Error);
        Assert.IsFalse(fixture.Expanded);
        Assert.AreEqual("Preview refreshed", fixture.BasicStatus);
    }

    [TestMethod]
    public async Task InvokeCannotToggleTheInMemorySwitchByFallback()
    {
        var fixture = new Fixture(root);
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"),
            new() { Id = "bad-invoke", PageId = "basics", Name = "Forbidden fallback", Kind = "invoke", TargetId = "PreviewGuidesToggle", ControlType = "Button" }));
        Assert.AreEqual("blocked", round.Status);
        Assert.IsFalse(fixture.Calls.Any(call => call.Arguments is ["invoke", "canonical-PreviewGuidesToggle"]));
        Assert.IsFalse(fixture.Guides);
    }

    [TestMethod]
    public async Task AlternateOwnedWindowIsNeverApprovedForThePresetRoute()
    {
        var fixture = new Fixture(root);
        fixture.AfterCommand = call =>
        {
            if (call.Arguments[0] != "expand-collapse") return;
            fixture.Windows.Visible.Add(new(999, fixture.Target.ProcessId, fixture.Target.WindowHandle));
            fixture.Windows.Signal("window", 999);
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"),
            new() { Id = "open", PageId = "basics", Name = "Expand", Kind = "expand-collapse", TargetId = "DensityCombo", ControlType = "ComboBox", ExpansionState = "Expanded" }));
        Assert.AreEqual("blocked", round.Status);
        Assert.IsTrue(round.ScopeEvents.Any(item => item.Kind == "window-boundary-changed"));
        Assert.IsFalse(round.ScopeEvents.Any(item => item.Kind == "popup-verified"));
        Assert.IsTrue(fixture.Calls.All(call => call.Target.WindowHandle == fixture.Target.WindowHandle));
    }

    [TestMethod]
    public async Task ExistingEvidenceDirectoryIsNeverOverwrittenOrUsedToLaunch()
    {
        var fixture = new Fixture(root);
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(Path.Combine(fixture.Output, "actions.json"), "original");
        await Assert.ThrowsExactlyAsync<IOException>(() => RunAsync(fixture));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(fixture.Output, "actions.json")));
        Assert.AreEqual(0, fixture.LaunchCount);
    }

    [TestMethod]
    public async Task APreexistingSingletonIsNeverClaimedOrDisposed()
    {
        var fixture = new Fixture(root) { FailLaunch = true };
        var round = await RunAsync(fixture);
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(0, fixture.AttachCount);
        Assert.AreEqual(0, fixture.DisposeCount);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Output, "actions.json")));
    }

    [TestMethod]
    public async Task WrongAttachmentStillClosesOnlyTheOwnedSession()
    {
        var fixture = new Fixture(root) { WrongAttachedProcess = true };
        var round = await RunAsync(fixture);
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(1, fixture.DisposeCount);
        Assert.IsTrue(fixture.MonitorStoppedBeforeDisposal);
        Assert.AreEqual(0, fixture.Calls.Count);
    }

    [TestMethod]
    public async Task ProcessNameCasingMustMatchTheSharedEvidenceContractBeforeRouteInput()
    {
        var fixture = new Fixture(root);
        var round = await RunAsync(fixture, fixture.Profile() with { ProcessName = fixture.Target.ProcessName.ToLowerInvariant() });
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(0, fixture.Calls.Count);
        Assert.AreEqual(1, fixture.DisposeCount);
    }

    [TestMethod]
    public async Task CancellationPreservesCompletedResponsesAndDisposesOwnedProcessAfterMonitor()
    {
        using var cancel = new CancellationTokenSource();
        var fixture = new Fixture(root);
        var countAtCancellation = 0;
        fixture.AfterCommand = call =>
        {
            if (call.Arguments[0] != "send-keys") return;
            countAtCancellation = fixture.Calls.Count;
            cancel.Cancel();
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"),
            Key("tab", "NavBasics", "TabItem", "Tab"), Key("space", "PreviewGuidesToggle", "Button", "Space")), cancel.Token);
        Assert.AreEqual("blocked", round.Status);
        Assert.AreEqual(countAtCancellation, fixture.Calls.Count);
        Assert.AreEqual("tab", round.Actions.Last().Step.Id);
        Assert.AreEqual(1, fixture.DisposeCount);
        Assert.IsTrue(fixture.MonitorStoppedBeforeDisposal);
        Assert.IsNotNull(round.ActionLogSha256);
        var input = round.Actions.Last().Commands.Single(command => command.Operation == "send-keys");
        Assert.AreEqual(input.ResponseSha256, AuditFiles.Hash(input.ResponseFile));
    }

    [TestMethod]
    public async Task TotalDurationStartsAfterStartupAndStopsFurtherInput()
    {
        var fixture = new Fixture(root) { LaunchDelay = TimeSpan.FromMilliseconds(1200) };
        var profile = fixture.Profile(Navigate("basics")) with { MaxDurationSeconds = 1, TimeoutSeconds = 3 };
        var completed = await RunAsync(fixture, profile);
        Assert.AreEqual("completed", completed.Status, completed.Error);

        var anotherRoot = Path.Combine(root, "bounded");
        Directory.CreateDirectory(anotherRoot);
        var bounded = new Fixture(anotherRoot);
        bounded.BeforeCommand = async (call, token) =>
        {
            if (call.Arguments[0] == "invoke") await Task.Delay(TimeSpan.FromSeconds(3), token);
        };
        var blocked = await RunAsync(bounded, bounded.Profile(Navigate("basics")) with { MaxDurationSeconds = 1, TimeoutSeconds = 3 });
        Assert.AreEqual("blocked", blocked.Status);
        StringAssert.Contains(blocked.Error, "total interaction duration");
        Assert.IsFalse(bounded.Calls.Any(call => call.Arguments[0] == "focus"));
        Assert.IsTrue(bounded.MonitorStoppedBeforeDisposal);
    }

    [TestMethod]
    public async Task AFiniteScrollPercentageUsesOnlyItsOwnPatternAndRecordsTheRealString()
    {
        var fixture = new Fixture(root);
        var scroll = new LoopStep
        {
            Id = "scroll", PageId = "scrolling", Name = "Explicit scroll", Kind = "scroll",
            TargetId = "ContentScroller", ControlType = "Pane", VerticalPercent = 100,
            Expectations = [Expect("ContentScroller", "ScrollVerticalPercent", 100)]
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("scrolling"), scroll));
        Assert.AreEqual("completed", round.Status, round.Error);
        Assert.AreEqual("100", round.Actions.Last().Checks.Single().Actual!.Value.GetString());
        Assert.IsTrue(fixture.Calls.Where(call => call.Arguments[0] == "scroll").All(call => call.Arguments[2] == "--vertical-percent"));
    }

    [TestMethod]
    public async Task PerCommandTimeoutIsExplicitAndDoesNotAttemptLaterPreparation()
    {
        var fixture = new Fixture(root);
        fixture.BeforeCommand = async (call, token) =>
        {
            if (call.Arguments[0] == "invoke") await Task.Delay(TimeSpan.FromSeconds(3), token);
        };
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics")));
        Assert.AreEqual("blocked", round.Status);
        StringAssert.Contains(round.Error, "command timeout");
        Assert.IsFalse(fixture.Calls.Any(call => call.Arguments[0] == "focus"));
        Assert.IsTrue(fixture.Calls.All(call => call.Timeout <= TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task SourceChangesBlockTheRoundWithoutDiscardingItsOriginalSourceOrActionRecord()
    {
        var fixture = new Fixture(root);
        var source = Path.Combine(fixture.Source, "MainWindow.xaml");
        var original = AuditFiles.Hash(source);
        var archive = Path.Combine(root, "parent-source-archive");
        _ = LoopSourceWorkspace.Copy(fixture.Project, archive);
        fixture.AfterCommand = call =>
        {
            if (call.Arguments[0] == "audit-snapshot")
                File.AppendAllText(source, Environment.NewLine + "<!-- unrelated external source change -->");
        };
        var round = await RunAsync(fixture);
        Assert.AreEqual("blocked", round.Status);
        StringAssert.Contains(round.Error, "Source files changed");
        Assert.AreEqual(original, round.Sources.Single(stamp => stamp.RelativePath == "MainWindow.xaml").Sha256);
        Assert.IsNull(round.SourceDirectory);
        Assert.AreEqual(original, AuditFiles.Hash(Path.Combine(archive, "MainWindow.xaml")));
        Assert.AreNotEqual(original, AuditFiles.Hash(source));
        Assert.AreEqual("captured", round.States.Single().Status);
        Assert.AreEqual(AuditFiles.Hash(Path.Combine(round.Directory, "actions.json")), round.ActionLogSha256);
        Assert.AreEqual(1, fixture.DisposeCount);
    }

    [TestMethod]
    [DataRow("Up", "up")]
    [DataRow("Down", "down")]
    [DataRow("Left", "left")]
    [DataRow("Right", "right")]
    [DataRow("Home", "home")]
    [DataRow("End", "end")]
    [DataRow("PageUp", "pageup")]
    [DataRow("PageDown", "pagedown")]
    public async Task AdditionalDeclaredNavigationKeysUseNativeNamedKeyGrammar(string requested, string canonical)
    {
        var fixture = new Fixture(root);
        var round = await RunAsync(fixture, fixture.Profile(Navigate("basics"), Key("key", "NavBasics", "TabItem", requested)));
        Assert.AreEqual("completed", round.Status, round.Error);
        var action = round.Actions.Last();
        Assert.AreEqual(requested, action.Step.Keys);
        Assert.IsFalse(action.Before!.Value.TryGetProperty("reviewedFlow", out _));
        var input = action.Commands.Single(command => command.Operation == "send-keys");
        CollectionAssert.AreEqual(new[] { "send-keys", canonical, "--via", "send-input" }, input.Arguments.ToArray());
        using var response = JsonDocument.Parse(File.ReadAllBytes(input.ResponseFile));
        Assert.AreEqual(canonical, response.RootElement.GetProperty("keys").GetString());
    }

}
