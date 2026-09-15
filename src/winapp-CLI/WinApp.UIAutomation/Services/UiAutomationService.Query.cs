// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    private UiElement[] SearchConstrained(UiTarget target, UiSelector selector, int maxResults, CancellationToken ct)
    {
        if (selector.ControlType is { } type && UiControlTypes.GetId(type) == 0)
        {
            throw new ArgumentException($"Unknown UIA control type '{type}'.", nameof(selector));
        }
        if (maxResults <= 0) { return []; }
        var windowRoot = GetRootElement(target);
        if (windowRoot is null) { return []; }

        IUIAutomationElement queryRoot = windowRoot;
        long hwnd = target.WindowHandle;
        if (selector.Root is { } rootSelector)
        {
            // Resolve afresh on every call (including every wait-for poll). Root selection is
            // independent of the descendant predicates and must never pick an arbitrary match.
            var roots = QueryWindow(windowRoot, rootSelector, 2, ct, includeRoot: true)
                .Select(el => (Element: el, Hwnd: hwnd)).ToList();
            if (!target.IsExplicitWindow)
            {
                foreach (var window in GetAllAppWindows(target))
                {
                    ct.ThrowIfCancellationRequested();
                    if (window.Hwnd == (nint)target.WindowHandle) { continue; }
                    var otherRoot = GetRootElementForHwnd(window.Hwnd);
                    if (otherRoot is null) { continue; }
                    foreach (var el in QueryWindow(otherRoot, rootSelector, 2, ct, includeRoot: true))
                    {
                        // Some providers expose an owned window inside the main UIA tree too.
                        if (!roots.Any(r => _automation.CompareElements(r.Element, el)))
                        {
                            roots.Add((el, (long)window.Hwnd));
                        }
                    }
                    if (roots.Count > 1) { break; }
                }
            }
            if (roots.Count > 1)
            {
                throw new UiAmbiguousSelectorException(
                    $"Root selector '{rootSelector.Slug ?? rootSelector.Query}' matched multiple elements. " +
                    "Use a unique AutomationId or slug from 'inspect', or target a single --window.");
            }
            if (roots.Count == 0) { return []; }
            (queryRoot, hwnd) = roots[0];
        }

        var matches = QueryWindow(queryRoot, selector, maxResults, ct);
        var nextId = 0;
        var results = new List<UiElement>();
        AddMatches(matches, queryRoot, hwnd);

        // Retain the established main-window-first fallback only for unscoped queries.
        // Once --root resolves, even zero descendants must not cause a popup search.
        if (results.Count == 0 && selector.Root is null && !target.IsExplicitWindow)
        {
            foreach (var window in GetAllAppWindows(target))
            {
                ct.ThrowIfCancellationRequested();
                if (window.Hwnd == (nint)target.WindowHandle) { continue; }
                var otherRoot = GetRootElementForHwnd(window.Hwnd);
                if (otherRoot is null) { continue; }
                AddMatches(QueryWindow(otherRoot, selector, maxResults - results.Count, ct),
                    otherRoot, (long)window.Hwnd);
                if (results.Count >= maxResults) { break; }
            }
        }
        // Keep runtime-ID slugs: an AutomationId unique in this subtree may be duplicated
        // elsewhere in the window. Subsequent GetText/GetProperties must read the same element.
        return results.ToArray();

        void AddMatches(List<IUIAutomationElement> elements, IUIAutomationElement boundary, long sourceHwnd)
        {
            foreach (var element in elements)
            {
                ct.ThrowIfCancellationRequested();
                var model = ToUiElement(element, "", ref nextId);
                model.WindowHandle = sourceHwnd;
                if (!IsInvokable(element))
                {
                    var ancestor = FindInvokableAncestor(element, boundary);
                    if (ancestor is not null)
                    {
                        model.InvokableAncestor = ToUiElement(ancestor, "", ref nextId);
                    }
                }
                results.Add(model);
            }
        }
    }

    private List<IUIAutomationElement> QueryWindow(IUIAutomationElement root, UiSelector selector,
        int maxResults, CancellationToken ct, bool includeRoot = false)
    {
        if (maxResults <= 0) { return []; }
        if (selector.IsSlug)
        {
            var (_, element) = FindElementBySlugWithCom(selector.Slug!, root, includeRoot, ct);
            return element is not null && MatchesQueryPredicates(element, selector) ? [element] : [];
        }

        // FindAll can return a nonempty but incomplete set (for example after WebView2).
        // Use the same Control View as inspect/slug resolution as the authority for queries
        // and root uniqueness, rather than treating a partial bulk result as complete.
        var matches = new List<IUIAutomationElement>();
        var exactMatches = new List<IUIAutomationElement>();
        var candidates = EnumerateQueryDescendants(root, ct);
        if (includeRoot) { candidates = candidates.Prepend(root); }
        foreach (var element in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesQueryPredicates(element, selector)) { continue; }
            if (selector.Query is not { } query)
            {
                matches.Add(element);
                if (matches.Count >= maxResults) { break; }
                continue;
            }

            var aid = SafeGetBstr(() => element.get_CurrentAutomationId());
            if (string.Equals(aid, query, StringComparison.Ordinal))
            {
                exactMatches.Add(element);
                if (exactMatches.Count >= maxResults) { return exactMatches; }
                continue;
            }
            // Even a full page of substring matches cannot rule out a later exact ID.
            if (exactMatches.Count > 0 || matches.Count >= maxResults) { continue; }
            if (aid?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || SafeGetBstr(() => element.get_CurrentName())?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                matches.Add(element);
            }
        }
        return exactMatches.Count > 0 ? exactMatches : matches;
    }

    private static bool MatchesQueryPredicates(IUIAutomationElement element, UiSelector selector) =>
        (selector.ControlType is null || (int)element.get_CurrentControlType() == UiControlTypes.GetId(selector.ControlType))
        && (selector.ClassName is null || string.Equals(
            SafeGetBstr(() => element.get_CurrentClassName()), selector.ClassName, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<IUIAutomationElement> EnumerateQueryDescendants(IUIAutomationElement root, CancellationToken ct)
    {
        var walker = _automation.get_ControlViewWalker();
        var parents = new Stack<IUIAutomationElement>();
        parents.Push(root);
        while (parents.TryPop(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            IUIAutomationElement? child;
            try { child = walker.GetFirstChildElement(parent); }
            catch (COMException) { continue; }
            var children = new List<IUIAutomationElement>();
            while (child is not null)
            {
                ct.ThrowIfCancellationRequested();
                children.Add(child);
                yield return child;
                try { child = walker.GetNextSiblingElement(child); }
                catch (COMException) { break; }
            }
            for (var i = children.Count - 1; i >= 0; i--) { parents.Push(children[i]); }
        }
    }
}
