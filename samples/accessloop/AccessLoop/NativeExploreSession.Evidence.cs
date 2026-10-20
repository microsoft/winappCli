using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeExploreSession
{
    private sealed record Control(string Selector, JsonElement Properties);
    private sealed record Reading(ExploreObservation Observation, IReadOnlyDictionary<string, string> Facts);
    private sealed record StateCheck(string Id, bool Passed, string Detail);
    private Reading? lastReading;

    private Reading Remember(ExploreObservation observation, IReadOnlyDictionary<string, string>? facts = null) =>
        lastReading = new(observation, facts ?? new Dictionary<string, string>());

    private async Task<ExploreStateResult> CaptureAsync(
        ExplorePage page, string kind, string name, AuditTarget captureTarget,
        Func<CancellationToken, Task<Reading>> observe, CancellationToken token)
    {
        RequireStateBudget();
        var stateRoot = Path.Combine(output, "states");
        if (!Directory.Exists(stateRoot)) AuditFiles.CreateNewDirectory(stateRoot);
        AuditFiles.EnsureDirectory(stateRoot);
        string path, relative;
        do
        {
            if (++sequence > 100000) throw new IOException("The state-directory sequence budget was exceeded.");
            relative = Path.Combine("states", sequence.ToString("D3", CultureInfo.InvariantCulture));
            path = Path.Combine(output, relative);
        } while (Path.Exists(path));
        AuditFiles.CreateNewDirectory(path);
        var state = new ExploreStateResult
        {
            Id = "state-" + sequence.ToString("D3", CultureInfo.InvariantCulture),
            PageId = page.Id, Kind = kind, Name = name, Directory = relative, Target = captureTarget
        };
        report.States.Add(state);
        var checks = new List<StateCheck>();
        progress?.Invoke(new("state", page.Id, state.Id, name));
        try
        {
            monitor.Check(captureTarget, audit: true);
            lastReading = null;
            Reading before;
            try { before = await observe(token); }
            catch
            {
                if (lastReading is { } observed)
                {
                    state.Before = observed.Observation;
                    AuditFiles.WriteNew(Path.Combine(path, "state-before.json"), observed);
                }
                throw;
            }
            state.Before = before.Observation;
            AuditFiles.WriteNew(Path.Combine(path, "state-before.json"), before);
            checks.Add(new("state-before", true, "Exact page and declared control state verified before capture."));
            var clock = Stopwatch.StartNew();
            AuditReport audit;
            try
            {
                var options = new AuditOptions(null, captureTarget.WindowHandle, "winapp", path,
                    profile.TreeDepth, profile.MaxElements, checked(profile.TimeoutSeconds * 1000), 0, 0, false, screenshot);
                audit = await new AuditRunner(platform).RunAsync(options, captureTarget, winappVersion, path, token);
                AuditFiles.WriteNew(Path.Combine(path, "scan.json"), audit);
                AuditReportRenderer.Write(Path.Combine(path, "report.html"), audit);
                state.ScanSha256 = AuditFiles.Hash(Path.Combine(path, "scan.json"));
                state.CaptureStatus = audit.Status;
                state.RuleFailures = audit.RuleFailureCount;
            }
            finally { auditMilliseconds += clock.ElapsedMilliseconds; }

            monitor.Check(captureTarget, audit: true);
            Reading after;
            lastReading = null;
            try
            {
                after = await observe(token);
                state.After = after.Observation;
                AuditFiles.WriteNew(Path.Combine(path, "state-after.json"), after);
                if (!SameReading(before, after))
                    throw new InvalidOperationException("The declared page/control state changed during capture.");
            }
            catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex) && ex is not OperationCanceledException && monitor.Violation is null)
            {
                if (state.After is null && lastReading is { } observed)
                {
                    state.After = observed.Observation;
                    AuditFiles.WriteNew(Path.Combine(path, "state-after.json"), observed);
                }
                monitor.Stop("state-changed", "Post-capture state verification failed. Changed-state images or scans do not count as coverage: " + ex.Message);
                throw;
            }
            checks.Add(new("state-after", true, "The same exact page, control state and scope were verified after capture."));
            var valid = audit.Error is null && audit.Snapshot is not null && audit.Axe is not null
                && audit.Status is "completed" or "partial";
            checks.Add(new("audit-execution", valid, audit.Error ?? $"Capture status: {audit.Status}; audit coverage and privacy gaps remain separate."));
            if (!valid)
                throw new InvalidOperationException(audit.Error ?? "The audit did not provide both the scoped snapshot and Axe evidence.");
            state.Status = "captured";
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            state.Status = "blocked";
            state.Error = Error(ex);
            checks.Add(new("capture-verified", false, state.Error));
            if (monitor.Violation is not null || ex is OperationCanceledException or ExploreBudgetException)
                throw;
        }
        finally
        {
            state.FinishedAtUtc = DateTimeOffset.UtcNow;
            AuditFiles.WriteNew(Path.Combine(path, "state-checks.json"), checks);
            AuditFiles.WriteNew(Path.Combine(path, "state.json"), state);
        }
        return state;
    }

    private static bool SameReading(Reading before, Reading after)
    {
        var a = before.Observation;
        var b = after.Observation;
        return a.PageId == b.PageId && a.WindowHandle == b.WindowHandle && a.PageReady && b.PageReady
            && a.ControlId == b.ControlId && a.ExpansionState == b.ExpansionState && a.DialogPresent == b.DialogPresent
            && a.SelectionAutomationIds.SequenceEqual(b.SelectionAutomationIds, StringComparer.Ordinal)
            && (a.ScrollPercent is null ? b.ScrollPercent is null : b.ScrollPercent is { } percent && Near(a.ScrollPercent.Value, percent))
            && (a.ViewSizePercent is null ? b.ViewSizePercent is null : b.ViewSizePercent is { } view && Near(a.ViewSizePercent.Value, view, ViewSizeTolerance))
            && before.Facts.Count == after.Facts.Count
            && before.Facts.All(pair => after.Facts.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private async Task<Reading> ReadBaseAsync(ExplorePage page, CancellationToken token)
    {
        var tree = await InspectAsync(target, token);
        RequirePage(tree, page.Id);
        RequireNoDialogs(tree, page, null);
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dropdown in page.Dropdowns)
        {
            var control = await ComboAsync(tree, dropdown, token);
            Require(Text(control.Properties, "ExpandCollapseState") == "Collapsed", "A base page cannot be captured with an undeclared expanded dropdown.");
            facts.Add(dropdown.ControlId, string.Join("|", Selection(control, dropdown)));
        }
        var observation = Observation(page);
        if (page.Scroll is { } scroll)
        {
            var position = await ScrollAsync(tree, page, scroll, token);
            observation = observation with { ControlId = scroll.ContainerId, ScrollPercent = position.Percent, ViewSizePercent = position.ViewSize };
        }
        return Remember(observation, facts);
    }

    private async Task NavigateAsync(ExplorePage page, CancellationToken token)
    {
        Require(!overlayOpen, "Underlying-page navigation is forbidden while a declared popup or dialog is open.");
        var tree = await InspectAsync(target, token);
        if (currentPage is not null) RequirePage(tree, currentPage);
        var control = await NavigationAsync(tree, page, token);
        var tab = Text(control.Properties, "ControlType") == "TabItem";
        if (currentPage != page.Id)
        {
            var action = await UiAsync(target, ["invoke", control.Selector], token, input: true);
            ExactAction(action, target, control.Selector, tab ? "SelectionItemPattern" : "InvokePattern");
        }
        for (var poll = 0; poll < 30; poll++)
        {
            tree = await InspectAsync(target, token);
            var active = ActivePage(tree);
            if (active == page.Id)
            {
                RequirePage(tree, page.Id);
                if (tab)
                    Require(PropertyBool((await NavigationAsync(tree, page, token)).Properties, "IsSelected"),
                        "The ready page does not have its declared tab selected.");
                currentPage = page.Id;
                return;
            }
            if (active is not null && active != currentPage)
            {
                monitor.Stop("state-changed", "Navigation activated an unexpected declared page; no further input is authorized.");
                throw new ExploreStoppedException("Navigation activated an unexpected page.");
            }
            await Task.Delay(100, token);
        }
        throw new TimeoutException($"Page '{page.Id}' did not expose its unique visible ready heading.");
    }

    private async Task<Control> NavigationAsync(Tree tree, ExplorePage page, CancellationToken token)
    {
        var root = tree.Exact(profile.NavigationRootId);
        var node = tree.Exact(page.NavigationId);
        Require(node.Under(root), $"Navigation '{page.NavigationId}' is not under the declared navigation root.");
        if (node.Type == "Button")
            return await ButtonAsync(tree, page.NavigationId, token);
        Require(node.Type == "TabItem" && node.Invokable && !node.Offscreen
            && node.Toggle is null && node.Expansion is null, "Declared navigation must be an ordinary Button or selection-only TabItem.");
        var control = await ConfirmAsync(node, target, token);
        Require(PropertyBool(control.Properties, "IsEnabled") && !PropertyBool(control.Properties, "IsOffscreen")
            && !Present(control.Properties, "ToggleState") && !Present(control.Properties, "ExpandCollapseState")
            && !Present(control.Properties, "Value") && !Present(control.Properties, "Value.Value")
            && !Present(control.Properties, "SelectionAutomationIds"),
            "A navigation tab is disabled, hidden or exposes an unexpected action pattern.");
        _ = PropertyBool(control.Properties, "IsSelected");
        return control;
    }

    private async Task<Control> ButtonAsync(Tree tree, string id, CancellationToken token, AuditTarget? scope = null)
    {
        var node = tree.Exact(id);
        Require(node.Type == "Button" && node.Invokable && !node.Offscreen
            && node.Toggle is null && node.Expansion is null, $"'{id}' must be a visible, literal AutomationId Button with no toggle/expansion fallback.");
        var control = await ConfirmAsync(node, scope ?? target, token);
        Require(PropertyBool(control.Properties, "IsEnabled") && !PropertyBool(control.Properties, "IsOffscreen")
            && !Present(control.Properties, "ToggleState") && !Present(control.Properties, "ExpandCollapseState")
            && !Present(control.Properties, "Value") && !Present(control.Properties, "Value.Value")
            && !Present(control.Properties, "IsSelected") && !Present(control.Properties, "SelectionAutomationIds"),
            $"Button '{id}' is disabled, hidden or exposes a setting/selection fallback.");
        return control;
    }

    private async Task<Control> ConfirmAsync(Node node, AuditTarget scope, CancellationToken token)
    {
        var response = await UiAsync(scope, ["get-property", node.Selector], token);
        var properties = response.GetProperty("properties");
        var selector = AuditJson.String(response, "elementId");
        Require(SafeSelector(selector) && (node.Selector == node.Id || selector == node.Selector)
            && Text(properties, "AutomationId") == node.Id && Text(properties, "ControlType") == node.Type,
            $"Property lookup did not resolve the exact declared AutomationId and type for '{node.Id}'.");
        if (Present(response, "hwnd")) Require(AuditJson.Long(response, "hwnd") == scope.WindowHandle, "Control properties came from a different HWND.");
        return new(selector, properties.Clone());
    }

    private string? ActivePage(Tree tree)
    {
        string? active = null;
        foreach (var page in profile.Pages)
        {
            var matches = tree.Matches(page.ReadyId);
            Require(matches.Length <= 1, $"Ready heading '{page.ReadyId}' is not unique.");
            if (matches.Length == 1 && !matches[0].Offscreen)
            {
                Require(matches[0].Type == "Text", $"Ready marker '{page.ReadyId}' must be a Text heading.");
                Require(active is null, "More than one declared page is visibly ready.");
                active = page.Id;
            }
        }
        return active;
    }

    private void RequirePage(Tree tree, string pageId)
    {
        var active = ActivePage(tree);
        Remember(new() { PageId = active ?? pageId, WindowHandle = target.WindowHandle, PageReady = active == pageId });
        Require(active == pageId, $"Expected the unique visible ready heading for page '{pageId}'.");
        var page = profile.Pages.Single(item => item.Id == pageId);
        if (page.Scroll is { } scroll)
            Require(!tree.Exact(page.ReadyId).Under(tree.Exact(scroll.ContainerId)), "The ready heading must be outside the declared content scroller.");
    }

    private static void RequireNoDialogs(Tree tree, ExplorePage page, string? allowed)
    {
        foreach (var dialog in page.Dialogs)
            Require(dialog.RootId == allowed || !tree.Matches(dialog.RootId).Any(node => !node.Offscreen),
                $"Unexpected open dialog '{dialog.RootId}'; no underlying-page actions are permitted.");
    }

    private ExploreObservation Observation(ExplorePage page, AuditTarget? scope = null) =>
        new() { PageId = page.Id, WindowHandle = (scope ?? target).WindowHandle, PageReady = true };

    private async Task<string?> FocusAsync(AuditTarget scope, CancellationToken token)
    {
        var response = await UiAsync(scope, ["get-focused"], token);
        if (AuditJson.Boolean(response, "hasFocus") != true) return null;
        if (Present(response, "hwnd")) Require(AuditJson.Long(response, "hwnd") == scope.WindowHandle, "Focus observation came from a different window.");
        var element = response.GetProperty("element");
        if (Present(element, "windowHandle")) Require(AuditJson.Long(element, "windowHandle") == scope.WindowHandle, "Focused element is outside the approved window.");
        return Text(element, "automationId");
    }

    private async Task<Tree> InspectAsync(AuditTarget scope, CancellationToken token) =>
        new(await UiAsync(scope, ["inspect", "--depth", profile.TreeDepth.ToString(CultureInfo.InvariantCulture)], token), scope, profile);

    private async Task<JsonElement> UiAsync(AuditTarget scope, IReadOnlyList<string> args, CancellationToken token,
        bool input = false, bool popup = false, bool closePopup = false, Action? issuing = null)
    {
        var result = await platform.ExecuteAsync(scope,
            ct => inner.UiAsync(scope, args, TimeSpan.FromSeconds(profile.TimeoutSeconds), ct),
            token, input: input, popup: popup, closePopup: closePopup, issuing: issuing);
        Require(result.Stdout.Length <= AuditFiles.MaxJsonBytes, "Native output exceeded the bounded JSON size.");
        var json = AuditJson.Object(result, args[0]);
        Require(result.ExitCode == 0 && !Present(json, "error"), $"Native '{args[0]}' failed (exit {result.ExitCode}); no fallback action was attempted.");
        return json;
    }

    private static void ExactAction(JsonElement response, AuditTarget scope, string selector, string pattern)
    {
        Require(AuditJson.Long(response, "hwnd") == scope.WindowHandle
            && AuditJson.String(response, "elementId") == selector && AuditJson.String(response, "pattern") == pattern,
            "The action did not identify the exact approved selector, HWND and required UIA pattern.");
    }

    private static bool SafeSelector(string text) => text.Length is > 0 and <= 512 && !text.StartsWith('-') && !text.Any(char.IsControl);
    private static bool Present(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null;
    private static string? Text(JsonElement value, string name) => AuditJson.OptionalString(value, name);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static bool PropertyBool(JsonElement properties, string name)
    {
        Require(properties.TryGetProperty(name, out var value), $"Required property '{name}' is unavailable.");
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        Require(value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out _), $"Required property '{name}' is not a boolean.");
        return bool.Parse(value.GetString()!);
    }
    private static double PropertyNumber(JsonElement properties, string name)
    {
        Require(properties.TryGetProperty(name, out var value), $"Required property '{name}' is unavailable; the control must expose its own ScrollPattern.");
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
        Require(double.IsFinite(number), $"Required property '{name}' must be finite.");
        return number;
    }

    private sealed class Node(JsonElement json, Node? parent)
    {
        public Node? Parent { get; } = parent;
        public string? Id { get; } = Text(json, "automationId");
        public string Type { get; } = AuditJson.String(json, "type");
        public string Selector { get; } = Text(json, "selector") ?? "";
        public bool Offscreen { get; } = AuditJson.Boolean(json, "isOffscreen") != false;
        public bool Invokable { get; } = AuditJson.Boolean(json, "isInvokable") == true;
        public string? Toggle { get; } = Text(json, "toggleState");
        public string? Expansion { get; } = Text(json, "expandState");
        public bool Scrollable { get; } = AuditJson.Boolean(json, "isScrollable") == true;
        public bool Under(Node root)
        {
            for (var node = Parent; node is not null; node = node.Parent)
                if (ReferenceEquals(node, root)) return true;
            return false;
        }
    }

    private sealed class Tree
    {
        public List<Node> Nodes { get; } = [];
        public Tree(JsonElement json, AuditTarget target, ExploreProfile profile)
        {
            var windows = AuditJson.Array(json, "windows");
            Require(windows.GetArrayLength() == 1 && AuditJson.Long(windows[0], "hwnd") == target.WindowHandle,
                "Inspection must contain exactly the approved HWND, without owned-window expansion.");
            if (Present(windows[0], "processId")) Require(AuditJson.Integer(windows[0], "processId") == target.ProcessId, "Inspection process identity changed.");
            Require(AuditJson.Boolean(json, "interactive") != true && AuditJson.Boolean(json, "hideOffscreen") != true
                && AuditJson.Boolean(json, "hideDisabled") != true, "Filtered inspection cannot establish unique exact control identities.");
            var roots = AuditJson.Array(windows[0], "elements");
            Require(roots.GetArrayLength() == 1, "Expected one exact UIA window root.");
            Add(roots[0], null, 0);
            if (Present(windows[0], "elementCount"))
                Require(AuditJson.Integer(windows[0], "elementCount") == Nodes.Count, "Inspected node count is inconsistent.");

            void Add(JsonElement value, Node? parent, int depth)
            {
                Require(depth <= profile.TreeDepth && Nodes.Count < profile.MaxElements
                    && AuditJson.Boolean(value, "hasMoreChildren") != true, "The inspected tree is truncated or exceeded the bounded tree budget.");
                if (Present(value, "windowHandle"))
                    Require(AuditJson.Long(value, "windowHandle") == target.WindowHandle, "An inspected node belongs to a different HWND.");
                var node = new Node(value, parent);
                Nodes.Add(node);
                if (Present(value, "children"))
                    foreach (var child in AuditJson.Array(value, "children").EnumerateArray())
                        Add(child, node, depth + 1);
            }
        }
        public Node[] Matches(string id) => Nodes.Where(node => node.Id == id).ToArray();
        public Node Exact(string id)
        {
            var matches = Matches(id);
            Require(matches.Length == 1, $"Expected exactly one literal AutomationId '{id}'; found {matches.Length}.");
            var node = matches[0];
            Require(SafeSelector(node.Selector) && Nodes.Count(item => item.Selector == node.Selector) == 1, $"The selector for '{id}' is missing or ambiguous.");
            return node;
        }
    }
}
