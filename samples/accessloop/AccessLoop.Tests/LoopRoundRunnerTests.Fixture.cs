using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessLoop.Tests;

public sealed partial class LoopRoundRunnerTests
{
    private sealed class Fixture : IAuditPlatform
    {
        public string Source { get; }
        public string Output { get; }
        public string Project { get; }
        public AuditTarget Target { get; private set; } = AuditFixture.Target with { ProcessName = "AccessLoop.StateDemo", RootRuntimeId = null };
        public NativeContext Native { get; } = new();
        public WindowSource Windows { get; }
        public List<Call> Calls { get; } = [];
        public List<LoopProgress> Progress { get; } = [];
        public string Page { get; set; } = "basics";
        public string? Focus { get; set; } = "NavBasics";
        public bool Guides { get; set; }
        public bool Expanded { get; set; }
        public bool Dialog { get; set; }
        public double Percent { get; set; }
        public string Selection { get; set; } = "DensityStandard";
        public string ToggleControlType { get; set; } = "Button";
        public string BasicStatus { get; set; } = "Preview not refreshed";
        public string ScrollStatus { get; set; } = "Layout not saved";
        public bool IgnoreTab { get; set; }
        public bool FailLaunch { get; set; }
        public bool WrongAttachedProcess { get; set; }
        public bool FailAxe { get; set; }
        public bool PartialSnapshot { get; set; }
        public bool TruncateSnapshot { get; set; }
        public bool NullName { get; set; } = true;
        public int LaunchCount { get; private set; }
        public string? LaunchedProject { get; private set; }
        public int AttachCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int AxeCount { get; private set; }
        public bool MonitorStoppedBeforeDisposal { get; private set; }
        public Action<JsonObject>? EditTree { get; set; }
        public Action<string, JsonObject>? EditProperties { get; set; }
        public Action<Call, JsonObject>? EditResponse { get; set; }
        public Action<Call>? AfterCommand { get; set; }
        public Action<JsonObject>? EditSnapshot { get; set; }
        public Action? DuringDispose { get; set; }
        public bool NonzeroInput { get; set; }
        public TimeSpan LaunchDelay { get; set; }
        public Func<Call, CancellationToken, Task>? BeforeCommand { get; set; }

        public Fixture(string root)
        {
            Source = Path.Combine(root, "source");
            Output = Path.Combine(root, "round");
            Directory.CreateDirectory(Source);
            Project = Path.Combine(Source, "AccessLoop.StateDemo.csproj");
            File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(Source, "MainWindow.xaml"),
                "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel>"
                + "<Button AutomationProperties.AutomationId=\"BasicIconButton\" AutomationProperties.Name=\"\" />"
                + "<Button AutomationProperties.AutomationId=\"ScrollBottomButton\" AutomationProperties.Name=\"\" />"
                + "</StackPanel></Window>");
            Windows = new(Target);
        }

        public LoopProfile Profile(params LoopStep[] steps) => new()
        {
            Name = "Preset fixture", Project = Project, ProcessName = Target.ProcessName,
            TimeoutSeconds = 1, MaxDurationSeconds = 30,
            Pages = [new("basics", "Basics", "NavBasics", "BasicsReady"),
                new("scrolling", "Scrolling", "NavScrolling", "ScrollingReady"),
                new("dialogs", "Dialogs", "NavDialogs", "DialogsReady")],
            Steps = steps.Length == 0 ? [Navigate("basics"), Scan("basics", "baseline", "BasicIconButton")] : steps,
            RepairTargets = []
        };

        public LoopProfile CompletePreset()
        {
            ToggleControlType = "CheckBox";
            LoopStep OnPage(string page, LoopStep step) => step with { PageId = page };
            LoopStep Scroll(string id, double percent) => new()
            {
                Id = id, PageId = "scrolling", Name = id, Kind = "scroll", TargetId = "ContentScroller",
                ControlType = "Pane", VerticalPercent = percent,
                Expectations = [Expect("ContentScroller", "ScrollVerticalPercent", percent)]
            };
            var steps = new LoopStep[]
            {
                Navigate("basics"),
                Scan("basics", "basics-default", "BasicIconButton", "PreviewGuidesToggle", "PreviewGuidesStatus", "DensityCombo"),
                Key("tab-toggle", "NavBasics", "TabItem", "Tab", Expect("PreviewGuidesToggle", "HasKeyboardFocus", "True")),
                Key("guides-on", "PreviewGuidesToggle", "CheckBox", "Space",
                    Expect("PreviewGuidesToggle", "ToggleState", "On"), Expect("PreviewGuidesStatus", "Name", "Preview guides on")),
                Scan("basics", "guides-enabled", "BasicIconButton", "PreviewGuidesToggle", "PreviewGuidesStatus"),
                Key("guides-off", "PreviewGuidesToggle", "CheckBox", "Space",
                    Expect("PreviewGuidesToggle", "ToggleState", "Off"), Expect("PreviewGuidesStatus", "Name", "Preview guides off")),
                Key("tab-density", "PreviewGuidesToggle", "CheckBox", "Tab", Expect("DensityCombo", "HasKeyboardFocus", "True")),
                Key("open-density", "DensityCombo", "ComboBox", "Alt+Down",
                    Expect("DensityCombo", "ExpandCollapseState", "Expanded"), Expect("DensityCombo", "SelectionAutomationIds", new[] { "DensityStandard" })),
                Scan("basics", "density-expanded", "BasicIconButton", "DensityCombo", "DensityStandard", "DensityComfortable", "DensityCompact"),
                Key("close-density", "DensityCombo", "ComboBox", "Escape",
                    Expect("DensityCombo", "ExpandCollapseState", "Collapsed"), Expect("DensityCombo", "SelectionAutomationIds", new[] { "DensityStandard" })),
                Key("tab-refresh", "DensityCombo", "ComboBox", "Tab", Expect("BasicIconButton", "HasKeyboardFocus", "True")),
                Key("reverse", "BasicIconButton", "Button", "Shift+Tab", Expect("DensityCombo", "HasKeyboardFocus", "True")),
                Key("return", "DensityCombo", "ComboBox", "Tab", Expect("BasicIconButton", "HasKeyboardFocus", "True")),
                Key("refresh", "BasicIconButton", "Button", "Enter", Expect("BasicsActionStatus", "Name", "Preview refreshed")),
                Navigate("scrolling"),
                Scroll("scroll-top", 0),
                Scan("scrolling", "top", "ContentScroller", "ScrollTopAnchor"),
                Scroll("scroll-middle", 50),
                Scan("scrolling", "middle", "ContentScroller"),
                Scroll("scroll-end", 100) with { Expectations = [Expect("ContentScroller", "ScrollVerticalPercent", 100), Expect("ScrollBottomAnchor", "IsOffscreen", "False")] },
                Scan("scrolling", "scroll-bottom", "ContentScroller", "ScrollBottomAnchor", "ScrollBottomButton"),
                OnPage("scrolling", Key("tab-body", "NavScrolling", "TabItem", "Tab", Expect("ContentScroller", "HasKeyboardFocus", "True"))),
                OnPage("scrolling", Key("tab-save", "ContentScroller", "Pane", "Tab", Expect("ScrollBottomButton", "HasKeyboardFocus", "True"))),
                OnPage("scrolling", Key("save", "ScrollBottomButton", "Button", "Enter", Expect("ScrollActionStatus", "Name", "Layout saved"))),
                Scroll("restore-scroll", 0),
                Navigate("dialogs"),
                Scan("dialogs", "dialog-entry", "OpenInfoDialog"),
                OnPage("dialogs", Key("tab-open", "NavDialogs", "TabItem", "Tab", Expect("OpenInfoDialog", "HasKeyboardFocus", "True"))),
                OnPage("dialogs", Key("open-dialog", "OpenInfoDialog", "Button", "Enter",
                    Expect("InfoDialog", "IsOffscreen", "False"), Expect("DialogIconButton", "HasKeyboardFocus", "True"))),
                Scan("dialogs", "dialog-open", "InfoDialog", "CloseInfoDialog", "DialogIconButton"),
                OnPage("dialogs", Key("tab-close", "DialogIconButton", "Button", "Tab", Expect("CloseInfoDialog", "HasKeyboardFocus", "True"))),
                OnPage("dialogs", Key("close-dialog", "CloseInfoDialog", "Button", "Escape", Expect("OpenInfoDialog", "HasKeyboardFocus", "True"))),
                Navigate("basics") with
                {
                    Id = "restore-basics", Expectations = [Expect("PreviewGuidesToggle", "ToggleState", "Off"),
                        Expect("DensityCombo", "SelectionAutomationIds", new[] { "DensityStandard" })]
                }
            };
            return Profile(steps) with
            {
                TimeoutSeconds = 2,
                RepairTargets = [new("BasicIconButton", "Refresh preview", "basics", "basics-default"),
                    new("ScrollBottomButton", "Save layout", "scrolling", "scroll-bottom")]
            };
        }

        public NativeLoopRoundRunner Runner(Action<LoopProgress>? progress = null) => new(
            "native-fixture", false, value => { Progress.Add(value); progress?.Invoke(value); },
            LaunchAsync, () => this, () => Native, () => Windows);

        private async Task<IAppSession> LaunchAsync(LoopProfile profile, string directory, CancellationToken token)
        {
            LaunchCount++;
            LaunchedProject = profile.Project;
            if (FailLaunch) throw new InvalidOperationException("Refused a pre-existing singleton process.");
            if (LaunchDelay > TimeSpan.Zero) await Task.Delay(LaunchDelay, token);
            Target = Target with { ProcessStartedAtUtc = DateTimeOffset.UtcNow };
            return new OwnedSession(this);
        }

        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("native-fixture");
        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token)
        {
            AttachCount++;
            if (options.ProcessId != Target.ProcessId) throw new InvalidOperationException("Wrong owned PID was attached.");
            return Task.FromResult(WrongAttachedProcess ? Target with { ProcessId = 9999 } : Target);
        }
        public void VerifyTarget(AuditTarget target)
        {
            if (target.ProcessId != Target.ProcessId || target.WindowHandle != Target.WindowHandle
                || target.ProcessStartedAtUtc != Target.ProcessStartedAtUtc || target.ProcessName != Target.ProcessName)
                throw new InvalidOperationException("The original owned target changed.");
        }

        public async Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            VerifyTarget(target);
            var call = new Call(arguments.ToArray(), target, timeout);
            Calls.Add(call);
            if (BeforeCommand is not null) await BeforeCommand(call, token);
            JsonObject response;
            var exit = 0;
            var stderr = "";
            switch (arguments[0])
            {
                case "inspect":
                    response = Tree();
                    break;
                case "get-property":
                    response = Properties(Id(arguments[1]));
                    break;
                case "get-focused":
                    response = new()
                    {
                        ["hasFocus"] = Focus is not null,
                        ["element"] = Focus is null ? null : new JsonObject
                        {
                            ["automationId"] = Focus, ["type"] = TypeOf(Focus), ["selector"] = Slug(Focus), ["windowHandle"] = target.WindowHandle
                        }
                    };
                    break;
                case "invoke":
                    var id = Id(arguments[1]);
                    if (id.StartsWith("Nav", StringComparison.Ordinal))
                    {
                        Page = id["Nav".Length..].ToLowerInvariant();
                        response = Action(arguments[1], "SelectionItemPattern");
                    }
                    else
                    {
                        Activate(id);
                        response = Action(arguments[1], "InvokePattern");
                    }
                    break;
                case "focus":
                    Focus = Id(arguments[1]);
                    response = Action(arguments[1]);
                    break;
                case "scroll":
                    Percent = double.Parse(arguments[3], CultureInfo.InvariantCulture);
                    response = Action(arguments[1]);
                    response["verticalPercent"] = Percent;
                    response["actualVerticalPercent"] = Percent;
                    break;
                case "expand-collapse":
                    Expanded = arguments[3] == "expanded";
                    response = Action(arguments[1], "ExpandCollapsePattern");
                    response["state"] = Expanded ? "Expanded" : "Collapsed";
                    break;
                case "send-keys":
                    if (arguments is not ["send-keys", _, "--via", "send-input"]
                        || arguments[1] != arguments[1].ToLowerInvariant())
                        throw new InvalidOperationException("Keyboard input requires one canonical key and send-input without corrective focus arguments.");
                    if (!NonzeroInput) SendKey(arguments[1]);
                    response = new()
                    {
                        ["keys"] = arguments[1], ["via"] = "send-input", ["actionCount"] = 1,
                        ["target"] = null, ["hwnd"] = target.WindowHandle, ["warnings"] = new JsonArray()
                    };
                    if (NonzeroInput)
                    {
                        exit = 1;
                        response = new() { ["status"] = "blocked" };
                        stderr = "{\"error\":\"input_delivery_failed\"}";
                    }
                    break;
                case "audit-snapshot":
                    response = Snapshot();
                    break;
                default:
                    throw new InvalidOperationException("Unexpected route command " + arguments[0]);
            }
            EditResponse?.Invoke(call, response);
            AfterCommand?.Invoke(call);
            return new CommandResult(exit, response.ToJsonString(Json.Options), stderr);
        }

        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AxeCount++;
            if (FailAxe) throw new InvalidOperationException("Axe output is unavailable.");
            var findings = new List<AccessibilityFinding>();
            foreach (var id in new[] { "BasicIconButton", "ScrollBottomButton" })
            {
                var node = Find(Tree(), id);
                if (node is null || node["isOffscreen"]!.GetValue<bool>() || !NullName) continue;
                findings.Add(new("NameNotNull", "Button has no accessible name.", "Provide an accessible name.", "WCAG",
                    new Dictionary<string, string>
                    {
                        ["AutomationId"] = id, ["ControlType"] = "Button", ["RuntimeId"] = Runtime(id),
                        ["ProcessId"] = target.ProcessId.ToString(CultureInfo.InvariantCulture), ["IsPassword"] = "False"
                    }, ["Invoke"]));
            }
            return Task.FromResult(AccessibilityScan.Create(target.ProcessId, target.WindowHandle, "2.4.2", [new(findings.Count, findings)]));
        }

        private void SendKey(string keys)
        {
            if (keys == "tab" && !IgnoreTab)
                Focus = Focus switch
                {
                    "NavBasics" => "PreviewGuidesToggle", "PreviewGuidesToggle" => "DensityCombo", "DensityCombo" => "BasicIconButton",
                    "NavScrolling" => "ContentScroller", "ContentScroller" => "ScrollBottomButton",
                    "NavDialogs" => "OpenInfoDialog", "DialogIconButton" => "CloseInfoDialog", _ => Focus
                };
            if (keys == "shift+tab" && !IgnoreTab && Focus == "BasicIconButton") Focus = "DensityCombo";
            if (keys == "space" && Focus == "PreviewGuidesToggle") Guides = !Guides;
            if (keys == "alt+down" && Focus == "DensityCombo") Expanded = true;
            if (keys == "escape" && Focus == "DensityCombo") Expanded = false;
            if (keys == "escape" && Focus == "CloseInfoDialog") { Dialog = false; Focus = "OpenInfoDialog"; }
            if (keys == "enter") Activate(Focus!);
            if (keys == "down" && Focus == "DensityCombo") Selection = "DensityComfortable";
            if (keys == "up" && Focus == "DensityCombo") Selection = "DensityStandard";
        }
        private void Activate(string id)
        {
            if (id == "BasicIconButton") BasicStatus = "Preview refreshed";
            if (id == "ScrollBottomButton") ScrollStatus = "Layout saved";
            if (id == "OpenInfoDialog") { Dialog = true; Focus = "DialogIconButton"; }
            if (id == "CloseInfoDialog") { Dialog = false; Focus = "OpenInfoDialog"; }
        }

        private JsonObject Tree()
        {
            var root = Element("MainWindow", "Window");
            var children = root["children"]!.AsArray();
            var navigation = Element("MainNavigation", "Tab");
            foreach (var id in new[] { "NavBasics", "NavScrolling", "NavDialogs" })
                navigation["children"]!.AsArray().Add(Element(id, "TabItem"));
            children.Add(navigation);
            children.Add(Element(Page switch { "basics" => "BasicsReady", "scrolling" => "ScrollingReady", _ => "DialogsReady" }, "Text"));
            if (Page == "basics")
            {
                foreach (var id in new[] { "BasicIconButton", "PreviewGuidesToggle", "PreviewGuidesStatus", "DensityCombo", "BasicsActionStatus" })
                    children.Add(Element(id, TypeOf(id)));
                if (Expanded)
                    foreach (var id in new[] { "DensityStandard", "DensityComfortable", "DensityCompact" })
                        children.Add(Element(id, "ListItem"));
            }
            else if (Page == "scrolling")
            {
                var scroller = Element("ContentScroller", "Pane");
                scroller["isScrollable"] = true;
                foreach (var id in new[] { "ScrollTopAnchor", "ScrollBottomAnchor", "ScrollBottomButton", "ScrollActionStatus" })
                {
                    var element = Element(id, TypeOf(id));
                    element["isOffscreen"] = id == "ScrollTopAnchor" ? Percent > 0.1 : Percent < 99.9;
                    scroller["children"]!.AsArray().Add(element);
                }
                children.Add(scroller);
            }
            else
            {
                children.Add(Element("OpenInfoDialog", "Button"));
                if (Dialog)
                {
                    var dialog = Element("InfoDialog", "Pane");
                    dialog["children"]!.AsArray().Add(Element("CloseInfoDialog", "Button"));
                    dialog["children"]!.AsArray().Add(Element("DialogIconButton", "Button"));
                    children.Add(dialog);
                }
            }
            var response = new JsonObject
            {
                ["interactive"] = false, ["hideDisabled"] = false, ["hideOffscreen"] = false,
                ["windows"] = new JsonArray(new JsonObject { ["hwnd"] = Target.WindowHandle, ["processId"] = Target.ProcessId, ["elements"] = new JsonArray(root) })
            };
            EditTree?.Invoke(response);
            return response;
        }
        private JsonObject Properties(string id)
        {
            var node = Find(Tree(), id) ?? throw new InvalidOperationException("Missing fake control " + id);
            var properties = new JsonObject
            {
                ["AutomationId"] = id, ["ControlType"] = node["type"]!.DeepClone(), ["ClassName"] = node["type"]!.DeepClone(),
                ["Name"] = NameOf(id), ["IsEnabled"] = "True", ["IsOffscreen"] = node["isOffscreen"]!.GetValue<bool>().ToString(),
                ["IsPassword"] = "False", ["HasKeyboardFocus"] = (Focus == id).ToString()
            };
            if (id.StartsWith("Nav", StringComparison.Ordinal)) properties["IsSelected"] = (id["Nav".Length..].ToLowerInvariant() == Page).ToString();
            if (id == "PreviewGuidesToggle") properties["ToggleState"] = Guides ? "On" : "Off";
            if (id == "DensityCombo")
            {
                properties["ExpandCollapseState"] = Expanded ? "Expanded" : "Collapsed";
                properties["SelectionAutomationIds"] = new JsonArray(Selection);
            }
            if (id == "ContentScroller")
            {
                properties["ScrollVerticalPercent"] = Percent.ToString("R", CultureInfo.InvariantCulture);
                properties["ScrollVerticalViewSize"] = "55.55555555555556";
                properties["HorizontallyScrollable"] = "False";
                properties["VerticallyScrollable"] = "True";
            }
            EditProperties?.Invoke(id, properties);
            return new() { ["elementId"] = Slug(id), ["hwnd"] = Target.WindowHandle, ["properties"] = properties };
        }
        private JsonObject Snapshot()
        {
            var nodes = new JsonArray();
            Add(Tree()["windows"]![0]!["elements"]![0]!.AsObject(), null, 0);
            var snapshot = new JsonObject
            {
                ["schemaVersion"] = 1, ["capturedAtUtc"] = JsonSerializer.SerializeToNode(DateTimeOffset.UtcNow),
                ["processId"] = Target.ProcessId, ["windowHandle"] = Target.WindowHandle, ["rootRuntimeId"] = Runtime("MainWindow"),
                ["title"] = "Loop fixture", ["status"] = PartialSnapshot || TruncateSnapshot ? "partial" : "captured",
                ["maxDepth"] = 20, ["maxElements"] = 1000, ["depthLimitReached"] = TruncateSnapshot, ["elementLimitReached"] = false,
                ["nodes"] = nodes, ["issues"] = new JsonArray()
            };
            if (PartialSnapshot)
                snapshot["issues"]!.AsArray().Add(new JsonObject { ["code"] = "privacy_withheld", ["message"] = "Auxiliary provider text withheld." });
            EditSnapshot?.Invoke(snapshot);
            return snapshot;

            void Add(JsonObject node, string? parentId, int depth)
            {
                var id = node["automationId"]!.GetValue<string>();
                var captureId = "n" + nodes.Count;
                nodes.Add(new JsonObject
                {
                    ["id"] = captureId, ["parentId"] = parentId, ["depth"] = depth, ["runtimeId"] = Runtime(id),
                    ["automationId"] = id, ["name"] = NameOf(id), ["controlType"] = node["type"]!.DeepClone(),
                    ["frameworkId"] = "XAML", ["className"] = node["type"]!.DeepClone(),
                    ["isPassword"] = false, ["isEnabled"] = true, ["isKeyboardFocusable"] = true,
                    ["hasKeyboardFocus"] = Focus == id, ["isOffscreen"] = node["isOffscreen"]!.DeepClone(),
                    ["bounds"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 200, ["height"] = 40 },
                    ["patterns"] = new JsonArray(), ["textRuns"] = new JsonArray(), ["issues"] = new JsonArray()
                });
                foreach (var child in node["children"]!.AsArray())
                    Add(child!.AsObject(), captureId, depth + 1);
            }
        }
        private string? NameOf(string id) => id switch
        {
            "BasicIconButton" => NullName ? null : "Refresh preview",
            "ScrollBottomButton" => NullName ? null : "Save layout",
            "PreviewGuidesStatus" => Guides ? "Preview guides on" : "Preview guides off",
            "BasicsActionStatus" => BasicStatus, "ScrollActionStatus" => ScrollStatus,
            _ => id
        };
        private string TypeOf(string id) => id switch
        {
            "NavBasics" or "NavScrolling" or "NavDialogs" => "TabItem",
            "PreviewGuidesToggle" => ToggleControlType,
            "DensityCombo" => "ComboBox", "ContentScroller" or "InfoDialog" => "Pane",
            "DensityStandard" or "DensityComfortable" or "DensityCompact" => "ListItem",
            _ => id.Contains("Button", StringComparison.Ordinal) || id is "PreviewGuidesToggle" or "OpenInfoDialog" or "CloseInfoDialog" ? "Button" : "Text"
        };
        private static string Runtime(string id) => id switch { "BasicIconButton" => "[1,2,3]", "ScrollBottomButton" => "[1,2,4]", _ => "runtime-" + id };
        private static string Slug(string id) => "canonical-" + id;
        private static string Id(string selector) => selector.StartsWith("canonical-", StringComparison.Ordinal) ? selector["canonical-".Length..] : selector;
        private JsonObject Action(string selector, string? pattern = null) => new() { ["elementId"] = selector, ["hwnd"] = Target.WindowHandle, ["pattern"] = pattern };
        private static JsonObject Element(string id, string type) => new()
        {
            ["automationId"] = id, ["selector"] = id, ["type"] = type, ["isOffscreen"] = false,
            ["isEnabled"] = true, ["hasMoreChildren"] = false, ["children"] = new JsonArray()
        };
        public static JsonObject? Find(JsonNode? node, string id) => NativeSurveySessionTestFixture.Find(node, id);

        public sealed record Call(string[] Arguments, AuditTarget Target, TimeSpan Timeout)
        {
            public bool Input => Arguments[0] is "invoke" or "focus" or "send-keys" or "scroll" or "expand-collapse";
        }
        public sealed class NativeContext : ISurveyNativeContext
        {
            public SurveyNativeWindow Window { get; set; } = new(true, false, true, "Default", "Default", "Default", 1024, 768, 96);
            public SurveyNativeWindow ReadWindow(AuditTarget target) => Window;
            public SurveyContext ReadContext(AuditTarget target, SurveyNativeWindow window) => throw new NotSupportedException();
        }
        public sealed class WindowSource(AuditTarget target) : IExploreWindowSource
        {
            private Action<ExploreWindowSignal>? changed;
            public bool Disposed { get; private set; }
            public long Foreground { get; set; } = target.WindowHandle;
            public List<ExploreScopeWindow> Visible { get; } = [new(target.WindowHandle, target.ProcessId, 0)];
            public ExploreWindowBoundary Read(AuditTarget target) => new(Foreground, Visible.ToArray());
            public bool TryForeground(AuditTarget target) { Foreground = target.WindowHandle; return true; }
            public void Start(AuditTarget target, Action<ExploreWindowSignal> changed) => this.changed = changed;
            public void Signal(string kind = "tick", long hwnd = 0) => changed?.Invoke(new(kind, hwnd));
            public void Dispose() => Disposed = true;
        }
        private sealed class OwnedSession(Fixture fixture) : IAppSession
        {
            public int ProcessId => fixture.Target.ProcessId;
            public Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Route UI must go through the guarded audit platform.");
            public ValueTask DisposeAsync()
            {
                fixture.DisposeCount++;
                fixture.MonitorStoppedBeforeDisposal = fixture.Windows.Disposed;
                fixture.DuringDispose?.Invoke();
                return ValueTask.CompletedTask;
            }
        }
    }
}
