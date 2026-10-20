using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessLoop.Tests;

internal sealed class NativeExploreSessionTestFixture : IAuditPlatform
{
    public static ExploreProfile Profile => new()
    {
        Name = "Three pages", ProcessName = "AccessLoop.StateDemo", NavigationRootId = "MainNavigation",
        InitialPageId = "basics", TimeoutSeconds = 5, MaxStates = 16,
        Pages =
        [
            new()
            {
                Id = "basics", Name = "Basics", NavigationId = "NavBasics", ReadyId = "BasicsReady",
                Dropdowns = [new() { ControlId = "DensityCombo", ItemIds = ["DensityStandard", "DensityComfortable", "DensityCompact"] }]
            },
            new()
            {
                Id = "scrolling", Name = "Scrolling", NavigationId = "NavScrolling", ReadyId = "ScrollingReady",
                Scroll = new() { ContainerId = "ContentScroller", TopAnchorId = "ScrollTopAnchor", BottomAnchorId = "ScrollBottomAnchor" }
            },
            new()
            {
                Id = "dialogs", Name = "Dialogs", NavigationId = "NavDialogs", ReadyId = "DialogsReady",
                Dialogs = [new() { OpenerId = "OpenInfoDialog", RootId = "InfoDialog", DismissId = "CloseInfoDialog" }]
            }
        ],
        ScopeProbe = new("dialogs", "OpenScopeProbe", "AccessLoop scope probe")
    };

    public AuditTarget Target { get; } = AuditFixture.Target with { ProcessName = "AccessLoop.StateDemo" };
    public NativeFake Native { get; } = new();
    public WindowFake Windows { get; }
    public List<Call> Calls { get; } = [];
    public List<double> ScrollRequests { get; } = [];
    public List<long> AxeWindows { get; } = [];
    public string Page { get; set; } = "basics";
    public string Selected { get; set; } = "DensityStandard";
    public bool Expanded { get; set; }
    public bool Dialog { get; set; }
    public double Percent { get; set; }
    public double ViewSize { get; set; } = 50;
    public string? Focused { get; set; } = "NavBasics";
    public string? PostDialogFocus { get; set; } = "OpenInfoDialog";
    public bool ExternalCombo { get; set; }
    public bool IgnoreProbe { get; set; }
    public bool IgnoreScroll { get; set; }
    public bool HideBottom { get; set; }
    public bool IdentityValid { get; set; } = true;
    public bool AxeFailure { get; set; }
    public bool PartialSnapshot { get; set; }
    public bool TabNavigation { get; set; }
    public Action<JsonObject>? EditTree { get; set; }
    public Action<string, JsonObject>? EditProperties { get; set; }
    public Action<Call, JsonObject>? EditResponse { get; set; }
    public Action<Call>? AfterCommand { get; set; }

    public NativeExploreSessionTestFixture() => Windows = new(Target);
    public NativeExploreSession Session(bool screenshot = false) => new(this, Target, "native-fixture", screenshot, Native, Windows);
    public ExploreReport Report(ExploreProfile? profile = null, bool owned = true) => new()
    {
        RunId = "explore-fixture", Name = "Explore fixture", Target = Target, ProfileSha256 = new('A', 64),
        Tools = new("native-fixture", new('B', 64), new('C', 64)), Profile = profile ?? Profile,
        OwnedApplication = owned, Screenshot = false
    };
    public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("native-fixture");
    public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) => throw new InvalidOperationException("Reattachment is forbidden.");
    public void VerifyTarget(AuditTarget candidate)
    {
        if (!IdentityValid || candidate.ProcessId != Target.ProcessId || candidate.ProcessStartedAtUtc != Target.ProcessStartedAtUtc
            || candidate.ProcessName != Target.ProcessName || !Windows.Visible.Any(window => window.Handle == candidate.WindowHandle))
            throw new InvalidOperationException("Original process/window identity changed.");
    }

    public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AxeWindows.Add(target.WindowHandle);
        if (target.WindowHandle == Target.WindowHandle && Windows.Visible.Count > 1)
            throw new InvalidOperationException("Parent Axe must remain isolated from visible owned HWNDs.");
        if (AxeFailure) throw new InvalidOperationException("Axe fixture execution failed.");
        return Task.FromResult(AuditFixture.Axe() with { ProcessId = target.ProcessId, WindowHandle = target.WindowHandle });
    }

    public Task<CommandResult> UiAsync(AuditTarget scope, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        var call = new Call(scope.WindowHandle, args);
        Calls.Add(call);
        JsonObject response;
        switch (args[0])
        {
            case "inspect":
                response = Tree(scope);
                break;
            case "get-property":
                response = Properties(scope, args[1]);
                break;
            case "invoke":
                var id = Id(args[1]);
                if (id.StartsWith("Nav", StringComparison.Ordinal))
                {
                    Page = id["Nav".Length..].ToLowerInvariant();
                    Focused = id;
                }
                else if (id == "OpenInfoDialog")
                {
                    Dialog = true;
                    Focused = "CloseInfoDialog";
                }
                else if (id == "CloseInfoDialog")
                {
                    Dialog = false;
                    Focused = PostDialogFocus;
                }
                else if (id == "OpenScopeProbe" && !IgnoreProbe)
                    Windows.AddOwned(200);
                else if (id != "OpenScopeProbe")
                    throw new InvalidOperationException("Unexpected fake invocation: " + id);
                response = Action(scope, args[1]);
                response["pattern"] = TabNavigation && id.StartsWith("Nav", StringComparison.Ordinal)
                    ? "SelectionItemPattern" : "InvokePattern";
                break;
            case "expand-collapse":
                if (Id(args[1]) != "DensityCombo" || args[2] != "--state")
                    throw new InvalidOperationException("Unexpected disclosure target.");
                Expanded = args[3] == "expanded";
                if (ExternalCombo && Expanded) Windows.AddOwned(100);
                if (ExternalCombo && !Expanded) Windows.Remove(100);
                response = Action(scope, args[1]);
                response["pattern"] = "ExpandCollapsePattern";
                response["state"] = Expanded ? "Expanded" : "Collapsed";
                break;
            case "scroll":
                if (Id(args[1]) != "ContentScroller" || args[2] != "--vertical-percent")
                    throw new InvalidOperationException("A wheel or unexpected scroll target was used.");
                var requested = double.Parse(args[3], CultureInfo.InvariantCulture);
                ScrollRequests.Add(requested);
                if (!IgnoreScroll) Percent = requested;
                response = Action(scope, args[1]);
                response["verticalPercent"] = requested;
                response["actualVerticalPercent"] = Percent;
                break;
            case "get-focused":
                response = new()
                {
                    ["hasFocus"] = Focused is not null,
                    ["element"] = Focused is null ? null : Element(Focused,
                        TabNavigation && Focused.StartsWith("Nav", StringComparison.Ordinal) ? "TabItem" : "Button")
                };
                break;
            case "audit-snapshot":
                response = JsonNode.Parse(AuditFixture.Snapshot().GetRawText())!.AsObject();
                response["windowHandle"] = scope.WindowHandle;
                if (PartialSnapshot) response["status"] = "partial";
                break;
            case "audit-screenshot":
                File.WriteAllBytes(args[2], Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0f0AAAAASUVORK5CYII="));
                response = new()
                {
                    ["schemaVersion"] = 1, ["processId"] = scope.ProcessId, ["windowHandle"] = scope.WindowHandle,
                    ["status"] = "captured", ["captureMethod"] = "windows-graphics-capture",
                    ["activationRequested"] = false, ["filePath"] = args[2], ["width"] = 1, ["height"] = 1
                };
                break;
            default:
                throw new InvalidOperationException("Unapproved fake command: " + args[0]);
        }
        EditResponse?.Invoke(call, response);
        AfterCommand?.Invoke(call);
        return Task.FromResult(new CommandResult(0, response.ToJsonString(Json.Options), ""));
    }

    public JsonObject Tree(AuditTarget scope)
    {
        var root = Element(scope.WindowHandle == 100 ? "DensityPopup" : "MainWindow", "Window");
        var children = root["children"]!.AsArray();
        if (scope.WindowHandle == 100)
            AddItems(children);
        else
        {
            var navigation = Element("MainNavigation", "Group");
            foreach (var page in Profile.Pages)
                navigation["children"]!.AsArray().Add(Element(page.NavigationId, TabNavigation ? "TabItem" : "Button"));
            children.Add(navigation);
            var current = Profile.Pages.Single(page => page.Id == Page);
            children.Add(Element(current.ReadyId, "Text"));
            if (Page == "basics")
            {
                var combo = Element("DensityCombo", "ComboBox");
                combo["expandState"] = Expanded ? "expanded" : "collapsed";
                children.Add(combo);
                if (Expanded && !ExternalCombo) AddItems(children);
            }
            else if (Page == "scrolling")
            {
                var scroller = Element("ContentScroller", "Pane");
                scroller["isScrollable"] = true;
                var top = Element("ScrollTopAnchor", "Text");
                top["isOffscreen"] = Percent > 0.1;
                var bottom = Element("ScrollBottomAnchor", "Text");
                bottom["isOffscreen"] = HideBottom || Percent < 99.9;
                scroller["children"]!.AsArray().Add(top);
                scroller["children"]!.AsArray().Add(bottom);
                scroller["children"]!.AsArray().Add(Element("ScrollBottomButton", "Button"));
                children.Add(scroller);
            }
            else
            {
                children.Add(Element("OpenInfoDialog", "Button"));
                children.Add(Element("OpenScopeProbe", "Button"));
                if (Dialog)
                {
                    var dialog = Element("InfoDialog", "Pane");
                    dialog["children"]!.AsArray().Add(Element("CloseInfoDialog", "Button"));
                    children.Add(dialog);
                }
            }
        }
        var result = new JsonObject
        {
            ["interactive"] = false, ["hideDisabled"] = false, ["hideOffscreen"] = false,
            ["windows"] = new JsonArray(new JsonObject
            {
                ["hwnd"] = scope.WindowHandle, ["processId"] = scope.ProcessId, ["elements"] = new JsonArray(root)
            })
        };
        EditTree?.Invoke(result);
        return result;
    }

    private void AddItems(JsonArray parent)
    {
        foreach (var id in Profile.Pages[0].Dropdowns[0].ItemIds)
            parent.Add(Element(id, "ListItem"));
    }

    private JsonObject Properties(AuditTarget scope, string selector)
    {
        var id = Id(selector);
        var node = Find(Tree(scope), id) ?? throw new InvalidOperationException("Missing fake control: " + id);
        var properties = new JsonObject
        {
            ["AutomationId"] = id, ["ControlType"] = node["type"]!.DeepClone(),
            ["IsEnabled"] = true, ["IsOffscreen"] = node["isOffscreen"]!.DeepClone(),
            ["HasKeyboardFocus"] = Focused == id
        };
        if (id == "DensityCombo")
        {
            properties["ExpandCollapseState"] = Expanded ? "Expanded" : "Collapsed";
            properties["SelectionAutomationIds"] = new JsonArray(Selected);
        }
        if (TabNavigation && id.StartsWith("Nav", StringComparison.Ordinal))
            properties["IsSelected"] = Page == id["Nav".Length..].ToLowerInvariant();
        if (id == "ContentScroller")
        {
            properties["ScrollVerticalPercent"] = Percent;
            properties["ScrollVerticalViewSize"] = ViewSize;
            properties["HorizontallyScrollable"] = false;
            properties["VerticallyScrollable"] = true;
        }
        EditProperties?.Invoke(id, properties);
        return new() { ["elementId"] = "control-" + id, ["hwnd"] = scope.WindowHandle, ["properties"] = properties };
    }

    private static string Id(string selector) => selector.StartsWith("control-", StringComparison.Ordinal) ? selector["control-".Length..] : selector;
    private static JsonObject Action(AuditTarget target, string selector) => new() { ["hwnd"] = target.WindowHandle, ["elementId"] = selector };
    public static JsonObject Element(string id, string type) => new()
    {
        ["automationId"] = id, ["name"] = id, ["type"] = type, ["selector"] = id,
        ["isEnabled"] = true, ["isOffscreen"] = false, ["isInvokable"] = type is "Button" or "TabItem",
        ["hasMoreChildren"] = false, ["children"] = new JsonArray()
    };
    public static JsonObject? Find(JsonNode? json, string id) => NativeSurveySessionTestFixture.Find(json, id);
    public sealed record Call(long Window, string[] Arguments)
    {
        public bool Input => Arguments[0] is "invoke" or "expand-collapse" or "scroll" or "focus" or "send-keys";
    }

    public sealed class NativeFake : ISurveyNativeContext
    {
        public SurveyNativeWindow Window { get; set; } = new(true, false, true, "Default", "Default", "Default", 1024, 768, 96);
        public SurveyNativeWindow ReadWindow(AuditTarget target) => Window;
        public SurveyContext ReadContext(AuditTarget target, SurveyNativeWindow window) =>
            throw new InvalidOperationException("The cheap monitor does not need binary-version discovery.");
    }

    public sealed class WindowFake(AuditTarget target) : IExploreWindowSource
    {
        private Action<ExploreWindowSignal>? changed;
        public long Foreground { get; set; } = target.WindowHandle;
        public List<ExploreScopeWindow> Visible { get; } = [new(target.WindowHandle, target.ProcessId, 0)];
        public int ForegroundRequests { get; private set; }
        public bool Disposed { get; private set; }
        public ExploreWindowBoundary Read(AuditTarget target) => new(Foreground, Visible.ToArray());
        public bool TryForeground(AuditTarget target) { ForegroundRequests++; Foreground = target.WindowHandle; return true; }
        public void Start(AuditTarget target, Action<ExploreWindowSignal> changed) => this.changed = changed;
        public void Signal(string kind = "tick", long hwnd = 0) => changed?.Invoke(new(kind, hwnd));
        public void AddOwned(long handle, long? owner = null, int? pid = null, string? title = null)
        {
            Visible.Add(new(handle, pid ?? target.ProcessId, owner ?? target.WindowHandle, title ?? (handle == 200 ? "AccessLoop scope probe" : "DensityPopup")));
            Signal("window", handle);
        }
        public void Remove(long handle)
        {
            Visible.RemoveAll(window => window.Handle == handle);
            Foreground = target.WindowHandle;
            Signal("window", handle);
        }
        public void Dispose() => Disposed = true;
    }
}
