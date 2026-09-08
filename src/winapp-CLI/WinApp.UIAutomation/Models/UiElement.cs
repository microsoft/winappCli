// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Represents a UI element discovered via UIA inspection.
/// </summary>
public sealed class UiElement
{
    /// <summary>Synthetic walk-order id (e0, e1, ...). Useful in flat result lists; null on nested inspect output where elements are addressed via tree position + selector.</summary>
    public string? Id { get; set; }
    /// <summary>The element's UIA control type, such as "Button" or "Edit".</summary>
    public string Type { get; set; } = "";

    /// <summary>The element's accessible name, which is usually its visible label.</summary>
    public string? Name { get; set; }

    /// <summary>The app-assigned automation id, stable across runs when the app sets one.</summary>
    public string? AutomationId { get; set; }

    /// <summary>The element's UIA class name.</summary>
    public string? ClassName { get; set; }

    /// <summary><see langword="true"/> when the element accepts input.</summary>
    public bool IsEnabled { get; set; }

    /// <summary><see langword="true"/> when the element is scrolled out of view or otherwise not on screen.</summary>
    public bool IsOffscreen { get; set; }

    /// <summary>Left edge of the element in screen coordinates.</summary>
    public double X { get; set; }

    /// <summary>Top edge of the element in screen coordinates.</summary>
    public double Y { get; set; }

    /// <summary>Width of the element in pixels.</summary>
    public double Width { get; set; }

    /// <summary>Height of the element in pixels.</summary>
    public double Height { get; set; }

    /// <summary>Child elements, when the result is a nested tree. <see langword="null"/> on flat result lists.</summary>
    public UiElement[]? Children { get; set; }

    /// <summary>Stable semantic slug that uniquely identifies this element (e.g., "btn-minimize-d1a0").</summary>
    public string? Selector { get; set; }

    /// <summary>Current text value for editable elements (from ValuePattern). Null if not editable or empty.</summary>
    public string? Value { get; set; }

    /// <summary>Toggle state for checkboxes/toggles: "on", "off", "indeterminate". Null if not a toggle.</summary>
    public string? ToggleState { get; set; }

    /// <summary>Expand/collapse state: "expanded", "collapsed". Null if not expandable.</summary>
    public string? ExpandState { get; set; }

    /// <summary>Scroll capability: "v" (vertical), "h" (horizontal), "vh" (both). Null if not scrollable.</summary>
    public string? ScrollDir { get; set; }

    /// <summary>Depth in the tree (0 = root). Set on flat result lists from inspect (so the
    /// command can rebuild a nested tree via the depth-stack); not populated by search /
    /// wait-for / get-focused, and null on already-nested inspect output.</summary>
    public int? Depth { get; set; }

    /// <summary>Selector of the parent element. Set during the inspect tree walk; not populated
    /// by search / wait-for / get-focused, and null on already-nested inspect output.</summary>
    public string? ParentSelector { get; set; }

    /// <summary>Ancestor element types from root to direct parent (e.g. ["Window","Pane","MenuBar"]).
    /// Set on <c>inspect --interactive --json</c> output to surface the collapsed non-interactive
    /// chain above each surviving element; not populated by search / wait-for / get-focused.</summary>
    public string[]? AncestorPath { get; set; }

    /// <summary>True when this element supports a directly invokable UIA pattern (Invoke/Toggle/SelectionItem/ExpandCollapse). Used by --interactive filtering.</summary>
    public bool IsInvokable { get; set; }

    /// <summary>True when the element has additional descendants that were not included
    /// because the inspect depth limit was reached. Hint to re-run with a deeper --depth.</summary>
    public bool? HasMoreChildren { get; set; }

    /// <summary>HWND of the window this element belongs to. Set on flat result lists; null on nested output (window context is the parent in the tree).</summary>
    public long? WindowHandle { get; set; }

    /// <summary>
    /// Nearest ancestor that supports an invoke pattern (InvokePattern, TogglePattern, etc.).
    /// Populated during search when the matched element itself is not invokable.
    /// </summary>
    public UiElement? InvokableAncestor { get; set; }
}
