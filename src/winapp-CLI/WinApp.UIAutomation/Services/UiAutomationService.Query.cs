// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    private UiElement[] SearchConstrained(UiTarget target, UiSelector selector, int maxResults, CancellationToken ct)
    {
        if (selector.Root?.Root is not null)
        {
            throw new ArgumentException("Only one root level is supported; selector.Root.Root must be null.", nameof(selector));
        }
        ValidateControlType(selector.ControlType);
        ValidateControlType(selector.Root?.ControlType);

        static void ValidateControlType(string? type)
        {
            if (type is not null && UiControlTypes.GetId(type) == 0)
            {
                throw new ArgumentException($"Unknown UIA control type '{type}'.", nameof(selector));
            }
        }
        if (maxResults <= 0) { return []; }
        var windowRoot = GetRootElement(target, requireCurrentIdentity: true);
        if (windowRoot is null) { return []; }

        IUIAutomationElement queryRoot = windowRoot;
        long hwnd = target.WindowHandle;
        if (selector.Root is { } rootSelector)
        {
            // Resolve afresh on every call (including every wait-for poll). Root selection is
            // independent of the descendant predicates and must never pick an arbitrary match.
            var roots = QueryWindow(windowRoot, rootSelector, 2, ct, out var hasExactRoot, includeRoot: true)
                .Select(el => (Element: el, Hwnd: hwnd)).ToList();
            if (!target.IsExplicitWindow)
            {
                foreach (var window in GetAllAppWindows(target))
                {
                    ct.ThrowIfCancellationRequested();
                    if (window.Hwnd == (nint)target.WindowHandle) { continue; }
                    var otherRoot = GetRootElementForHwnd(window.Hwnd, requireCurrentIdentity: true);
                    if (otherRoot is null) { continue; }
                    var windowRoots = QueryWindow(otherRoot, rootSelector, 2, ct, out var hasExactWindowRoot, includeRoot: true);
                    // Exact IDs take precedence across the entire app, not just within an HWND.
                    if (hasExactRoot && !hasExactWindowRoot) { continue; }
                    if (hasExactWindowRoot && !hasExactRoot) { roots.Clear(); }
                    hasExactRoot |= hasExactWindowRoot;
                    foreach (var el in windowRoots)
                    {
                        // Some providers expose an owned window inside the main UIA tree too.
                        if (roots.Count < 2 && !roots.Any(r => _automation.CompareElements(r.Element, el)))
                        {
                            roots.Add((el, (long)window.Hwnd));
                        }
                    }
                    // Substring ambiguity is provisional until every window has been checked for an exact ID.
                    if (roots.Count > 1 && (hasExactRoot || rootSelector.Query is null)) { break; }
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

        var matches = QueryWindow(queryRoot, selector, maxResults, ct, out _);
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
                var otherRoot = GetRootElementForHwnd(window.Hwnd, requireCurrentIdentity: true);
                if (otherRoot is null) { continue; }
                AddMatches(QueryWindow(otherRoot, selector, maxResults - results.Count, ct, out _),
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
                var model = ToUiElement(element, "", ref nextId, requireCurrentIdentity: true);
                model.WindowHandle = sourceHwnd;
                if (!IsInvokable(element))
                {
                    var ancestor = FindInvokableAncestor(element, boundary);
                    if (ancestor is not null)
                    {
                        model.InvokableAncestor = ToUiElement(ancestor, "", ref nextId, requireCurrentIdentity: true);
                    }
                }
                results.Add(model);
            }
        }
    }

    private List<IUIAutomationElement> QueryWindow(IUIAutomationElement root, UiSelector selector,
        int maxResults, CancellationToken ct, out bool hasExactId, bool includeRoot = false)
    {
        hasExactId = false;
        if (maxResults <= 0) { return []; }
        if (selector.IsSlug)
        {
            // A same-name element with a different runtime ID is not this identity.
            // In particular, another app window may contain the actual slug.
            var (_, element) = FindElementBySlugWithCom(selector.Slug!, root, includeRoot,
                throwOnHashMismatch: false, ct: ct, requireCurrentIdentity: true);
            return element is not null && MatchesQueryPredicates(element, selector) ? [element] : [];
        }

        // Complete the exact-ID search before considering even a full page of substring
        // matches: a provider may omit a later exact ID from its bulk results.
        if (selector.Query is { } query)
        {
            var exact = Search(_automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, ComVariant.Create(query)),
                element => string.Equals(GetBstr(() => s_getCurrentBstr(element, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId)),
                    query, StringComparison.Ordinal));
            if (exact.Count > 0)
            {
                hasExactId = true;
                return exact;
            }
            return Search(BuildCondition(selector)!, element =>
                GetBstr(() => s_getCurrentBstr(element, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId)).Contains(query, StringComparison.OrdinalIgnoreCase)
                || GetBstr(() => s_getCurrentBstr(element, UIA_PROPERTY_ID.UIA_NamePropertyId)).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        return Search(_automation.CreateTrueCondition(), _ => true);

        List<IUIAutomationElement> Search(IUIAutomationCondition condition, Func<IUIAutomationElement, bool> textMatches)
        {
            condition = _automation.CreateAndCondition(condition, _automation.get_ControlViewCondition());
            if (selector.ControlType is { } controlType)
            {
                condition = _automation.CreateAndCondition(condition, _automation.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_ControlTypePropertyId, ComVariant.Create(UiControlTypes.GetId(controlType))));
            }
            if (selector.ClassName is { } className)
            {
                condition = _automation.CreateAndCondition(condition, _automation.CreatePropertyConditionEx(
                    UIA_PROPERTY_ID.UIA_ClassNamePropertyId, ComVariant.Create(className),
                    PropertyConditionFlags.PropertyConditionFlags_IgnoreCase));
            }
            bool Matches(IUIAutomationElement element) => MatchesQueryPredicates(element, selector) && textMatches(element);
            ct.ThrowIfCancellationRequested();
            var rootMatches = includeRoot && Matches(root);
            var remaining = maxResults - (rootMatches ? 1 : 0);
            var results = FindAllDescendantMatches(root, condition, remaining,
                () => ManualTreeSearchCore(root, remaining, Matches, ct, throwOnTraversalFailure: true),
                Matches, requireCurrentIdentity: true, ct: ct);
            if (rootMatches) { results.Insert(0, root); }
            return results;
        }
    }

    private static bool MatchesQueryPredicates(IUIAutomationElement element, UiSelector selector) =>
        (selector.ControlType is null || (int)element.get_CurrentControlType() == UiControlTypes.GetId(selector.ControlType))
        && (selector.ClassName is null || string.Equals(
            GetBstr(() => s_getCurrentBstr(element, UIA_PROPERTY_ID.UIA_ClassNamePropertyId)), selector.ClassName, StringComparison.OrdinalIgnoreCase));
}
