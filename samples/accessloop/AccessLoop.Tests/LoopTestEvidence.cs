using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AccessLoop.Tests;

internal sealed class LoopTestEvidence : IDisposable
{
    private readonly AuditTestFixture audit;
    private int sequence;
    public AuditTestFixture Audit => audit;
    public LoopReport Report { get; set; }
    public string Root => Path.Combine(audit.Root, "loop #&%' run");

    public LoopTestEvidence()
    {
        audit = new();
        Directory.CreateDirectory(Root);
        var project = WriteSources(Path.Combine(Root, "workspace"), []);
        var profile = CreateProfile(project);
        Report = new()
        {
            RunId = "loop-fixture-run", Name = "Three-page closed loop", Directory = Root,
            Profile = profile, PlanSha256 = LoopInput.PlanHash(profile),
            Tools = new(audit.Report.WinappVersion, new string('B', 64), new string('C', 64)),
            StartedAtUtc = AuditTestFixture.Time, FinishedAtUtc = AuditTestFixture.Time.AddSeconds(1),
            Status = "partial", VerificationStatus = "failed", CaptureStatus = "partial"
        };
        AuditFiles.WriteNewBytes(Path.Combine(Root, "plan.json"), LoopInput.PlanBytes(profile));
    }

    public void Dispose() => audit.Dispose();

    public static LoopProfile CreateProfile(string project) => new()
    {
        Name = "Reviewed three-page route", Project = project, ProcessName = "Demo",
        Pages =
        [
            new("basics", "Basics", "NavBasics", "BasicsReady"),
            new("scroll", "Layout", "NavScroll", "ScrollReady"),
            new("settings", "Settings", "NavSettings", "SettingsReady")
        ],
        Steps =
        [
            new() { Id = "enter-basics", PageId = "basics", Name = "Enter Basics", Kind = "navigate", TargetId = "NavBasics", ControlType = "TabItem" },
            new() { Id = "tab-to-icon", PageId = "basics", Name = "Tab to preview", Kind = "key", TargetId = "NavBasics",
                ControlType = "TabItem", Keys = "Tab",
                Expectations = [new("BasicIconButton", "HasKeyboardFocus", JsonSerializer.SerializeToElement("True"))] },
            new() { Id = "basic-base", PageId = "basics", Name = "Preview baseline", Kind = "scan", RequiredIds = ["BasicIconButton"] },
            new() { Id = "open-mode", PageId = "basics", Name = "Expand display modes", Kind = "expand-collapse",
                TargetId = "DisplayMode", ControlType = "ComboBox", ExpansionState = "expanded" },
            new() { Id = "basic-expanded", PageId = "basics", Name = "Expanded display modes", Kind = "scan",
                RequiredIds = ["BasicIconButton", "DisplayMode"] },
            new() { Id = "enter-scroll", PageId = "scroll", Name = "Enter Layout", Kind = "navigate", TargetId = "NavScroll", ControlType = "TabItem" },
            new() { Id = "scroll-end", PageId = "scroll", Name = "Scroll to layout action", Kind = "scroll",
                TargetId = "LayoutScroller", ControlType = "Pane", VerticalPercent = 100 },
            new() { Id = "scroll-bottom", PageId = "scroll", Name = "Bottom of layout", Kind = "scan", RequiredIds = ["ScrollBottomButton"] },
            new() { Id = "enter-settings", PageId = "settings", Name = "Enter Settings", Kind = "navigate", TargetId = "NavSettings", ControlType = "TabItem" },
            new() { Id = "settings-base", PageId = "settings", Name = "Settings baseline", Kind = "scan", RequiredIds = ["SettingsToggle"] }
        ],
        RepairTargets =
        [
            new("BasicIconButton", "Refresh preview", "basics", "basic-base"),
            new("ScrollBottomButton", "Save layout", "scroll", "scroll-bottom")
        ]
    };

    public static string WriteSources(string directory, IReadOnlyCollection<string> fixedTargets)
    {
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Demo.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "MainWindow.xaml"),
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
            + "<Button AutomationProperties.AutomationId=\"BasicIconButton\" AutomationProperties.Name=\""
            + (fixedTargets.Contains("BasicIconButton") ? "Refresh preview" : "") + "\" />"
            + "<Button AutomationProperties.AutomationId=\"ScrollBottomButton\" AutomationProperties.Name=\""
            + (fixedTargets.Contains("ScrollBottomButton") ? "Save layout" : "") + "\" /></Page>");
        return project;
    }

    public LoopRoundResult AddRound(string id, params string[] fixedTargets)
    {
        var archive = Path.Combine(Root, "sources", id);
        _ = WriteSources(archive, fixedTargets);
        var directory = Path.Combine(Root, "rounds", id);
        var round = BuildRound(Report.Profile, id, directory, archive);
        Report.Rounds.Add(round);
        Report.FinishedAtUtc = round.FinishedAtUtc!.Value.AddSeconds(1);
        if (id == "reproduction") Report.ReproductionStatus = "passed";
        if (fixedTargets.Length == Report.Profile.RepairTargets.Count) Report.VerificationStatus = "passed";
        return round;
    }

    // Creates test artifacts only; controller tests retain ownership of the archive and report.
    public LoopRoundResult BuildRound(
        LoopProfile profile, string id, string directory, string sourceDirectory, DateTimeOffset? startedAtUtc = null)
    {
        LoopInput.Validate(profile);
        var number = sequence++;
        var start = startedAtUtc ?? AuditTestFixture.Time.AddSeconds(number * 100 + 1);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        AuditFiles.CreateNewDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "commands"));
        var archivedProject = AuditFiles.ResolveArtifact(sourceDirectory, Path.GetFileName(profile.Project));
        var names = profile.RepairTargets.ToDictionary(repair => repair.AutomationId,
            repair => SourceName(archivedProject, repair.AutomationId), StringComparer.Ordinal);
        var target = audit.Report.Target with
        {
            ProcessId = 42 + number, WindowHandle = 73 + number, ProcessName = profile.ProcessName,
            ProcessStartedAtUtc = start.AddSeconds(-1)
        };
        var round = new LoopRoundResult
        {
            Id = id, Directory = directory, SourceDirectory = sourceDirectory,
            StartedAtUtc = start, Status = "completed", Target = target,
            Sources = JourneyInput.CaptureSources(archivedProject)
        };
        foreach (var (step, index) in profile.Steps.Select((step, index) => (step, index)))
        {
            var actionStart = start.AddSeconds(index * 5);
            var action = new LoopActionResult
            {
                Step = step, StartedAtUtc = actionStart, FinishedAtUtc = actionStart.AddSeconds(4), Status = "completed",
                Before = Properties(step.TargetId ?? profile.Pages.Single(page => page.Id == step.PageId).ReadyId,
                    step.ControlType ?? "Text", "Ready"),
                After = Properties(step.TargetId ?? profile.Pages.Single(page => page.Id == step.PageId).ReadyId,
                    step.ControlType ?? "Text", "Ready")
            };
            foreach (var expectation in step.Expectations)
                action.Checks.Add(new() { TargetId = expectation.TargetId, Property = expectation.Property,
                    Expected = expectation.Expected.Clone(), Actual = expectation.Expected.Clone(), Passed = true });
            var responseFile = Path.Combine(directory, "commands", $"{index:D3}-response.json");
            var errorFile = Path.Combine(directory, "commands", $"{index:D3}-error.txt");
            if (step.Kind == "key")
                AuditFiles.WriteNew(responseFile, new JsonObject
                {
                    ["keys"] = step.Keys!.ToLowerInvariant(), ["via"] = "send-input", ["actionCount"] = 1,
                    ["target"] = null, ["hwnd"] = target.WindowHandle, ["warnings"] = new JsonArray()
                });
            else
                AuditFiles.WriteNew(responseFile, action.After);
            AuditFiles.WriteNewBytes(errorFile, []);
            var operation = step.Kind switch
            {
                "key" => "send-keys", "navigate" or "invoke" => "invoke", "scroll" => "scroll",
                "expand-collapse" => "expand-collapse", _ => "get-property"
            };
            action.Commands.Add(new(operation,
                step.Kind == "key" ? ["send-keys", step.Keys!.ToLowerInvariant(), "--via", "send-input"] : [operation, "id:" + (step.TargetId ?? "Ready")],
                responseFile, errorFile, AuditFiles.Hash(responseFile), AuditFiles.Hash(errorFile)));
            round.Actions.Add(action);
            if (step.Kind != "scan") continue;
            var folder = Path.Combine(directory, "states", step.Id + " #&%");
            Directory.CreateDirectory(Path.Combine(folder, "evidence"));
            var required = step.RequiredIds.Append(profile.Pages.Single(page => page.Id == step.PageId).ReadyId)
                .Distinct(StringComparer.Ordinal).ToArray();
            var nodes = new List<object>
            {
                Node("root", "MainWindow", "Window", "Demo", null)
            };
            var before = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var after = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var failures = new List<AuditCheckResult>();
            var native = new List<AccessibilityFinding>();
            foreach (var control in required)
            {
                var repair = profile.RepairTargets.SingleOrDefault(item => item.AutomationId == control);
                var type = repair is not null ? "Button" : control == "DisplayMode" ? "ComboBox" : control == "SettingsToggle" ? "CheckBox" : "Text";
                var name = repair is null ? control : names[control];
                var nodeId = "node-" + control;
                nodes.Add(Node(nodeId, control, type, name, "root"));
                before.Add(control, Properties(control, type, name));
                after.Add(control, Properties(control, type, name));
                if (repair is not null && string.IsNullOrWhiteSpace(name))
                {
                    failures.Add(Failure(nodeId));
                    native.Add(NativeFailure(control));
                }
            }
            var snapshot = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, processId = target.ProcessId, windowHandle = target.WindowHandle,
                capturedAtUtc = actionStart.AddSeconds(1), rootRuntimeId = target.RootRuntimeId, title = "Demo", status = "captured",
                maxDepth = 20, maxElements = 1000, depthLimitReached = false, elementLimitReached = false,
                nodes, issues = Array.Empty<string>()
            }, Json.Options);
            var scan = audit.Report with
            {
                RunId = id + "-" + step.Id, Target = target, StartedAtUtc = actionStart.AddSeconds(1),
                FinishedAtUtc = actionStart.AddSeconds(3), Mode = "snapshot", Snapshot = snapshot, Observation = null,
                Axe = AccessibilityScan.Create(target.ProcessId, target.WindowHandle, "2.4.2", [new(native.Count, native)]),
                Results =
                [
                    .. failures,
                    new() { CheckId = "capture.tree", Status = "observed", Summary = "Required controls captured.", Evidence = [AuditTestFixture.SnapshotPath] },
                    new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Text metadata is partial.", Evidence = [AuditTestFixture.SnapshotPath] },
                    new() { CheckId = "keyboard.path", Status = "not-tested", Summary = "Passive snapshot; route input is recorded in actions.json." }
                ]
            };
            var state = new LoopStateResult
            {
                StepId = step.Id, PageId = step.PageId, Name = step.Name, Directory = folder,
                Status = "captured", StartedAtUtc = actionStart.AddMilliseconds(100),
                FinishedAtUtc = actionStart.AddMilliseconds(3900), BeforeControls = before, AfterControls = after
            };
            SaveScan(state, scan, initial: true);
            round.States.Add(state);
        }
        round.FinishedAtUtc = round.Actions[^1].FinishedAtUtc!.Value.AddMilliseconds(100);
        SaveActions(round, initial: true);
        return round;
    }

    private static string SourceName(string project, string id)
    {
        var candidate = SourceRepair.Locate(project, id).Single();
        var element = XDocument.Load(candidate.FullPath).Descendants()
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);
        return (string?)element.Attribute("AutomationProperties.Name") ?? "";
    }

    private static object Node(string nodeId, string automationId, string type, string name, string? parent) => new
    {
        id = nodeId, parentId = parent, depth = parent is null ? 0 : 1,
        runtimeId = parent is null ? "runtime-root" : "runtime-" + automationId,
        automationId, controlType = type, name, frameworkId = "XAML", className = type,
        isOffscreen = false, isEnabled = true, isPassword = false, isKeyboardFocusable = true, hasKeyboardFocus = false,
        bounds = new { left = 125, top = 100, width = 90, height = 32 },
        patterns = new[] { "Invoke" }, textRuns = Array.Empty<object>(), issues = Array.Empty<string>()
    };

    public static JsonElement Properties(string id, string type, string name) => JsonSerializer.SerializeToElement(new
    {
        elementId = "id:" + id,
        properties = new Dictionary<string, object>
        {
            ["AutomationId"] = id, ["ControlType"] = type, ["Name"] = name, ["RuntimeId"] = "runtime-" + id,
            ["HasKeyboardFocus"] = "True", ["IsEnabled"] = "True", ["IsPassword"] = "False", ["IsOffscreen"] = "False"
        }
    }, Json.Options);

    public static AuditCheckResult Failure(string nodeId) => new()
    {
        CheckId = "axe.scan", Status = "rule-failure", SourceRuleId = "NameNotNull",
        ElementId = nodeId, ElementName = "", Summary = "Accessible name is missing.",
        Evidence = [AuditTestFixture.SnapshotPath, AuditTestFixture.AxePath]
    };

    public static AccessibilityFinding NativeFailure(string id, string? runtime = null) => new(
        "NameNotNull", "Accessible name is missing.", "Supply an accessible name.", "WCAG",
        new Dictionary<string, string> { ["AutomationId"] = id, ["ControlType"] = "Button", ["RuntimeId"] = runtime ?? "runtime-" + id }, ["Invoke"]);

    public void SaveScan(LoopStateResult state, AuditReport scan, bool initial = false)
    {
        var folder = state.Directory;
        File.WriteAllBytes(Path.Combine(folder, AuditTestFixture.SnapshotPath), JsonSerializer.SerializeToUtf8Bytes(scan.Snapshot, Json.Options));
        File.WriteAllBytes(Path.Combine(folder, AuditTestFixture.AxePath), JsonSerializer.SerializeToUtf8Bytes(scan.Axe, Json.Options));
        if (initial) File.Copy(Path.Combine(audit.Source, AuditTestFixture.ScreenshotPath), Path.Combine(folder, AuditTestFixture.ScreenshotPath));
        scan = scan with
        {
            Artifacts =
            [
                AuditFiles.Describe(folder, AuditTestFixture.SnapshotPath, "application/json", "WinAppCLI"),
                AuditFiles.Describe(folder, AuditTestFixture.AxePath, "application/json", "Axe.Windows"),
                AuditFiles.Describe(folder, AuditTestFixture.ScreenshotPath, "image/png", "WinAppCLI")
            ]
        };
        var scanPath = Path.Combine(folder, "scan.json");
        File.WriteAllBytes(scanPath, JsonSerializer.SerializeToUtf8Bytes(scan, Json.Options));
        var htmlPath = Path.Combine(folder, "report.html");
        if (!initial) File.Delete(htmlPath);
        AuditReportRenderer.Write(htmlPath, scan);
        state.ScanSha256 = AuditFiles.Hash(scanPath);
        state.CaptureStatus = scan.Status;
        state.RuleFailureOccurrences = scan.RuleFailureCount;
    }

    public static void SaveActions(LoopRoundResult round, bool initial = false)
    {
        var path = Path.Combine(round.Directory, "actions.json");
        if (initial) AuditFiles.WriteNew(path, round.Actions);
        else File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(round.Actions, Json.Options));
        round.ActionLogSha256 = AuditFiles.Hash(path);
    }

    public void SetProfile(LoopProfile profile)
    {
        Report = Report with { Profile = profile, PlanSha256 = LoopInput.PlanHash(profile) };
        File.WriteAllBytes(Path.Combine(Root, "plan.json"), LoopInput.PlanBytes(profile));
        foreach (var round in Report.Rounds)
        {
            for (var index = 0; index < round.Actions.Count; index++)
                round.Actions[index] = round.Actions[index] with { Step = profile.Steps[index] };
            SaveActions(round);
        }
    }

    public LoopRepairResult AddRepair(string id, string afterId, string source = "reviewed-file")
    {
        var afterIndex = Report.Rounds.FindIndex(round => round.Id == afterId);
        var before = Report.Rounds[afterIndex - 1];
        var target = Report.Profile.RepairTargets.Single(target => target.AutomationId == id);
        var folder = Path.Combine(Root, "repairs", id);
        Directory.CreateDirectory(folder);
        var proposal = new RepairProposal("MainWindow.xaml", before.Sources.Single(item => item.RelativePath == "MainWindow.xaml").Sha256,
            id, target.ExpectedName, "Give the static icon button its reviewed accessible name.");
        AuditFiles.WriteNew(Path.Combine(folder, "proposal.json"), proposal);
        var diff = Path.Combine(folder, "repair.diff");
        File.WriteAllText(diff, $"--- MainWindow.xaml\n+++ MainWindow.xaml\n+ AutomationProperties.Name=\"{target.ExpectedName}\"\n");
        if (source == "copilot")
        {
            var response = JsonSerializer.Serialize(proposal, Json.Options);
            File.WriteAllText(Path.Combine(folder, "advisor-prompt.txt"), "Propose only the reviewed Name.");
            File.WriteAllText(Path.Combine(folder, "advisor-response.txt"), response);
            File.WriteAllText(Path.Combine(folder, "advisor-events.jsonl"),
                JsonSerializer.Serialize(new { type = "assistant.message", data = new { content = response } })
                + "\n" + JsonSerializer.Serialize(new { type = "result", exitCode = 0 }) + "\n");
        }
        var repair = new LoopRepairResult
        {
            AutomationId = id, ExpectedName = target.ExpectedName, Directory = folder, Status = "fixed",
            ProposalSource = source, Proposal = proposal, DiffFile = diff, VerificationRoundId = afterId
        };
        Report.Repairs.Add(repair);
        return repair;
    }
}
