// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    private UiElement[] SearchConstrained(UiTarget target, UiSelector selector, int maxResults,
        bool requireUnique = false, bool preferExactName = false, CancellationToken ct = default)
    {
        // A window-scoped target whose window has closed has no elements left, so a `--gone`
        // wait succeeds instead of failing on the dead provider. Other failures still surface.
        if (ExplicitWindowClosed(target)) { return []; }
        try
        {
            return SearchConstrainedCore(target, selector, maxResults, requireUnique, preferExactName, ct);
        }
        catch (System.Runtime.InteropServices.COMException) when (ExplicitWindowClosed(target))
        {
            return [];
        }
    }

    private static bool ExplicitWindowClosed(UiTarget target) =>
        target.IsExplicitWindow && target.WindowHandle != 0
        && SystemUiQuery.s_getProcessIdForWindow(target.WindowHandle) == 0;

    private UiElement[] SearchConstrainedCore(UiTarget target, UiSelector selector, int maxResults,
        bool requireUnique, bool preferExactName, CancellationToken ct)
    {
        var matches = QueryConstrained(target, selector, maxResults, requireUnique, ct, preferExactName: preferExactName);
        var nextId = 0;
        var results = new List<UiElement>();
        foreach (var (element, boundary, sourceHwnd) in matches)
        {
            ct.ThrowIfCancellationRequested();
            var model = ToUiElement(element, "", ref nextId, requireCurrentIdentity: true);
            if (requireUnique) { SetResolvedWindowHandle(model, element, sourceHwnd); }
            else { model.WindowHandle = sourceHwnd; }
            if (!requireUnique && !IsInvokable(element))
            {
                var ancestor = FindInvokableAncestor(element, boundary);
                if (ancestor is not null)
                {
                    model.InvokableAncestor = ToUiElement(ancestor, "", ref nextId, requireCurrentIdentity: true);
                }
            }
            results.Add(model);
        }
        // Keep runtime-ID slugs: an AutomationId unique in this subtree may be duplicated
        // elsewhere in the window. Subsequent GetText/GetProperties must read the same element.
        return results.ToArray();
    }

    private List<(IUIAutomationElement Element, IUIAutomationElement Boundary, long Hwnd)> QueryConstrained(
        UiTarget target, UiSelector selector, int maxResults, bool requireUnique, CancellationToken ct,
        IUIAutomationElement? windowRoot = null, bool preferExactName = false)
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
        // Explicit actions must not recover a missing HWND onto a different app window.
        windowRoot ??= requireUnique && target.WindowHandle != 0
            ? s_elementFromHandle(this, (nint)target.WindowHandle)
            : GetRootElement(target, requireCurrentIdentity: true);
        if (windowRoot is null) { return []; }

        IUIAutomationElement queryRoot = windowRoot;
        long hwnd = target.WindowHandle;
        if (selector.Root is { } rootSelector)
        {
            // Resolve afresh on every call (including every wait-for poll). Root selection is
            // independent of the descendant predicates and must never pick an arbitrary match.
            var roots = QueryWindow(windowRoot, rootSelector, 2, ct, out var hasExactRoot,
                includeRoot: true, requireUnique: requireUnique)
                .Select(el => (Element: el, Hwnd: hwnd)).ToList();
            if (!target.IsExplicitWindow)
            {
                foreach (var window in GetAllAppWindows(target))
                {
                    ct.ThrowIfCancellationRequested();
                    if (window.Hwnd == (nint)target.WindowHandle) { continue; }
                    var otherRoot = GetWindowRoot(window.Hwnd);
                    if (otherRoot is null) { continue; }
                    var windowRoots = QueryWindow(otherRoot, rootSelector, 2, ct, out var hasExactWindowRoot,
                        includeRoot: true, requireUnique: requireUnique);
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

        var includeWindow = selector.IsSlug && selector.Root is null;
        // An exact Name match can be anywhere among the substring matches, so collect them all.
        var exactName = preferExactName && !selector.IsSlug ? selector.Query : null;
        var limit = exactName is null ? maxResults : int.MaxValue;
        var matches = QueryWindow(queryRoot, selector, limit, ct, out var hasExactId,
            includeRoot: includeWindow, requireUnique: requireUnique);
        var results = new List<(IUIAutomationElement Element, IUIAutomationElement Boundary, long Hwnd)>();
        AddMatches(matches, queryRoot, hwnd);

        // Retain the established main-window-first fallback only for unscoped queries.
        // Once --root resolves, even zero descendants must not cause a popup search.
        if ((requireUnique || results.Count == 0) && selector.Root is null && !target.IsExplicitWindow)
        {
            foreach (var window in GetAllAppWindows(target))
            {
                ct.ThrowIfCancellationRequested();
                if (window.Hwnd == (nint)target.WindowHandle) { continue; }
                var otherRoot = GetWindowRoot(window.Hwnd);
                if (otherRoot is null) { continue; }
                var windowMatches = QueryWindow(otherRoot, selector, requireUnique ? limit : limit - results.Count,
                    ct, out var hasExactWindowId, includeRoot: includeWindow, requireUnique: requireUnique);
                if (requireUnique)
                {
                    // Substring ambiguity is provisional until every HWND has been checked for an exact ID.
                    if (hasExactId && !hasExactWindowId) { continue; }
                    if (hasExactWindowId && !hasExactId) { results.Clear(); }
                }
                hasExactId |= hasExactWindowId;
                AddMatches(windowMatches, otherRoot, (long)window.Hwnd);
                if (!requireUnique && results.Count >= limit) { break; }
            }
        }
        if (exactName is not null && !hasExactId && results.Count > 1)
        {
            var exactNameResults = results.Where(result => IsExactNameMatch(
                GetBstr(() => s_getCurrentBstr(result.Element, UIA_PROPERTY_ID.UIA_NamePropertyId)), exactName)).Take(2).ToList();
            if (exactNameResults.Count == 1) { results = exactNameResults; }
        }
        if (requireUnique && results.Count > 1)
        {
            throw new UiAmbiguousSelectorException(FormatAmbiguousMatches(
                results.Select(r => r.Element).ToList(),
                "Use a unique slug from 'inspect' or narrow --root, --type, or --class-name."));
        }
        return results.Count > maxResults ? results.GetRange(0, maxResults) : results;

        IUIAutomationElement? GetWindowRoot(nint hwnd) => requireUnique
            ? s_elementFromHandle(this, hwnd) ?? throw new InvalidOperationException(
                $"Cannot verify selector uniqueness because HWND {hwnd} could not be read. Re-run 'inspect' or 'search'.")
            : GetRootElementForHwnd(hwnd, requireCurrentIdentity: true);

        void AddMatches(List<IUIAutomationElement> elements, IUIAutomationElement boundary, long sourceHwnd)
        {
            foreach (var element in elements)
            {
                ct.ThrowIfCancellationRequested();
                if (requireUnique && results.Any(result =>
                    s_compareElements(this, result.Element, element))) { continue; }
                results.Add((element, boundary, sourceHwnd));
            }
        }
    }

    private List<IUIAutomationElement> QueryWindow(IUIAutomationElement root, UiSelector selector,
        int maxResults, CancellationToken ct, out bool hasExactId, bool includeRoot = false, bool requireUnique = false)
    {
        hasExactId = false;
        if (maxResults <= 0) { return []; }
        if (selector.IsSlug)
        {
            // A same-name element with a different runtime ID is not this identity.
            // In particular, another app window may contain the actual slug.
            var (_, element) = FindElementBySlugWithCom(selector.Slug!, root, includeRoot,
                throwOnHashMismatch: false, ct: ct, requireCurrentIdentity: true,
                requireUnique: requireUnique, matches: candidate => MatchesQueryPredicates(candidate, selector));
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
            // A bounded bulk result cannot prove uniqueness. Explicit actions finish the strict
            // Control View walk, including predicates, even after finding their first candidate.
            var results = requireUnique
                ? ManualTreeSearchCore(root, int.MaxValue, Matches, ct, throwOnTraversalFailure: true)
                : FindAllDescendantMatches(root, condition, remaining,
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

    /// <summary>
    /// Every match of an ambiguous read, for the error listing. The read stopped at its second match, so
    /// this repeats the same lookup without that cap. It runs only after the read has already failed as
    /// ambiguous, and falls back to the two matches it found if the repeat fails or the UI changed.
    /// </summary>
    private List<IUIAutomationElement> ListAmbiguousMatches(
        UiTarget target, UiSelector selector, UiElement[] found, CancellationToken ct)
    {
        try
        {
            var all = QueryConstrained(target, selector, int.MaxValue, requireUnique: false, ct)
                .Select(match => match.Element).ToList();
            if (all.Count > 1) { return all; }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
            or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not list every match of an ambiguous selector");
        }
        return found.Select(match => match.Context?.AutomationElement).OfType<IUIAutomationElement>().ToList();
    }

    /// <summary>
    /// The message for a selector that had to match one element but matched several: the count, the first
    /// five matches with a selector for each, and <paramref name="advice"/>.
    /// </summary>
    private string FormatAmbiguousMatches(List<IUIAutomationElement> matches, string advice)
    {
        const int Shown = 5;
        var listing = new System.Text.StringBuilder();
        listing.AppendLine($"Selector matched {matches.Count} elements:");
        foreach (var candidate in matches.Take(Shown))
        {
            try
            {
                // Identity properties only: describing a candidate must not read its control patterns.
                var type = GetControlTypeName(candidate.get_CurrentControlType());
                var name = SafeGetBstr(() => s_getCurrentBstr(candidate, UIA_PROPERTY_ID.UIA_NamePropertyId));
                var automationId = SafeGetBstr(() => s_getCurrentBstr(candidate, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId));
                string? slug = null;
                try
                {
                    unsafe { slug = SlugGenerator.GenerateSlugFromSafeArray(type, automationId, name, candidate.GetRuntimeId()); }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
                {
                    // Without a runtime ID the line still names the element; the fallback text says how to get a selector.
                    _logger.LogDebug(ex, "Could not build a slug for an ambiguous match");
                }
                listing.AppendLine($"  {type} \"{name}\"  -> {slug ?? "(run inspect for an exact selector)"}");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // A match that went away while listing must not hide the ambiguity itself.
                listing.AppendLine("  (element is no longer available)");
            }
        }
        if (matches.Count > Shown)
        {
            listing.AppendLine($"  ... and {matches.Count - Shown} more");
        }
        listing.Append(advice);
        return listing.ToString();
    }
}
