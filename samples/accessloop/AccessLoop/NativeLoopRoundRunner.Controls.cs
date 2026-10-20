using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeLoopRoundRunner
{
    private sealed partial class RoundExecution
    {
        private static readonly HashSet<string> ExpectationProperties = new(StringComparer.Ordinal)
        {
            "Name", "IsEnabled", "IsOffscreen", "IsSelected", "HasKeyboardFocus",
            "ToggleState", "ExpandCollapseState", "ScrollVerticalPercent", "SelectionAutomationIds"
        };

        private sealed record Control(string Id, string Type, string Selector, JsonElement Response)
        {
            public JsonElement Properties => Response.GetProperty("properties");
        }

        private async Task<Tree> InspectAsync(CancellationToken token) =>
            new(await UiAsync(["inspect", "--depth", Number(profile.TreeDepth)], token), target, profile);

        private async Task<Control> ControlAsync(Tree tree, string id, string? expectedType, CancellationToken token)
        {
            var node = tree.Exact(id);
            Require(expectedType is null || node.Type == expectedType, $"'{id}' is not the exact declared {expectedType} control.");
            var response = await UiAsync(["get-property", node.Selector], token);
            Require(response.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object,
                "Native get-property returned no properties object.");
            var selector = AuditJson.String(response, "elementId");
            Require(SafeSelector(selector) && (node.Selector == node.Id || node.Selector == selector)
                && Text(properties, "AutomationId") == id && Text(properties, "ControlType") == node.Type,
                $"Properties no longer identify the exact literal AutomationId/type for '{id}'.");
            if (Present(response, "hwnd")) Require(AuditJson.Long(response, "hwnd") == target.WindowHandle, "Properties belong to a different HWND.");
            return new(id, node.Type, selector, response);
        }

        private string? ActivePage(Tree tree)
        {
            string? active = null;
            foreach (var page in profile.Pages)
            {
                var matches = tree.Matches(page.ReadyId);
                Require(matches.Length <= 1, $"Ready heading '{page.ReadyId}' is ambiguous.");
                if (matches.Length != 1 || matches[0].Offscreen) continue;
                Require(matches[0].Type == "Text", $"Ready marker '{page.ReadyId}' is not a Text heading.");
                Require(active is null, "Multiple declared pages are visibly ready.");
                active = page.Id;
            }
            return active;
        }

        private void RequirePage(Tree tree, string pageId)
        {
            Require(ActivePage(tree) == pageId, $"The current window is not on declared page '{pageId}'.");
            var page = profile.Pages.Single(item => item.Id == pageId);
            var heading = tree.Exact(page.ReadyId);
            foreach (var scroller in profile.Steps.Where(step => step.PageId == pageId && step.Kind == "scroll"))
                foreach (var node in tree.Matches(scroller.TargetId!))
                    Require(!heading.Under(node), "The page-ready heading must remain outside the declared scrolling body.");
        }

        private async Task<Tree> WaitForPageAsync(string? expected, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            for (var poll = 0; poll < profile.TimeoutSeconds * 10 + 1; poll++)
            {
                var tree = await InspectAsync(bounded.Token);
                var active = ActivePage(tree);
                if (active is not null && (expected is null || active == expected))
                {
                    RequirePage(tree, active);
                    var page = profile.Pages.Single(item => item.Id == active);
                    var heading = await ControlAsync(tree, page.ReadyId, "Text", bounded.Token);
                    Require(!Flag(heading.Properties, "IsOffscreen"), "The ready heading became hidden.");
                    return tree;
                }
                if (expected is not null && active is not null && active != currentPage)
                    throw new InvalidDataException($"Navigation reached unexpected page '{active}', not '{expected}'.");
                await Task.Delay(100, bounded.Token);
            }
            throw new TimeoutException("A unique visible declared ready heading was not observed within the readiness bound.");
        }

        private async Task RequireFocusAsync(Control control, CancellationToken token)
        {
            RequireUsable(control);
            Require(Flag(control.Properties, "HasKeyboardFocus"),
                $"'{control.Id}' does not have keyboard focus. No focus or invoke fallback is permitted.");
            var focused = await UiAsync(["get-focused"], token);
            Require(AuditJson.Boolean(focused, "hasFocus") == true, "The target application has no observable focused element.");
            if (Present(focused, "hwnd")) Require(AuditJson.Long(focused, "hwnd") == target.WindowHandle, "Focus left the exact target window.");
            var element = focused.GetProperty("element");
            Require(Text(element, "automationId") == control.Id && Text(element, "type") == control.Type
                && Text(element, "selector") == control.Selector,
                $"Actual focus is not on the exact declared '{control.Id}' control. No corrective input was sent.");
            if (Present(element, "windowHandle"))
                Require(AuditJson.Long(element, "windowHandle") == target.WindowHandle, "The focused element belongs to a different window.");
        }

        private static void RequireUsable(Control control)
        {
            Require(Flag(control.Properties, "IsEnabled") && !Flag(control.Properties, "IsOffscreen")
                && !Flag(control.Properties, "IsPassword"),
                $"'{control.Id}' must be enabled, visible, and known non-password before any input.");
        }

        private async Task WaitExpectationsAsync(LoopStep step, CancellationToken token)
        {
            if (step.Expectations.Count == 0) return;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            try
            {
                for (var poll = 0; poll < profile.TimeoutSeconds * 10 + 1; poll++)
                {
                    var tree = await InspectAsync(bounded.Token);
                    RequirePage(tree, step.PageId);
                    var controls = new Dictionary<string, Control>(StringComparer.Ordinal);
                    var checks = new List<LoopCheckResult>();
                    foreach (var expectation in step.Expectations)
                    {
                        JsonElement? actual = null;
                        string? error = null;
                        try
                        {
                            if (!controls.TryGetValue(expectation.TargetId, out var control))
                            {
                                control = await ControlAsync(tree, expectation.TargetId, null, bounded.Token);
                                controls.Add(expectation.TargetId, control);
                            }
                            action!.After = control.Response;
                            Require(control.Properties.TryGetProperty(expectation.Property, out var value),
                                $"Property '{expectation.Property}' is unavailable on '{expectation.TargetId}'.");
                            actual = value.Clone();
                        }
                        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
                        {
                            error = Error(ex);
                            if (ex is OperationCanceledException or TimeoutException)
                            {
                                actual ??= action!.Checks.LastOrDefault(check => check.TargetId == expectation.TargetId
                                    && check.Property == expectation.Property)?.Actual;
                                if (actual is not null) error += " The last successfully observed value is retained.";
                            }
                            checks.Add(new()
                            {
                                TargetId = expectation.TargetId, Property = expectation.Property, Expected = expectation.Expected,
                                Actual = actual, Passed = false, Error = error
                            });
                            action!.Checks.Clear();
                            action.Checks.AddRange(checks);
                            SaveProgress();
                            throw;
                        }
                        var matched = Matches(expectation.Property, expectation.Expected, actual!.Value);
                        checks.Add(new()
                        {
                            TargetId = expectation.TargetId, Property = expectation.Property, Expected = expectation.Expected,
                            Actual = actual, Passed = matched,
                            Error = matched ? null : $"The observed '{expectation.Property}' does not match the frozen expectation."
                        });
                    }
                    action!.Checks.Clear();
                    action.Checks.AddRange(checks);
                    SaveProgress();
                    if (action.Checks.All(check => check.Passed)) return;
                    await Task.Delay(100, bounded.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && monitor.Violation is null)
            {
                throw new TimeoutException("The explicit expectations did not match within the step timeout; final observed values were retained.");
            }
            throw new TimeoutException("The explicit expectations did not match within the observation budget.");
        }

        private async Task RecordAfterAsync(LoopStep step, CancellationToken token)
        {
            var tree = await InspectAsync(token);
            RequirePage(tree, step.PageId);
            var id = step.TargetId;
            if (id is null)
                id = profile.Pages.Single(page => page.Id == step.PageId).ReadyId;
            else if (tree.Matches(id).Length == 0)
            {
                // A named dialog dismissal may remove its own target. Observe a declared postcondition, never refocus it.
                Require(step.Expectations.Count > 0, "The action target disappeared without a declared observable postcondition.");
                id = step.Expectations[0].TargetId;
            }
            action!.After = (await ControlAsync(tree, id, null, token)).Response;
        }

        private static void ValidateExpectation(LoopExpectation expectation)
        {
            Require(ExpectationProperties.Contains(expectation.Property), "An expectation requests an unsupported property.");
            var expected = expectation.Expected;
            if (expectation.Property == "ScrollVerticalPercent")
                Require(expected.ValueKind == JsonValueKind.Number && expected.TryGetDouble(out var number)
                    && double.IsFinite(number) && number is >= 0 and <= 100, "Scroll expectations require a finite numeric percentage.");
            else if (expectation.Property == "SelectionAutomationIds")
                Require(expected.ValueKind == JsonValueKind.Array
                    && expected.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                    "SelectionAutomationIds expectations must be JSON string arrays.");
            else
                Require(expected.ValueKind == JsonValueKind.String || (expectation.Property == "Name" && expected.ValueKind == JsonValueKind.Null),
                    "Native scalar expectations must be exact JSON strings (or an explicitly null Name).");
        }

        private static bool Matches(string property, JsonElement expected, JsonElement actual)
        {
            if (property != "ScrollVerticalPercent") return JsonElement.DeepEquals(expected, actual);
            return TryNumber(actual, out var observed) && Near(expected.GetDouble(), observed);
        }

        private static bool TryNumber(JsonElement value, out double number)
        {
            number = double.NaN;
            var parsed = value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out number)
                : value.ValueKind == JsonValueKind.String
                    && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            return parsed && double.IsFinite(number);
        }

        private static double Numeric(JsonElement properties, string name)
        {
            Require(properties.TryGetProperty(name, out var value) && TryNumber(value, out _), $"Required finite property '{name}' is unavailable.");
            _ = TryNumber(value, out var result);
            return result;
        }

        private static bool Flag(JsonElement properties, string name)
        {
            var value = Text(properties, name);
            Require(value is "True" or "False", $"Required known native property '{name}' is unavailable.");
            return value == "True";
        }

        private static bool Present(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null;
        private static string? Text(JsonElement value, string name) => AuditJson.OptionalString(value, name);
        private static bool SafeSelector(string value) => value.Length is > 0 and <= 512 && !value.StartsWith('-') && !value.Any(char.IsControl);

        private sealed class Node(JsonElement json, Node? parent)
        {
            public Node? Parent { get; } = parent;
            public string? Id { get; } = Text(json, "automationId");
            public string Type { get; } = AuditJson.String(json, "type");
            public string Selector { get; } = Text(json, "selector") ?? "";
            public bool Offscreen { get; } = AuditJson.Boolean(json, "isOffscreen") != false;
            public bool Scrollable { get; } = AuditJson.Boolean(json, "isScrollable") == true;
            public bool Under(Node root)
            {
                for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
                    if (ReferenceEquals(root, ancestor)) return true;
                return false;
            }
        }

        private sealed class Tree
        {
            public List<Node> Nodes { get; } = [];
            public Tree(JsonElement response, AuditTarget target, LoopProfile profile)
            {
                var windows = AuditJson.Array(response, "windows");
                Require(windows.GetArrayLength() == 1 && AuditJson.Long(windows[0], "hwnd") == target.WindowHandle,
                    "Inspection must contain exactly the original main HWND.");
                if (Present(windows[0], "processId"))
                    Require(AuditJson.Integer(windows[0], "processId") == target.ProcessId, "Inspection process identity changed.");
                Require(AuditJson.Boolean(response, "interactive") != true && AuditJson.Boolean(response, "hideOffscreen") != true
                    && AuditJson.Boolean(response, "hideDisabled") != true, "Filtered inspection cannot prove unique control identity.");
                var roots = AuditJson.Array(windows[0], "elements");
                Require(roots.GetArrayLength() == 1, "Inspection must contain one exact UIA root.");
                Add(roots[0], null, 0);
                if (Present(windows[0], "elementCount"))
                    Require(AuditJson.Integer(windows[0], "elementCount") == Nodes.Count, "Inspected node count is inconsistent.");

                void Add(JsonElement value, Node? parent, int depth)
                {
                    Require(depth <= profile.TreeDepth && Nodes.Count < profile.MaxElements
                        && AuditJson.Boolean(value, "hasMoreChildren") != true,
                        "The inspected tree is truncated or exceeds the declared depth/element budget.");
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
                Require(SafeSelector(node.Selector) && Nodes.Count(item => item.Selector == node.Selector) == 1,
                    $"The selector for '{id}' is missing or ambiguous.");
                return node;
            }
        }
    }
}
