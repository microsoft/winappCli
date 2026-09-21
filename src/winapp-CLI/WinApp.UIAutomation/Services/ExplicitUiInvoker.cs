// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

// This boundary keeps routing and toggle transitions testable without implementing a COM provider.
// Implementations must throw when a matching pattern is unavailable; never try another pattern.
internal interface IExplicitUiInvokePatterns
{
    void Invoke();
    void Select();
    void Toggle();
    ToggleState ReadToggleState();
    void Expand();
    void Collapse();
}

internal static class ExplicitUiInvoker
{
    private const int UiaElementNotAvailable = unchecked((int)0x80040201);

    internal static (string Pattern, string Action) Describe(UiInvokeAction action) => action switch
    {
        UiInvokeAction.Invoke => ("InvokePattern", "invoke"),
        UiInvokeAction.Select => ("SelectionItemPattern", "select"),
        UiInvokeAction.Toggle => ("TogglePattern", "toggle"),
        UiInvokeAction.ToggleOn => ("TogglePattern", "toggle-on"),
        UiInvokeAction.ToggleOff => ("TogglePattern", "toggle-off"),
        UiInvokeAction.Expand => ("ExpandCollapsePattern", "expand"),
        UiInvokeAction.Collapse => ("ExpandCollapsePattern", "collapse"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown UI invoke action."),
    };

    internal static UiInvokeActionResult Apply(
        IExplicitUiInvokePatterns patterns, UiElement element, UiInvokeAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (pattern, performedAction) = Describe(action);
        try
        {
            switch (action)
            {
                case UiInvokeAction.Invoke:
                    patterns.Invoke();
                    break;
                case UiInvokeAction.Select:
                    patterns.Select();
                    break;
                case UiInvokeAction.Toggle:
                    patterns.Toggle();
                    break;
                case UiInvokeAction.ToggleOn:
                case UiInvokeAction.ToggleOff:
                    var desired = action == UiInvokeAction.ToggleOn ? ToggleState.ToggleState_On : ToggleState.ToggleState_Off;
                    var initial = ReadToggleState(patterns);
                    if (initial == desired)
                    {
                        performedAction = "none";
                        break;
                    }

                    var maxToggles = initial == ToggleState.ToggleState_Indeterminate ? 2 : 1;
                    var current = initial;
                    for (var attempt = 0; attempt < maxToggles; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        patterns.Toggle();
                        current = ReadToggleState(patterns);
                        if (current == desired)
                        {
                            break;
                        }
                    }

                    if (current != desired)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} ({element.Type}) did not reach the requested " +
                            $"{desired} state for '{performedAction}' through {pattern} after {maxToggles} toggle(s); reported {current}.");
                    }
                    performedAction = "toggle";
                    break;
                case UiInvokeAction.Expand:
                    patterns.Expand();
                    break;
                case UiInvokeAction.Collapse:
                    patterns.Collapse();
                    break;
            }
        }
        // Preserve the stale-element signal; unsupported patterns and other provider failures
        // are operation errors, not a reason to re-resolve or fall back to an ancestor.
        catch (COMException ex) when (ex.HResult != UiaElementNotAvailable)
        {
            throw new InvalidOperationException(
                $"Action '{performedAction}' through {pattern} failed on element {element.Selector ?? element.Id} ({element.Type}): {ex.Message}", ex);
        }

        return new(pattern, performedAction);
    }

    private static ToggleState ReadToggleState(IExplicitUiInvokePatterns patterns)
    {
        var state = patterns.ReadToggleState();
        return state switch
        {
            ToggleState.ToggleState_On or ToggleState.ToggleState_Off or ToggleState.ToggleState_Indeterminate => state,
            _ => throw new InvalidOperationException($"TogglePattern reported an invalid toggle state: {(int)state}."),
        };
    }
}

internal sealed class ComExplicitUiInvokePatterns(IUIAutomationElement element, UiElement model) : IExplicitUiInvokePatterns
{
    // Retain the same TogglePattern for all reads and writes of this one operation.
    private IUIAutomationTogglePattern? _toggle;
    private IUIAutomationTogglePattern TogglePattern =>
        _toggle ??= GetPattern<IUIAutomationTogglePattern>(UIA_PATTERN_ID.UIA_TogglePatternId, "TogglePattern");

    public void Invoke() => GetPattern<IUIAutomationInvokePattern>(UIA_PATTERN_ID.UIA_InvokePatternId, "InvokePattern").Invoke();
    public void Select() => GetPattern<IUIAutomationSelectionItemPattern>(UIA_PATTERN_ID.UIA_SelectionItemPatternId, "SelectionItemPattern").Select();
    public void Toggle() => TogglePattern.Toggle();
    public ToggleState ReadToggleState() => TogglePattern.get_CurrentToggleState();
    public void Expand() => GetPattern<IUIAutomationExpandCollapsePattern>(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, "ExpandCollapsePattern").Expand();
    public void Collapse() => GetPattern<IUIAutomationExpandCollapsePattern>(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, "ExpandCollapsePattern").Collapse();

    private T GetPattern<T>(UIA_PATTERN_ID id, string name) where T : class
    {
        var pattern = element.GetCurrentPattern(id) as T;
        return pattern ?? throw new InvalidOperationException(
            $"Element {model.Selector ?? model.Id} ({model.Type}) does not support {name}. No other pattern or ancestor was tried.");
    }
}
