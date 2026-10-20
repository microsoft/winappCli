// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    private const int StatePollIntervalMs = 50;
    private const int StateSettleTimeoutMs = 2000;
    private const double ScrollPercentTolerance = 0.01;
    private const int MaxSelectionAutomationIds = 256;
    private const int MaxSelectionAutomationIdLength = 1024;
    private const int UiaNotSupported = unchecked((int)0x80040204);

    public async Task<UiExpandCollapseState> SetExpandCollapseStateAsync(
        UiTarget uiTarget, UiElement element, UiExpandCollapseState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state is not (UiExpandCollapseState.Expanded or UiExpandCollapseState.Collapsed))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Use Expanded or Collapsed.");
        }

        var target = ResolveStateTarget(uiTarget, element);
        var pattern = GetRequiredPattern<IUIAutomationExpandCollapsePattern>(
            target, element, UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, "ExpandCollapsePattern");
        var expected = state == UiExpandCollapseState.Expanded
            ? ExpandCollapseState.ExpandCollapseState_Expanded
            : ExpandCollapseState.ExpandCollapseState_Collapsed;

        ExpandCollapseState ReadState()
        {
            EnsureStateTargetEnabled(target, element);
            var current = pattern.get_CurrentExpandCollapseState();
            if (current is not (ExpandCollapseState.ExpandCollapseState_Expanded
                or ExpandCollapseState.ExpandCollapseState_Collapsed
                or ExpandCollapseState.ExpandCollapseState_PartiallyExpanded))
            {
                throw new InvalidOperationException(
                    $"Element {element.Selector ?? element.Id} has unsupported ExpandCollapseState '{current}'.");
            }
            return current;
        }

        if (ReadState() != expected)
        {
            ct.ThrowIfCancellationRequested();
            if (state == UiExpandCollapseState.Expanded)
            {
                pattern.Expand();
            }
            else
            {
                pattern.Collapse();
            }
        }

        await ConfirmPatternStateAsync(ReadState, current => current == expected,
            $"ExpandCollapsePattern did not confirm {state} for element {element.Selector ?? element.Id}", ct);
        return state;
    }

    public async Task<double> SetVerticalScrollPercentAsync(
        UiTarget uiTarget, UiElement element, double verticalPercent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(verticalPercent) || verticalPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalPercent), "Use a finite percentage from 0 to 100.");
        }

        var target = ResolveStateTarget(uiTarget, element);
        var pattern = GetRequiredPattern<IUIAutomationScrollPattern>(
            target, element, UIA_PATTERN_ID.UIA_ScrollPatternId, "ScrollPattern");
        if (!pattern.get_CurrentVerticallyScrollable())
        {
            throw new InvalidOperationException($"Element {element.Selector ?? element.Id} itself cannot scroll vertically.");
        }

        var horizontalBefore = pattern.get_CurrentHorizontalScrollPercent();
        if (!double.IsFinite(horizontalBefore) || (horizontalBefore != -1 && horizontalBefore is < 0 or > 100))
        {
            throw new InvalidOperationException("ScrollPattern returned an invalid horizontal percentage.");
        }

        double ReadVerticalPercent()
        {
            EnsureStateTargetEnabled(target, element);
            var horizontal = pattern.get_CurrentHorizontalScrollPercent();
            if (!double.IsFinite(horizontal) || Math.Abs(horizontal - horizontalBefore) > ScrollPercentTolerance)
            {
                throw new InvalidOperationException("ScrollPattern changed the horizontal position during vertical scrolling.");
            }
            var current = pattern.get_CurrentVerticalScrollPercent();
            if (!double.IsFinite(current) || current is < 0 or > 100)
            {
                throw new InvalidOperationException("ScrollPattern returned an invalid vertical percentage.");
            }
            return current;
        }

        bool IsRequested(double current) => Math.Abs(current - verticalPercent) <= ScrollPercentTolerance;

        if (!IsRequested(ReadVerticalPercent()))
        {
            ct.ThrowIfCancellationRequested();
            pattern.SetScrollPercent(-1, verticalPercent);
        }

        return await ConfirmPatternStateAsync(ReadVerticalPercent, IsRequested,
            $"ScrollPattern did not confirm vertical percentage {verticalPercent.ToString(CultureInfo.InvariantCulture)} " +
            $"for element {element.Selector ?? element.Id}", ct);
    }

    private async Task<T> ConfirmPatternStateAsync<T>(
        Func<T> read, Func<T, bool> matches, string failure, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var consecutiveMatches = 0;
        T? current = default;
        for (int attempt = 0; attempt <= StateSettleTimeoutMs / StatePollIntervalMs; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            current = read();
            ct.ThrowIfCancellationRequested();
            consecutiveMatches = matches(current) ? consecutiveMatches + 1 : 0;
            if (consecutiveMatches >= 2)
            {
                return current;
            }
            if (attempt == StateSettleTimeoutMs / StatePollIntervalMs
                || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= StateSettleTimeoutMs)
            {
                break;
            }
            await _pollDelay.DelayAsync(StatePollIntervalMs, ct);
        }
        throw new TimeoutException($"{failure} within {StateSettleTimeoutMs} ms. Last observed: {current}.");
    }

    private IUIAutomationElement ResolveStateTarget(UiTarget uiTarget, UiElement element)
    {
        var target = ResolveComElement(uiTarget, element, exactTarget: true)
            ?? throw new InvalidOperationException(
                $"Element {element.Selector ?? element.Id} is stale. Re-run 'inspect' or 'search'.");
        EnsureStateTargetEnabled(target, element);
        return target;
    }

    private static void EnsureStateTargetEnabled(IUIAutomationElement target, UiElement element)
    {
        if (!target.get_CurrentIsEnabled())
        {
            throw new InvalidOperationException($"Element {element.Selector ?? element.Id} is disabled.");
        }
    }

    private static T GetRequiredPattern<T>(
        IUIAutomationElement target, UiElement element, UIA_PATTERN_ID id, string name) where T : class
    {
        var message = $"Element {element.Selector ?? element.Id} itself does not support {name}; no fallback is attempted.";
        try
        {
            return target.GetCurrentPattern(id) as T ?? throw new NotSupportedException(message);
        }
        catch (COMException ex) when (ex.HResult == UiaNotSupported)
        {
            throw new NotSupportedException(message, ex);
        }
    }

    private static IUIAutomationElement? FindUniqueComElement(
        IUIAutomationElement root, IUIAutomationCondition condition, UiElement element)
    {
        var matches = root.FindAll(TreeScope.TreeScope_Subtree, condition);
        var count = matches.get_Length();
        if (count > 1)
        {
            throw new UiAmbiguousSelectorException(
                $"Element {element.Selector ?? element.Id} now matches {count} elements. Re-run 'inspect' for an exact selector.");
        }
        return count == 1 ? matches.GetElement(0) : null;
    }

    private static string[] ReadSelectionAutomationIds(IUIAutomationSelectionPattern pattern)
    {
        var selection = pattern.GetCurrentSelection();
        var count = selection.get_Length();
        if (count is < 0 or > MaxSelectionAutomationIds)
        {
            throw new InvalidOperationException($"Selection exceeds the {MaxSelectionAutomationIds}-item metadata limit.");
        }

        var ids = new string[count];
        for (int i = 0; i < count; i++)
        {
            var id = selection.GetElement(i).get_CurrentAutomationId();
            try
            {
                if (id.Length > MaxSelectionAutomationIdLength)
                {
                    throw new InvalidOperationException("Selected AutomationId exceeds the metadata length limit.");
                }
                ids[i] = id.ToString();
            }
            finally
            {
                global::Windows.Win32.PInvoke.SysFreeString(id);
            }
        }
        return ids;
    }
}
