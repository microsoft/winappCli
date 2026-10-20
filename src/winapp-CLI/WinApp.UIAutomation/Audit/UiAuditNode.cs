// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>An element observed in one bounded raw-view UIA snapshot.</summary>
public sealed record UiAuditNode
{
    /// <summary>Scan-local identifier, such as <c>n1</c>; not an AutomationId or a persistent locator.</summary>
    public string Id { get; init; } = "";

    /// <summary>Scan-local parent identifier; null for the selected window's root.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentId { get; init; }

    /// <summary>Raw-tree depth, starting at zero for the root.</summary>
    public int Depth { get; init; }

    /// <summary>Provider runtime ID in uppercase hexadecimal array notation, or null if unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeId { get; init; }

    /// <summary>Provider AutomationId, when available. An empty or missing ID does not prevent capture.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AutomationId { get; init; }

    /// <summary>Accessible name, collected only when this node and its ancestors are known non-password elements. May contain user data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; init; }

    /// <summary>UIA control-type name, or <c>Unknown</c> if the property cannot be read.</summary>
    public string ControlType { get; init; } = "Unknown";

    /// <summary>Provider framework identifier when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FrameworkId { get; init; }

    /// <summary>Provider class name when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ClassName { get; init; }

    /// <summary>Directly verified password status; null means privacy is indeterminate, not false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsPassword { get; init; }

    /// <summary>Provider enabled status, or null if unavailable; never defaulted to false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsEnabled { get; init; }

    /// <summary>Provider keyboard-focusability status, or null if unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsKeyboardFocusable { get; init; }

    /// <summary>Provider keyboard-focus status at read time, or null if unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? HasKeyboardFocus { get; init; }

    /// <summary>Provider offscreen status, or null if unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsOffscreen { get; init; }

    /// <summary>Provider bounding rectangle in physical screen coordinates, or null if unavailable or malformed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UiAuditNodeBounds? Bounds { get; init; }

    /// <summary>Control-pattern names positively reported as available. Unsupported availability properties are recorded in issues.</summary>
    public IReadOnlyList<string> Patterns { get; init; } = [];

    /// <summary>Bounded visible text formatting evidence, without text content; empty when no text pattern is available.</summary>
    public IReadOnlyList<UiAuditNodeTextRun> TextRuns { get; init; } = [];

    /// <summary>Node-specific read failures, unsupported properties, bounds, and privacy limitations.</summary>
    public IReadOnlyList<UiAuditSnapshotIssue> Issues { get; init; } = [];
}

/// <summary>A provider rectangle in physical screen coordinates. Zero-area rectangles remain zero-area; invalid numbers are never emitted.</summary>
/// <param name="Left">Left screen coordinate.</param>
/// <param name="Top">Top screen coordinate.</param>
/// <param name="Width">Nonnegative width.</param>
/// <param name="Height">Nonnegative height.</param>
public sealed record UiAuditNodeBounds(double Left, double Top, double Width, double Height);

/// <summary>Formatting attributes for a bounded visible text range; contains no text or Value data.</summary>
public sealed record UiAuditNodeTextRun
{
    /// <summary>Scan-local text range ID, such as <c>n1.t1</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Up to 32 provider rectangles for this range; omissions and malformed rectangles are recorded in the node's issues.</summary>
    public IReadOnlyList<UiAuditNodeBounds> Bounds { get; init; } = [];

    /// <summary>Foreground COLORREF as a raw number in <c>0x00bbggrr</c> order. Unknown values are never converted to black.</summary>
    public UiAuditField ForegroundColor { get; init; } = new();

    /// <summary>Background COLORREF as a raw number in <c>0x00bbggrr</c> order; not an inferred or composited pixel color.</summary>
    public UiAuditField BackgroundColor { get; init; } = new();

    /// <summary>Provider font size in points, not pixels.</summary>
    public UiAuditField FontSize { get; init; } = new();

    /// <summary>Provider numeric font weight.</summary>
    public UiAuditField FontWeight { get; init; } = new();

    /// <summary>Whether the provider says the range is hidden; mixed and unavailable values remain explicit.</summary>
    public UiAuditField IsHidden { get; init; } = new();
}
