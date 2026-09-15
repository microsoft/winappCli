// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// A parsed selector expression. Either a semantic slug (btn-minimize-c4b9) or a plain text search query.
/// </summary>
public sealed record UiSelector
{
    /// <summary>Semantic slug selector, e.g., "btn-minimize-c4b9".</summary>
    public string? Slug { get; init; }

    /// <summary>Plain text search query — matches against Name and AutomationId (substring, case-insensitive).</summary>
    public string? Query { get; init; }

    /// <summary>Selector for a unique root. Only its descendants are searched. The root selector must not itself have a Root.</summary>
    public UiSelector? Root { get; init; }

    /// <summary>UIA control type name (case-insensitive), or TextBox/Edit and TextBlock/Text aliases.</summary>
    public string? ControlType { get; init; }

    /// <summary>Exact, case-insensitive UIA ClassName. Not a substring or wildcard.</summary>
    public string? ClassName { get; init; }

    /// <summary>Whether this selector narrows the query using a root, type, or class.</summary>
    public bool HasConstraints => Root is not null || ControlType is not null || ClassName is not null;

    /// <summary>Whether this selector targets a semantic slug.</summary>
    public bool IsSlug => Slug is not null;

    /// <summary>Whether this selector targets a plain text query.</summary>
    public bool IsQuery => Query is not null;
}
