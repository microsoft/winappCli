using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeExploreSession
{
    private async Task ExploreDropdownAsync(
        ExplorePage page, ExploreDropdown dropdown, ExploreInteractionResult result, CancellationToken token)
    {
        IReadOnlyList<string>? selection = null;
        var triggered = false;
        var captured = false;
        AuditTarget popup = target;
        try
        {
            var before = await ReadDropdownAsync(page, dropdown, "Collapsed", null, target, token);
            result.Before = before.Observation;
            selection = before.Observation.SelectionAutomationIds;
            var tree = await InspectAsync(target, token);
            RequirePage(tree, page.Id);
            var control = await ComboAsync(tree, dropdown, token);
            Require(Selection(control, dropdown).SequenceEqual(selection, StringComparer.Ordinal)
                && Text(control.Properties, "ExpandCollapseState") == "Collapsed", "The dropdown changed before expansion.");
            var action = await UiAsync(target, ["expand-collapse", control.Selector, "--state", "expanded"], token,
                input: true, popup: true, issuing: () => { triggered = true; overlayOpen = true; });
            ExactAction(action, target, control.Selector, "ExpandCollapsePattern");
            Require(Text(action, "state") == "Expanded", "The native expansion action did not report Expanded.");
            await WaitForAsync(async ct =>
            {
                popup = PopupTarget();
                return await ReadDropdownAsync(page, dropdown, "Expanded", selection, popup, ct);
            }, token);
            monitor.ApprovePopup(popup.WindowHandle == target.WindowHandle ? null : popup.WindowHandle);
            RequireCaptured(await CaptureAsync(page, "dropdown", $"{page.Name}: {dropdown.ControlId} expanded", popup,
                ct => ReadDropdownAsync(page, dropdown, "Expanded", selection, popup, ct), token));
            captured = true;
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            result.Status = "blocked";
            result.Reason = Error(ex);
            if (monitor.Violation is not null || ex is OperationCanceledException or ExploreBudgetException) throw;
        }
        finally
        {
            result.Restoration = triggered && selection is not null
                ? await CollapseAsync(page, dropdown, selection, result)
                : new("not-attempted", "No expansion was authorized; the originally observed selection was not changed.");
            if (triggered && result.Restoration.Status != "restored-control")
            {
                result.Status = "blocked";
                result.Reason ??= result.Restoration.Message;
            }
            else if (captured)
                result.Status = "captured";
        }
    }

    private async Task<Control> ComboAsync(Tree tree, ExploreDropdown dropdown, CancellationToken token)
    {
        var node = tree.Exact(dropdown.ControlId);
        Require(node.Type == "ComboBox" && !node.Offscreen, "Only the exact declared visible ComboBox can be expanded.");
        var control = await ConfirmAsync(node, target, token);
        Require(PropertyBool(control.Properties, "IsEnabled") && !PropertyBool(control.Properties, "IsOffscreen")
            && Text(control.Properties, "ExpandCollapseState") is "Collapsed" or "Expanded",
            "The declared ComboBox must expose its own usable ExpandCollapsePattern.");
        return control;
    }

    private static IReadOnlyList<string> Selection(Control control, ExploreDropdown dropdown)
    {
        Require(control.Properties.TryGetProperty("SelectionAutomationIds", out var selection)
            && selection.ValueKind == JsonValueKind.Array && selection.GetArrayLength() == 1,
            "The dropdown selection must expose exactly one stable SelectionAutomationIds entry.");
        var id = selection[0].ValueKind == JsonValueKind.String ? selection[0].GetString() : null;
        Require(!string.IsNullOrWhiteSpace(id) && dropdown.ItemIds.Count(item => item == id) == 1
            && dropdown.ItemIds.Distinct(StringComparer.Ordinal).Count() == dropdown.ItemIds.Count,
            "The selected stable AutomationId is missing, ambiguous or outside the declared item set.");
        return [id!];
    }

    private async Task<Reading> ReadDropdownAsync(
        ExplorePage page, ExploreDropdown dropdown, string expansion, IReadOnlyList<string>? selection,
        AuditTarget popup, CancellationToken token)
    {
        var tree = await InspectAsync(target, token);
        RequirePage(tree, page.Id);
        RequireNoDialogs(tree, page, null);
        var control = await ComboAsync(tree, dropdown, token);
        var actual = Selection(control, dropdown);
        var observed = Remember(Observation(page, popup) with
        {
            ControlId = dropdown.ControlId, ExpansionState = Text(control.Properties, "ExpandCollapseState"), SelectionAutomationIds = actual
        }, new Dictionary<string, string> { ["expectedItems"] = string.Join("|", dropdown.ItemIds) });
        Require(selection is null || selection.SequenceEqual(actual, StringComparer.Ordinal), "The original dropdown selection changed; no option-selection actions are authorized.");
        Require(Text(control.Properties, "ExpandCollapseState") == expansion, $"The dropdown is not in the expected {expansion} state.");
        if (expansion == "Expanded")
        {
            var items = popup.WindowHandle == target.WindowHandle ? tree : await InspectAsync(popup, token);
            foreach (var id in dropdown.ItemIds)
            {
                var item = items.Exact(id);
                Require(item.Type == "ListItem" && !item.Offscreen, $"Expected visible dropdown item '{id}' was not observed in the exact popup scope.");
                var confirmed = await ConfirmAsync(item, popup, token);
                Require(!PropertyBool(confirmed.Properties, "IsOffscreen"), $"Dropdown item '{id}' became hidden.");
            }
        }
        return observed;
    }

    private async Task<SurveyRestoration> CollapseAsync(
        ExplorePage page, ExploreDropdown dropdown, IReadOnlyList<string> selection, ExploreInteractionResult result)
    {
        if (monitor.Violation is not null)
            return new("not-attempted", "The scope guard is latched; no collapse or selection-restoration action was sent.");
        var previous = cleaningUp;
        cleaningUp = true;
        cleanupCommands = 0;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var tree = await InspectAsync(target, cleanup.Token);
            RequirePage(tree, page.Id);
            var control = await ComboAsync(tree, dropdown, cleanup.Token);
            Require(Selection(control, dropdown).SequenceEqual(selection, StringComparer.Ordinal), "Selection changed; rollback by selecting an option is forbidden.");
            // If same-window item verification failed, only this already-authorized ComboBox can be collapsed.
            if (monitor.Provisional && monitor.PopupWindow is null)
                monitor.ApprovePopup(null);
            var action = await UiAsync(target, ["expand-collapse", control.Selector, "--state", "collapsed"], cleanup.Token, input: true, closePopup: true);
            ExactAction(action, target, control.Selector, "ExpandCollapsePattern");
            Require(Text(action, "state") == "Collapsed", "The native collapse action did not report Collapsed.");
            var restored = await WaitForAsync(ct => ReadDropdownAsync(page, dropdown, "Collapsed", selection, target, ct), cleanup.Token);
            monitor.EndPopup();
            overlayOpen = false;
            result.After = restored.Observation;
            return new("restored-control", "Collapsed the originally authorized ComboBox and verified the unchanged original selection and page. No options or keys were selected.");
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex)) { return new("incomplete", Error(ex)); }
        finally { cleaningUp = previous; }
    }

    private async Task ExploreDialogAsync(
        ExplorePage page, ExploreDialog dialog, ExploreInteractionResult result, CancellationToken token)
    {
        var triggered = false;
        var captured = false;
        try
        {
            Require(dialog.Scope == "same-window",
                "Owned-window dialogs are unsupported by this executor: the existing native command contract requires its HWND to survive post-action verification. No opener or dismissal fallback was invoked.");
            var tree = await InspectAsync(target, token);
            RequirePage(tree, page.Id);
            RequireNoDialogs(tree, page, null);
            result.Before = Observation(page) with
            {
                ControlId = dialog.RootId, DialogPresent = false, FocusAutomationId = await FocusAsync(target, token)
            };
            var opener = await ButtonAsync(tree, dialog.OpenerId, token);
            var action = await UiAsync(target, ["invoke", opener.Selector], token, input: true,
                issuing: () => { triggered = true; overlayOpen = true; });
            ExactAction(action, target, opener.Selector, "InvokePattern");
            await WaitForAsync(ct => ReadDialogAsync(page, dialog, ct), token);
            RequireCaptured(await CaptureAsync(page, "dialog", $"{page.Name}: {dialog.RootId} open", target,
                ct => ReadDialogAsync(page, dialog, ct), token));
            captured = true;
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            result.Status = "blocked";
            result.Reason = Error(ex);
            if (monitor.Violation is not null || ex is OperationCanceledException or ExploreBudgetException) throw;
        }
        finally
        {
            result.Restoration = triggered
                ? await DismissAsync(page, dialog, result)
                : new("not-attempted", "No dialog was opened.");
            if (triggered && result.Restoration.Status != "restored-control")
            {
                result.Status = "blocked";
                result.Reason ??= result.Restoration.Message;
            }
            else if (captured)
                result.Status = "captured";
        }
    }

    private async Task<Reading> ReadDialogAsync(ExplorePage page, ExploreDialog dialog, CancellationToken token)
    {
        var tree = await InspectAsync(target, token);
        RequirePage(tree, page.Id);
        RequireNoDialogs(tree, page, dialog.RootId);
        Remember(Observation(page) with
        {
            ControlId = dialog.RootId, DialogPresent = tree.Matches(dialog.RootId).Any(node => !node.Offscreen)
        });
        var root = tree.Exact(dialog.RootId);
        Require(!root.Offscreen, "The unique declared dialog root is not visible.");
        var marker = await ConfirmAsync(root, target, token);
        Require(!PropertyBool(marker.Properties, "IsOffscreen"), "The declared dialog marker is hidden.");
        var dismiss = tree.Exact(dialog.DismissId);
        Require(dismiss.Under(root), "The named dismissal button must belong to the exact declared dialog.");
        _ = await ButtonAsync(tree, dialog.DismissId, token);
        return Remember(Observation(page) with
        {
            ControlId = dialog.RootId, DialogPresent = true, FocusAutomationId = await FocusAsync(target, token)
        }, new Dictionary<string, string> { ["dialogRoot"] = dialog.RootId, ["dismissId"] = dialog.DismissId });
    }

    private async Task<SurveyRestoration> DismissAsync(
        ExplorePage page, ExploreDialog dialog, ExploreInteractionResult result)
    {
        if (monitor.Violation is not null)
            return new("not-attempted", "A latched scope stop forbids dismissal, focus restoration or underlying-page input.");
        var previous = cleaningUp;
        cleaningUp = true;
        cleanupCommands = 0;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            _ = await ReadDialogAsync(page, dialog, cleanup.Token);
            var tree = await InspectAsync(target, cleanup.Token);
            var dismiss = await ButtonAsync(tree, dialog.DismissId, cleanup.Token);
            Require(tree.Exact(dialog.DismissId).Under(tree.Exact(dialog.RootId)), "Dialog ancestry changed before dismissal.");
            var action = await UiAsync(target, ["invoke", dismiss.Selector], cleanup.Token, input: true);
            ExactAction(action, target, dismiss.Selector, "InvokePattern");
            var closed = await WaitForAsync(async ct =>
            {
                var main = await InspectAsync(target, ct);
                RequirePage(main, page.Id);
                RequireNoDialogs(main, page, null);
                Require(monitor.PopupWindow is null, "The exact owned dialog HWND remains visible.");
                return new Reading(Observation(page) with
                {
                    ControlId = dialog.RootId, DialogPresent = false, FocusAutomationId = await FocusAsync(target, ct)
                }, new Dictionary<string, string>());
            }, cleanup.Token);
            monitor.EndPopup();
            overlayOpen = false;
            result.After = closed.Observation;
            return new("restored-control", "The named dialog was dismissed and the page reverified. Before/open/after focus was observed, never forced; focus restoration is not assumed.");
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex)) { return new("incomplete", Error(ex)); }
        finally { cleaningUp = previous; }
    }

    private AuditTarget PopupTarget()
    {
        monitor.Check(target);
        if (monitor.PopupWindow is { } hwnd)
        {
            monitor.Stop("window-boundary-changed",
                "This exploration profile authorizes in-window dropdown states only. A separate native popup cannot be safely included; no further input or automatic collapse was sent.", hwnd);
            throw new ExploreStoppedException(monitor.Violation!.Message);
        }
        return target;
    }

    private async Task<Reading> WaitForAsync(Func<CancellationToken, Task<Reading>> observe, CancellationToken token)
    {
        string? reason = null;
        for (var poll = 0; poll < 20; poll++)
        {
            try { return await observe(token); }
            catch (InvalidOperationException ex) when (ex is not (ExploreStoppedException or ExploreBudgetException) && monitor.Violation is null)
            {
                reason = ex.Message;
            }
            await Task.Delay(100, token);
        }
        throw new TimeoutException("The declared state did not become verifiable within the bounded observation period. " + reason);
    }
}
