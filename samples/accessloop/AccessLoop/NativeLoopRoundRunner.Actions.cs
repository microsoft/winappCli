using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeLoopRoundRunner
{
    private sealed partial class RoundExecution
    {
        private async Task ExecuteStepAsync(LoopStep step, CancellationToken token)
        {
            var tree = await InspectAsync(token);
            Require(currentPage is not null, "The initial declared page has not been observed.");
            RequirePage(tree, step.Kind == "navigate" ? currentPage! : step.PageId);
            var page = profile.Pages.Single(item => item.Id == step.PageId);
            Control? control = null;
            if (step.TargetId is not null)
            {
                control = await ControlAsync(tree, step.TargetId, step.ControlType, token);
                action!.Before = control.Response;
            }
            else
                action!.Before = (await ControlAsync(tree, page.ReadyId, "Text", token)).Response;

            switch (step.Kind)
            {
                case "navigate":
                    Require(control is not null && step.TargetId == page.NavigationId
                        && step.ControlType is "TabItem" or "Button", "Navigation must use the declared tab/header ID and exact declared type.");
                    var navigationRoot = tree.Exact("MainNavigation");
                    Require(tree.Exact(page.NavigationId).Under(navigationRoot), "The declared navigation header is outside MainNavigation.");
                    RequireUsable(control!);
                    NoSettingPatterns(control!, allowSelection: step.ControlType == "TabItem");
                    if (step.ControlType == "TabItem") _ = Flag(control!.Properties, "IsSelected");
                    var navigated = await UiAsync(["invoke", control!.Selector], token, input: true);
                    ExactAction(navigated, control.Selector, step.ControlType == "TabItem" ? "SelectionItemPattern" : "InvokePattern");
                    tree = await WaitForPageAsync(page.Id, token);
                    currentPage = page.Id;
                    control = await ControlAsync(tree, page.NavigationId, step.ControlType, token);
                    RequireUsable(control);
                    if (step.ControlType == "TabItem")
                        Require(Flag(control.Properties, "IsSelected"), "The requested navigation tab is not selected.");
                    var preparation = Path.Combine(commandDirectory, "navigation-preparation.json");
                    AuditFiles.WriteNew(preparation, new
                    {
                        kind = "navigation-header-focus-preparation", pageId = page.Id, targetId = page.NavigationId,
                        note = "Explicitly prepares only the declared navigation header. This is not evidence of keyboard reachability."
                    });
                    Emit("navigation-preparation", step.Id, "Preparing the declared header only; content focus will not be repaired.");
                    var focused = await UiAsync(["focus", control.Selector], token, input: true);
                    ExactElement(focused, control.Selector);
                    tree = await InspectAsync(token);
                    RequirePage(tree, page.Id);
                    control = await ControlAsync(tree, page.NavigationId, step.ControlType, token);
                    await RequireFocusAsync(control, token);
                    break;
                case "key":
                    Require(control is not null && !string.IsNullOrWhiteSpace(step.ControlType), "A keyboard step requires a literal target ID/type.");
                    await RequireFocusAsync(control!, token);
                    var key = ReviewedKey(step.Keys);
                    var sent = await UiAsync(["send-keys", key, "--via", "send-input"], token, input: true);
                    Require(Text(sent, "keys") == key && Text(sent, "via") == "send-input"
                        && AuditJson.Integer(sent, "actionCount") == 1
                        && AuditJson.Long(sent, "hwnd") == target.WindowHandle && !Present(sent, "target")
                        && AuditJson.Array(sent, "warnings").GetArrayLength() == 0,
                        "Native input did not confirm one exact reviewed key in the original window without corrective focus.");
                    break;
                case "scroll":
                    Require(control is not null && step.VerticalPercent is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100,
                        "An explicit scroll step requires its own declared target and finite percentage.");
                    RequireUsable(control!);
                    Require(!Flag(control!.Properties, "HorizontallyScrollable") && Flag(control.Properties, "VerticallyScrollable"),
                        "Only the exact target's own finite vertical-only ScrollPattern is supported.");
                    var oldPercent = Numeric(control.Properties, "ScrollVerticalPercent");
                    var oldView = Numeric(control.Properties, "ScrollVerticalViewSize");
                    Require(oldPercent is >= 0 and <= 100 && oldView is > 0 and < 100, "The declared scroll body has no finite stable extent.");
                    var body = tree.Exact(control.Id);
                    Require(!tree.Nodes.Any(node => node != body && node.Scrollable && (node.Under(body) || body.Under(node))),
                        "Nested or ancestor scroll fallback is not supported.");
                    var scrolled = await UiAsync(["scroll", control.Selector, "--vertical-percent", Number(step.VerticalPercent!.Value)], token, input: true);
                    ExactElement(scrolled, control.Selector);
                    Require(scrolled.GetProperty("verticalPercent").ValueKind == JsonValueKind.Number
                        && scrolled.GetProperty("actualVerticalPercent").ValueKind == JsonValueKind.Number
                        && Near(Numeric(scrolled, "verticalPercent"), step.VerticalPercent.Value)
                        && Near(Numeric(scrolled, "actualVerticalPercent"), step.VerticalPercent.Value),
                        "The native scroll action did not confirm requested and actual percentages.");
                    tree = await InspectAsync(token);
                    RequirePage(tree, page.Id);
                    var afterScroll = await ControlAsync(tree, control.Id, control.Type, token);
                    Require(Near(Numeric(afterScroll.Properties, "ScrollVerticalPercent"), step.VerticalPercent.Value)
                        && Math.Abs(Numeric(afterScroll.Properties, "ScrollVerticalViewSize") - oldView) <= 0.05,
                        "The scroll position or content extent did not remain stable.");
                    break;
                case "expand-collapse":
                    Require(control is not null && step.ExpansionState is not null, "Expansion requires an exact declared control and state.");
                    RequireUsable(control!);
                    var state = step.ExpansionState!.ToLowerInvariant();
                    Require(state is "expanded" or "collapsed" && Text(control!.Properties, "ExpandCollapseState") is "Expanded" or "Collapsed",
                        "The target must expose its own known ExpandCollapsePattern.");
                    var expanded = await UiAsync(["expand-collapse", control!.Selector, "--state", state], token, input: true);
                    ExactAction(expanded, control.Selector, "ExpandCollapsePattern");
                    Require(Text(expanded, "state") == (state == "expanded" ? "Expanded" : "Collapsed"), "The native disclosure state does not match the request.");
                    tree = await InspectAsync(token);
                    RequirePage(tree, page.Id);
                    var afterExpansion = await ControlAsync(tree, control.Id, control.Type, token);
                    Require(Text(afterExpansion.Properties, "ExpandCollapseState") == Text(expanded, "state"),
                        "The actual disclosure state did not match its native action response.");
                    break;
                case "invoke":
                    Require(control is { Type: "Button" } && step.ControlType == "Button", "Invoke is restricted to an explicitly declared Button.");
                    RequireUsable(control!);
                    NoSettingPatterns(control!, allowSelection: false);
                    var invoked = await UiAsync(["invoke", control!.Selector], token, input: true);
                    ExactAction(invoked, control.Selector, "InvokePattern");
                    break;
                case "assert":
                    Require(step.Expectations.Count > 0, "A read-only assert step needs explicit expectations.");
                    break;
                case "scan":
                    await WaitExpectationsAsync(step, token);
                    await CaptureAsync(step, token);
                    await RecordAfterAsync(step, token);
                    return;
                default:
                    throw new InvalidDataException("The action kind is not part of the fixed route.");
            }
            var after = await InspectAsync(token);
            RequirePage(after, page.Id);
            await WaitExpectationsAsync(step, token);
            await RecordAfterAsync(step, token);
        }

        private static string ReviewedKey(string? keys)
        {
            var key = keys?.ToLowerInvariant();
            Require(key is "tab" or "shift+tab" or "space" or "enter" or "alt+down" or "escape" or "up" or "down" or "left" or "right"
                or "home" or "end" or "pageup" or "pagedown",
                "This preset prototype supports one reviewed navigation key/chord, never text, system keys, or arbitrary shortcuts.");
            return key!;
        }

        private static void NoSettingPatterns(Control control, bool allowSelection)
        {
            Require(!Present(control.Properties, "ToggleState") && !Present(control.Properties, "ExpandCollapseState")
                && !Present(control.Properties, "Value") && !Present(control.Properties, "Value.Value")
                && !Present(control.Properties, "IsReadOnly") && !Present(control.Properties, "SelectionAutomationIds")
                && (allowSelection || !Present(control.Properties, "IsSelected")),
                "The target exposes setting/value/selection semantics outside the authorized invocation pattern.");
        }

        private void ExactElement(JsonElement response, string selector) =>
            Require(AuditJson.Long(response, "hwnd") == target.WindowHandle && Text(response, "elementId") == selector,
                "The action response did not identify the original HWND and exact approved canonical selector.");
        private void ExactAction(JsonElement response, string selector, string pattern)
        {
            ExactElement(response, selector);
            Require(Text(response, "pattern") == pattern, "The native action used a different UIA pattern than the prescribed one.");
        }
    }
}
