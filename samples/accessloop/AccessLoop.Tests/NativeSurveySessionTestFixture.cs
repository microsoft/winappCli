using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessLoop.Tests;

internal sealed class NativeSurveySessionTestFixture : IAuditPlatform
{
    public static SurveyProfile Profile => new()
    {
        Name = "PowerToys Settings",
        ProcessName = "PowerToys.Settings",
        NavigationPaneId = "PaneRoot",
        MenuRootId = "MenuItemsHost",
        ItemClassName = "Microsoft.UI.Xaml.Controls.NavigationViewItem",
        HeadingAutomationId = "Header",
        GroupIds =
        [
            "SystemToolsNavItem", "WindowingAndLayoutsNavItem", "InputOutputNavItem",
            "FileManagementNavItem", "AdvancedNavItem"
        ],
        ExcludedIds = ["OOBENavItem", "WhatIsNewNavItem", "FeedbackNavItem"],
        HeadingAliases = new Dictionary<string, string[]>
        {
            ["AwakeNavItem"] = ["唤醒"],
            ["AlwaysOnTopNavItem"] = ["前端显示"],
            ["GrabAndMoveNavItem"] = ["Grab And Move"]
        },
        TimeoutSeconds = 1
    };

    public AuditTarget Target { get; } = AuditFixture.Target with { ProcessName = Profile.ProcessName };
    public NativeContextFake Native { get; } = new();
    public Dictionary<string, bool> Groups { get; } = Profile.GroupIds.ToDictionary(id => id, _ => false, StringComparer.Ordinal);
    public List<SurveyPageDefinition> Pages { get; } =
    [
        new("GeneralNavItem", "General", []),
        new("AwakeNavItem", "Awake", ["SystemToolsNavItem"]),
        new("AlwaysOnTopNavItem", "Always On Top", ["WindowingAndLayoutsNavItem"]),
        new("GrabAndMoveNavItem", "Grab and move", ["WindowingAndLayoutsNavItem"]),
        new("KeyboardManagerNavItem", "Keyboard Manager", ["InputOutputNavItem"]),
        new("FileExplorerNavItem", "File Explorer", ["FileManagementNavItem"]),
        new("EnvironmentVariablesNavItem", "Environment Variables", ["AdvancedNavItem"])
    ];
    public List<Call> Calls { get; } = [];
    public List<string> Invoked { get; } = [];
    public List<string[]> Keys { get; } = [];
    public HashSet<string> Offscreen { get; } = new(StringComparer.Ordinal);
    public string Selected { get; set; } = "GeneralNavItem";
    public string? Focused { get; private set; }
    public string? HeadingName { get; set; }
    public string HeadingType { get; set; } = "Text";
    public string? HeadingIdOverride { get; set; }
    public double HeadingX { get; set; } = 300;
    public double HeadingY { get; set; } = 20;
    public bool DuplicateHeading { get; set; }
    public bool EmptyHeadingSearch { get; set; }
    public bool TruncatedHeadingSearch { get; set; }
    public bool IgnoreFocus { get; set; }
    public bool IgnoreGroupKeys { get; set; }
    public bool IgnoreSelection { get; set; }
    public bool IdentityValid { get; set; } = true;
    public bool ReturnResolvedSelectors { get; set; }
    public string InvokePattern { get; set; } = "SelectionItemPattern";
    public Action<JsonObject>? EditTree { get; set; }
    public Action<string, JsonObject>? EditProperties { get; set; }
    public Action<string[], JsonObject>? EditResponse { get; set; }
    public Action<string[]>? AfterCommand { get; set; }
    public JsonElement Snapshot { get; set; } = AuditFixture.Snapshot();
    public int SnapshotExit { get; set; }
    public AccessibilityScan AxeScan { get; set; } = AuditFixture.Axe();
    public int AxeCalls { get; private set; }
    public int AttachCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    public NativeSurveySession Session(SurveyProfile? profile = null, bool screenshot = false) =>
        new(profile ?? Profile, this, Target, "native-fixture", screenshot, Native);

    public async Task<SurveyInventory> DiscoverAsync(NativeSurveySession session)
    {
        var original = await session.ReadNavigationStateAsync(CancellationToken.None);
        return await session.DiscoverAsync(original, CancellationToken.None);
    }

    public void ExpandAll()
    {
        foreach (var id in Groups.Keys.ToArray())
            Groups[id] = true;
    }

    public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("native-fixture");
    public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token)
    {
        AttachCalls++;
        throw new InvalidOperationException("An existing-app survey must not attach to another process.");
    }
    public void VerifyTarget(AuditTarget target)
    {
        VerifyCalls++;
        if (!IdentityValid || target.ProcessId != Target.ProcessId || target.WindowHandle != Target.WindowHandle
            || target.ProcessStartedAtUtc != Target.ProcessStartedAtUtc || target.ProcessName != Target.ProcessName)
            throw new InvalidOperationException("The original process start time or HWND changed.");
    }
    public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
    {
        AxeCalls++;
        return Task.FromResult(AxeScan);
    }
    public Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var args = arguments.ToArray();
        Calls.Add(new(target, args, timeout));
        JsonObject response;
        var exit = 0;
        switch (args[0])
        {
            case "inspect":
                response = Tree();
                break;
            case "get-property":
                response = Properties(args[1]);
                break;
            case "scroll-into-view":
                Offscreen.Remove(OriginalId(args[1]));
                response = Action(args[1]);
                break;
            case "focus":
                Focused = IgnoreFocus ? null : OriginalId(args[1]);
                response = Action(args[1]);
                break;
            case "invoke":
                Invoked.Add(OriginalId(args[1]));
                if (!IgnoreSelection)
                    Selected = OriginalId(args[1]);
                response = Action(args[1]);
                response["pattern"] = InvokePattern;
                break;
            case "send-keys":
                Keys.Add(args);
                if (!IgnoreGroupKeys)
                    Groups[OriginalId(args[5])] = !Groups[OriginalId(args[5])];
                response = new()
                {
                    ["hwnd"] = Target.WindowHandle, ["target"] = args[5], ["keys"] = args[1],
                    ["via"] = args[3], ["actionCount"] = 1
                };
                break;
            case "search":
                if (args.Length != 4 || args[2] != "--max")
                    throw new InvalidOperationException("Search must use the native CLI's --max result-limit option.");
                var matches = new JsonArray();
                if (!EmptyHeadingSearch && !(args[1] == Profile.HeadingAutomationId && HeadingIdOverride == ""))
                {
                    var heading = Element(HeadingIdOverride ?? Profile.HeadingAutomationId, HeadingType,
                        HeadingName ?? Pages.Single(page => page.Id == Selected).Name);
                    heading["x"] = HeadingX;
                    heading["y"] = HeadingY;
                    heading["width"] = 600;
                    heading["height"] = 40;
                    heading["value"] = "PRIVATE-SEARCH-VALUE";
                    matches.Add(heading);
                    if (DuplicateHeading)
                        matches.Add(heading.DeepClone());
                    matches.Add(new JsonObject
                    {
                        ["automationId"] = "PrivateInput", ["name"] = "PRIVATE-INPUT-TEXT", ["type"] = "Edit",
                        ["isOffscreen"] = false, ["value"] = "PRIVATE-SEARCH-VALUE"
                    });
                }
                else
                    exit = 1;
                response = new()
                {
                    ["matches"] = matches, ["matchCount"] = matches.Count, ["hasMore"] = TruncatedHeadingSearch
                };
                break;
            case "audit-snapshot":
                response = JsonNode.Parse(Snapshot.GetRawText())!.AsObject();
                exit = SnapshotExit;
                break;
            case "audit-screenshot":
                var imagePath = args[2];
                File.WriteAllBytes(imagePath, Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0f0AAAAASUVORK5CYII="));
                response = new()
                {
                    ["schemaVersion"] = 1, ["processId"] = target.ProcessId, ["windowHandle"] = target.WindowHandle,
                    ["status"] = "captured", ["captureMethod"] = "windows-graphics-capture",
                    ["activationRequested"] = false, ["filePath"] = imagePath, ["width"] = 1, ["height"] = 1
                };
                break;
            default:
                throw new InvalidOperationException("Unexpected survey command: " + args[0]);
        }
        EditResponse?.Invoke(args, response);
        AfterCommand?.Invoke(args);
        return Task.FromResult(new CommandResult(exit, response.ToJsonString(Json.Options), ""));
    }

    public JsonObject Tree()
    {
        var menu = Element(Profile.MenuRootId, "Group");
        var children = menu["children"]!.AsArray();
        foreach (var page in Pages.Where(page => page.GroupIds.Count == 0))
            children.Add(Navigation(page.Id, page.Name));
        foreach (var id in Profile.GroupIds)
        {
            var group = Navigation(id, id);
            group["expandState"] = Groups[id] ? "expanded" : "collapsed";
            if (Groups[id])
                foreach (var page in Pages.Where(page => page.GroupIds.Count > 0 && page.GroupIds[0] == id))
                    group["children"]!.AsArray().Add(Navigation(page.Id, page.Name));
            children.Add(group);
        }
        var pane = Element(Profile.NavigationPaneId, "Pane");
        pane["x"] = 0;
        pane["y"] = 0;
        pane["width"] = 200;
        pane["height"] = 800;
        pane["children"]!.AsArray().Add(menu);
        var footer = Element("FooterMenuItemsHost", "Group");
        foreach (var id in Profile.ExcludedIds)
            footer["children"]!.AsArray().Add(Navigation(id, id));
        pane["children"]!.AsArray().Add(footer);
        var input = Element("NavigationFilter", "Edit", "PRIVATE-INPUT-TEXT");
        input["value"] = "PRIVATE-TREE-VALUE";
        pane["children"]!.AsArray().Add(input);
        var tree = new JsonObject
        {
            ["interactive"] = false, ["hideDisabled"] = false, ["hideOffscreen"] = false,
            ["windows"] = new JsonArray(new JsonObject
            {
                ["hwnd"] = Target.WindowHandle, ["processId"] = Target.ProcessId, ["elements"] = new JsonArray(pane)
            })
        };
        EditTree?.Invoke(tree);
        return tree;
    }

    private JsonObject Properties(string selector)
    {
        var id = OriginalId(selector);
        var node = Find(Tree(), id) ?? throw new InvalidOperationException("The fake selector is missing: " + selector);
        var properties = new JsonObject
        {
            ["AutomationId"] = node["automationId"]!.GetValue<string>(),
            ["Name"] = node["name"]?.GetValue<string>(),
            ["ClassName"] = node["className"]?.GetValue<string>(),
            ["ControlType"] = node["type"]!.GetValue<string>(),
            ["IsEnabled"] = (node["isEnabled"]!.GetValue<bool>()).ToString(),
            ["IsOffscreen"] = (node["isOffscreen"]!.GetValue<bool>()).ToString(),
            ["HasKeyboardFocus"] = (Focused == id).ToString(),
            ["IsSelected"] = (Selected == id).ToString(),
            ["HelpText"] = "PRIVATE-UNNEEDED-PROPERTY"
        };
        if (node["expandState"] is { } expand)
            properties["ExpandCollapseState"] = expand.GetValue<string>() == "expanded" ? "Expanded" : "Collapsed";
        if (node["toggleState"] is { } toggle)
            properties["ToggleState"] = toggle.DeepClone();
        if (node["value"] is { } value)
            properties["Value"] = value.DeepClone();
        EditProperties?.Invoke(id, properties);
        return new JsonObject { ["elementId"] = ReturnResolvedSelectors ? "resolved-" + id : id, ["properties"] = properties };
    }

    private string OriginalId(string selector) => ReturnResolvedSelectors && selector.StartsWith("resolved-", StringComparison.Ordinal)
        ? selector["resolved-".Length..] : selector;

    private JsonObject Action(string selector) => new() { ["elementId"] = selector, ["hwnd"] = Target.WindowHandle };

    public JsonObject Navigation(string id, string name)
    {
        var node = Element(id, "ListItem", name);
        node["className"] = Profile.ItemClassName;
        node["isInvokable"] = true;
        node["isOffscreen"] = Offscreen.Contains(id);
        return node;
    }

    public static JsonObject Element(string id, string type, string? name = null) => new()
    {
        ["automationId"] = id, ["name"] = name ?? id, ["type"] = type, ["className"] = type,
        ["selector"] = id, ["isEnabled"] = true, ["isOffscreen"] = false, ["isInvokable"] = false,
        ["hasMoreChildren"] = false, ["children"] = new JsonArray()
    };

    public static JsonObject? Find(JsonNode? node, string id)
    {
        if (node is JsonObject obj)
        {
            if (obj["automationId"]?.GetValue<string>() == id)
                return obj;
            foreach (var (_, child) in obj)
                if (Find(child, id) is { } found)
                    return found;
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (Find(child, id) is { } found)
                    return found;
        return null;
    }

    public sealed record Call(AuditTarget Target, string[] Arguments, TimeSpan Timeout);

    internal sealed class NativeContextFake : ISurveyNativeContext
    {
        public SurveyNativeWindow Window { get; set; } = new(true, false, true, "Default", "Default", "Default", 1440, 960, 144);
        public SurveyContext Context { get; set; } = new(
            new("PowerToys.Settings", Path.Combine(Environment.CurrentDirectory, "approved-app", "PowerToys.Settings.exe"),
                new string('A', 64), "0.95.1", new string('B', 64)),
            new(1440, 960, 144, "Windows test build", "en-US", false, null));
        public int WindowReads { get; private set; }
        public SurveyNativeWindow ReadWindow(AuditTarget target)
        {
            WindowReads++;
            return Window;
        }
        public SurveyContext ReadContext(AuditTarget target, SurveyNativeWindow window) => Context;
    }
}
