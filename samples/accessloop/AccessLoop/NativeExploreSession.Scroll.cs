namespace AccessLoop;

public sealed partial class NativeExploreSession
{
    private sealed record ScrollPosition(Control Control, double Percent, double ViewSize, bool TopVisible, bool BottomVisible);

    private async Task ExploreScrollAsync(
        ExplorePage page, ExplorePageResult result, ExploreStateResult baseline, CancellationToken token)
    {
        var scroll = page.Scroll!;
        var interaction = result.Interactions.Single(item => item.Kind == "scroll");
        ScrollPosition? original = null;
        var changed = false;
        var endObserved = false;
        try
        {
            Require(scroll.MaxPositions is >= 1 and <= 100 && double.IsFinite(scroll.OverlapFraction)
                && scroll.OverlapFraction is > 0 and < 1, "The scroll position and viewport-overlap bounds are invalid.");
            var tree = await InspectAsync(target, token);
            RequirePage(tree, page.Id);
            original = await ScrollAsync(tree, page, scroll, token);
            interaction.Before = ScrollObservation(page, scroll, original);
            Require(baseline.After?.ScrollPercent is { } percent && Near(percent, original.Percent)
                && baseline.After.ViewSizePercent is { } view && Near(view, original.ViewSize, ViewSizeTolerance),
                "The scroll state changed since the verified base capture.");
            var position = original;
            if (!Near(position.Percent, 0))
            {
                RequireStateBudget();
                changed = true;
                position = await SetScrollAsync(page, scroll, position, 0, token);
            }
            Require(position.TopVisible, "The top anchor is not visibly inside the declared scroll container at its top.");
            var reusedBase = baseline.Status == "captured" && baseline.Before?.ScrollPercent is { } before
                && baseline.After?.ScrollPercent is { } after && Near(before, 0) && Near(after, 0)
                && Near(original.Percent, 0) && baseline.Before.ViewSizePercent is { } baseView
                && Near(baseView, position.ViewSize, ViewSizeTolerance);
            if (!reusedBase)
                RequireCaptured(await CaptureScrollAsync(page, scroll, position, token));
            var positions = 1;
            while (true)
            {
                if (Near(position.Percent, 100))
                {
                    Require(position.BottomVisible, "The bottom percentage was reached, but the declared bottom anchor is not visible.");
                    result.ScrollStatus = "end-observed";
                    endObserved = true;
                    return;
                }
                if (positions >= scroll.MaxPositions)
                    throw new ExploreBudgetException("The maximum scroll positions were captured without observing the bottom anchor at a stable end position.");
                RequireStateBudget();
                // UIA percent is relative to the scrollable remainder, not the full content height.
                var step = 100 * position.ViewSize / (100 - position.ViewSize) * (1 - scroll.OverlapFraction);
                Require(double.IsFinite(step) && step > PercentTolerance, "The scroll step cannot provide observable progress with the declared viewport overlap.");
                var next = Math.Min(100, position.Percent + step);
                changed = true;
                var moved = await SetScrollAsync(page, scroll, position, next, token);
                Require(moved.Percent > position.Percent + PercentTolerance, "The declared scroller made no observable progress; no wheel or nested-scroll fallback was attempted.");
                RequireCaptured(await CaptureScrollAsync(page, scroll, moved, token));
                position = moved;
                positions++;
            }
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            result.ScrollStatus = ex is ExploreBudgetException ? "limit-reached" : "blocked";
            result.ScrollReason = Error(ex);
            interaction.Status = "blocked";
            interaction.Reason = Error(ex);
            if (monitor.Violation is not null || ex is OperationCanceledException)
                throw;
        }
        finally
        {
            if (original is not null)
                interaction.Restoration = await RestoreScrollAsync(page, scroll, original, changed, interaction);
            else
                interaction.Restoration = new("not-attempted", "No valid original ScrollPattern state was established; no scrolling was authorized.");
            if (interaction.Restoration.Status is not ("restored-scroll-position" or "unchanged"))
            {
                interaction.Status = "blocked";
                result.ScrollStatus = "blocked";
                result.ScrollReason ??= interaction.Restoration.Message;
            }
            else if (endObserved)
                interaction.Status = "captured";
        }
    }

    private Task<ExploreStateResult> CaptureScrollAsync(ExplorePage page, ExploreScroll scroll, ScrollPosition expected, CancellationToken token) =>
        CaptureAsync(page, "scroll", $"{page.Name}: scroll {Number(expected.Percent)}%", target, async ct =>
        {
            var tree = await InspectAsync(target, ct);
            RequirePage(tree, page.Id);
            RequireNoDialogs(tree, page, null);
            var actual = await ScrollAsync(tree, page, scroll, ct);
            Require(Near(expected.Percent, actual.Percent) && Near(expected.ViewSize, actual.ViewSize, ViewSizeTolerance),
                "The scroll extent or position changed before/after the state capture.");
            Require(!Near(expected.Percent, 0) || actual.TopVisible, "The top anchor disappeared during the top-state capture.");
            Require(!Near(expected.Percent, 100) || actual.BottomVisible, "The bottom anchor disappeared during the bottom-state capture.");
            return Remember(ScrollObservation(page, scroll, actual), new Dictionary<string, string>
            {
                ["topAnchorVisible"] = actual.TopVisible.ToString(),
                ["bottomAnchorVisible"] = actual.BottomVisible.ToString()
            });
        }, token);

    private async Task<ScrollPosition> ScrollAsync(Tree tree, ExplorePage page, ExploreScroll scroll, CancellationToken token)
    {
        var node = tree.Exact(scroll.ContainerId);
        Require(!node.Offscreen, "The declared scroll container is not visible.");
        Require(!tree.Nodes.Any(item => item != node && item.Scrollable && (item.Under(node) || node.Under(item))),
            "Nested scroll containers are unsupported; a wheel or ancestor fallback would not preserve declared scope.");
        var top = tree.Exact(scroll.TopAnchorId);
        var bottom = tree.Exact(scroll.BottomAnchorId);
        Require(top.Under(node) && bottom.Under(node), "Both scroll anchors must belong to the declared finite container.");
        Require(!tree.Exact(page.ReadyId).Under(node), "The page-ready heading must remain outside the scrolling content.");
        var control = await ConfirmAsync(node, target, token);
        Require(!PropertyBool(control.Properties, "IsOffscreen") && PropertyBool(control.Properties, "IsEnabled")
            && !PropertyBool(control.Properties, "HorizontallyScrollable") && PropertyBool(control.Properties, "VerticallyScrollable"),
            "Only a visible, enabled, finite vertical-only control exposing its own ScrollPattern is supported.");
        var percent = PropertyNumber(control.Properties, "ScrollVerticalPercent");
        var view = PropertyNumber(control.Properties, "ScrollVerticalViewSize");
        Require(percent is >= 0 and <= 100 && view is > 0 and < 100,
            "A stable finite vertical ScrollPattern percentage and viewport size are required.");
        var observed = new ScrollPosition(control, percent, view, !top.Offscreen, !bottom.Offscreen);
        Remember(ScrollObservation(page, scroll, observed));
        return observed;
    }

    private async Task<ScrollPosition> SetScrollAsync(
        ExplorePage page, ExploreScroll scroll, ScrollPosition before, double percent, CancellationToken token)
    {
        var currentTree = await InspectAsync(target, token);
        RequirePage(currentTree, page.Id);
        var current = await ScrollAsync(currentTree, page, scroll, token);
        Require(Near(current.Percent, before.Percent) && Near(current.ViewSize, before.ViewSize, ViewSizeTolerance),
            "Scroll state changed before the explicitly authorized percentage action.");
        var action = await UiAsync(target, ["scroll", current.Control.Selector, "--vertical-percent", Number(percent)], token, input: true);
        Require(AuditJson.Long(action, "hwnd") == target.WindowHandle && Text(action, "elementId") == current.Control.Selector
            && Near(PropertyNumber(action, "verticalPercent"), percent)
            && Near(PropertyNumber(action, "actualVerticalPercent"), percent),
            "The explicit ScrollPattern action did not confirm the exact HWND, selector, requested and actual percentages.");
        ScrollPosition? previous = null;
        for (var poll = 0; poll < 20; poll++)
        {
            var tree = await InspectAsync(target, token);
            RequirePage(tree, page.Id);
            var actual = await ScrollAsync(tree, page, scroll, token);
            Require(Near(actual.ViewSize, before.ViewSize, ViewSizeTolerance),
                "The content extent changed while scrolling; virtualized or changing extents cannot count as end coverage.");
            if (Near(actual.Percent, percent) && previous is not null && Near(actual.Percent, previous.Percent))
                return actual;
            previous = actual;
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("The scroller did not settle at the requested percentage; no progress or unsupported provider behavior was observed.");
    }

    private async Task<SurveyRestoration> RestoreScrollAsync(
        ExplorePage page, ExploreScroll scroll, ScrollPosition original, bool changed, ExploreInteractionResult interaction)
    {
        if (monitor.Violation is not null)
            return new("not-attempted", "A latched boundary stop forbids scroll restoration.");
        var priorCleanup = cleaningUp;
        cleaningUp = true;
        cleanupCommands = 0;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var tree = await InspectAsync(target, cleanup.Token);
            RequirePage(tree, page.Id);
            var current = await ScrollAsync(tree, page, scroll, cleanup.Token);
            Require(Near(current.ViewSize, original.ViewSize, ViewSizeTolerance),
                "The original content extent is no longer stable; percentage restoration would not restore the known viewport.");
            if (!Near(current.Percent, original.Percent))
                current = await SetScrollAsync(page, scroll, current, original.Percent, cleanup.Token);
            interaction.After = ScrollObservation(page, scroll, current);
            Require(Near(current.Percent, original.Percent), "The original scroll percentage could not be restored within tolerance.");
            return new(changed ? "restored-scroll-position" : "unchanged",
                $"Original vertical percentage {Number(original.Percent)} verified within {Number(PercentTolerance)} percentage points. Exact pixel/focus restoration is not claimed.");
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex)) { return new("incomplete", Error(ex)); }
        finally { cleaningUp = priorCleanup; }
    }

    private ExploreObservation ScrollObservation(ExplorePage page, ExploreScroll scroll, ScrollPosition position) =>
        Observation(page) with { ControlId = scroll.ContainerId, ScrollPercent = position.Percent, ViewSizePercent = position.ViewSize };

    private static void RequireCaptured(ExploreStateResult state) =>
        Require(state.Status == "captured", state.Error ?? "The required state was not verified as captured.");
}
