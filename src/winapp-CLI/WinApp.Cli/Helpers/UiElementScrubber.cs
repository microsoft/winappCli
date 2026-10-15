// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Strips internal/redundant fields from <see cref="UiElement"/> trees before JSON serialization.
/// </summary>
/// <remarks>
/// JSON consumers address elements via the public <c>selector</c> slug; the synthetic walk-order
/// <c>id</c>, <c>parentSelector</c>, and <c>windowHandle</c> are implementation detail that leak
/// if not scrubbed. This helper also flattens <c>InvokableAncestor</c> down to a non-recursive
/// copy (no <c>children</c> or nested <c>invokableAncestor</c>) so it can never form a reference
/// cycle with its parent's children — System.Text.Json (without ReferenceHandler) would otherwise throw.
/// </remarks>
internal static class UiElementScrubber
{
    /// <summary>Recursively clear internal fields on <paramref name="element"/>, its
    /// <see cref="UiElement.Children"/>, and (in flattened form) its
    /// <see cref="UiElement.InvokableAncestor"/>.</summary>
    public static void Scrub(UiElement? element)
    {
        if (element is null) { return; }
        Strip(element);
        FlattenInvokableAncestor(element);
        if (element.Children is { } kids)
        {
            foreach (var c in kids) { Scrub(c); }
        }
    }

    /// <summary>Scrub each element of a flat list (e.g., search/wait-for matches).</summary>
    public static void ScrubAll(IEnumerable<UiElement>? elements)
    {
        if (elements is null) { return; }
        foreach (var el in elements) { Scrub(el); }
    }

    private static void Strip(UiElement el)
    {
        el.Id = null;
        el.ParentSelector = null;
        el.WindowHandle = null;
    }

    private static void FlattenInvokableAncestor(UiElement el)
    {
        if (el.InvokableAncestor is { } anc) { el.InvokableAncestor = ToAncestorHint(anc); }
    }

    /// <summary>Copy <paramref name="anc"/> without Children / nested InvokableAncestor (breaks any
    /// cycle) or internal ids. Non-nullable state and bounds are copied so the hint doesn't
    /// serialize as a disabled element at 0,0.</summary>
    internal static UiElement ToAncestorHint(UiElement anc) => new()
    {
        Type = anc.Type,
        Name = anc.Name,
        AutomationId = anc.AutomationId,
        Selector = anc.Selector,
        IsInvokable = anc.IsInvokable,
        IsEnabled = anc.IsEnabled,
        IsOffscreen = anc.IsOffscreen,
        X = anc.X,
        Y = anc.Y,
        Width = anc.Width,
        Height = anc.Height,
    };
}
