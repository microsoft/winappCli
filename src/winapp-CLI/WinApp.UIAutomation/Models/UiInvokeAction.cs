// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>An explicit UIA action, performed only through its matching pattern on the supplied element.</summary>
public enum UiInvokeAction
{
    /// <summary>Invoke through InvokePattern.</summary>
    Invoke,
    /// <summary>Select through SelectionItemPattern.</summary>
    Select,
    /// <summary>Toggle exactly once through TogglePattern.</summary>
    Toggle,
    /// <summary>Ensure TogglePattern reports On, without changing an already-On element.</summary>
    ToggleOn,
    /// <summary>Ensure TogglePattern reports Off, without changing an already-Off element.</summary>
    ToggleOff,
    /// <summary>Expand through ExpandCollapsePattern.</summary>
    Expand,
    /// <summary>Collapse through ExpandCollapsePattern.</summary>
    Collapse,
}

/// <summary>The matching UIA pattern and the action actually performed.</summary>
/// <param name="Pattern">The pattern name, such as <c>TogglePattern</c>.</param>
/// <param name="PerformedAction">The lowercase action performed (<c>toggle</c> for a changed toggle-on/off request), or <c>none</c> when the toggle state was already correct.</param>
public sealed record UiInvokeActionResult(string Pattern, string PerformedAction);
