// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// UIA backend using Windows UI Automation COM APIs via CsWin32.
/// Provides cross-process element tree inspection and pattern-based interaction.
/// </summary>
/// <remarks>
/// Coverage ceiling (issue #630): the deterministic real-UIA suite drives a live in-process WinForms
/// target to cover the ordinary success and error paths across this partial class. The lines that
/// remain uncovered are defensive UI Automation COM provider fault arms (catch/break/continue/log)
/// and native HWND-enumeration branches that cannot be triggered safely without unsafe native
/// provider fault injection on the shared desktop.
/// </remarks>
internal sealed partial class UiAutomationService : IUiAutomation
{
    private const int UiaElementNotAvailable = unchecked((int)0x80040201);

    private readonly ILogger<UiAutomationService> _logger;
    private readonly IUIAutomation _automation;
    private readonly IUiSelectorParser _selectorParser;
    private int _serializedElementResolutionCount;

    internal int SerializedElementResolutionCount => Volatile.Read(ref _serializedElementResolutionCount);

    internal static Func<UiAutomationService, UiTarget, IUIAutomationElement?> s_getRootElement = (service, uiTarget) => service.GetRootElementCore(uiTarget);
    internal static Func<UiAutomationService, nint, IUIAutomationElement?> s_getRootElementForHwnd = (service, hwnd) => service.GetRootElementForHwndCore(hwnd);
    internal static Func<UiAutomationService, UiTarget, List<(nint Hwnd, int Pid, string Title)>> s_getAllAppWindows = (service, uiTarget) => service.GetAllAppWindowsCore(uiTarget);
    internal static Func<UiAutomationService, UiTarget, UiSelector, CancellationToken, UiElement?> s_findElementOnOtherWindows =
        (service, uiTarget, selector, ct) => service.FindElementOnOtherWindowsCore(uiTarget, selector, ct);
    internal static Func<IUIAutomationElement, IUIAutomationCondition, IUIAutomationElementArray?> s_findAllDescendants =
        (root, condition) => root.FindAll(TreeScope.TreeScope_Descendants, condition);
    internal static Func<UiAutomationService, IUIAutomationTreeWalker> s_getControlViewWalker =
        service => service._automation.get_ControlViewWalker();
    internal static Func<UiAutomationService, IUIAutomationElement, IUIAutomationElement, bool> s_compareElements =
        (service, first, second) => service._automation.CompareElements(first, second);
    internal static Func<UiAutomationService, IUIAutomationElement, string, int, CancellationToken, List<IUIAutomationElement>> s_manualTreeSearch =
        (service, root, query, maxResults, ct) => service.ManualTreeSearchCore(root, query, maxResults, ct);
    internal static Func<UiAutomationService, IUIAutomationElement, IUIAutomationElement, IUIAutomationElement?> s_findInvokableAncestor = (service, element, root) => service.FindInvokableAncestorCore(element, root);
    internal static Func<UiAutomationService, IUIAutomationElement?> s_getFocusedElement = service => service._automation.GetFocusedElement();
    internal static Func<IUIAutomationElement, int> s_getElementProcessId = element => element.get_CurrentProcessId();
    internal static Func<UiAutomationService, IUIAutomationElement?> s_getDesktopRootElement = service => service._automation.GetRootElement();
    internal static Func<UiAutomationService, nint, IUIAutomationElement?> s_elementFromHandle = (service, hwnd) => service._automation.ElementFromHandle(new global::Windows.Win32.Foundation.HWND(hwnd));
    internal static Func<int, nint> s_getMainWindowHandleForProcessId = pid => System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle;

    internal static void ResetNativeSeams()
    {
        s_getRootElement = (service, uiTarget) => service.GetRootElementCore(uiTarget);
        s_getRootElementForHwnd = (service, hwnd) => service.GetRootElementForHwndCore(hwnd);
        s_getAllAppWindows = (service, uiTarget) => service.GetAllAppWindowsCore(uiTarget);
        s_findElementOnOtherWindows =
            (service, uiTarget, selector, ct) => service.FindElementOnOtherWindowsCore(uiTarget, selector, ct);
        s_findAllDescendants = (root, condition) => root.FindAll(TreeScope.TreeScope_Descendants, condition);
        s_getControlViewWalker = service => service._automation.get_ControlViewWalker();
        s_compareElements = (service, first, second) => service._automation.CompareElements(first, second);
        s_manualTreeSearch =
            (service, root, query, maxResults, ct) => service.ManualTreeSearchCore(root, query, maxResults, ct);
        s_findInvokableAncestor = (service, element, root) => service.FindInvokableAncestorCore(element, root);
        s_getFocusedElement = service => service._automation.GetFocusedElement();
        s_getElementProcessId = element => element.get_CurrentProcessId();
        s_getDesktopRootElement = service => service._automation.GetRootElement();
        s_elementFromHandle = (service, hwnd) => service._automation.ElementFromHandle(new global::Windows.Win32.Foundation.HWND(hwnd));
        s_getMainWindowHandleForProcessId = pid => System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle;
        s_captureFromWindow = CaptureFromWindow;
        s_captureFromScreenScaled = CaptureFromScreenScaled;
        s_foregroundWindowForBlankRetry = ForegroundWindowForBlankRetry;
        s_sleepForBlankRetry = Thread.Sleep;
    }

    public UiAutomationService(ILogger<UiAutomationService> logger, IUiSelectorParser selectorParser)
    {
        _logger = logger;
        _selectorParser = selectorParser;
        _automation = CUIAutomation8.CreateInstance<IUIAutomation>();
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery)
    {
        return EnumerateWindows((pid, title) =>
            string.IsNullOrEmpty(titleQuery) || (title.Length > 0 && title.Contains(titleQuery, StringComparison.OrdinalIgnoreCase)));
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int targetPid)
    {
        return EnumerateWindows((pid, title) => pid == targetPid);
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): tests cover zero-HWND and successful live HWND bounds. Remaining
    /// lines are Win32 GetWindowRect failure/catch arms that require native handle invalidation races.
    /// </remarks>
    public bool TryGetWindowRect(long hwnd, out PointerRect rect)
    {
        rect = default;
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            var target = new global::Windows.Win32.Foundation.HWND((nint)hwnd);
            // Touch/pen bounds checks may pass a child/control HWND from UIA; use its top-level
            // root so the safety gate matches the foreground guard and the documented contract.
            var root = global::Windows.Win32.PInvoke.GetAncestor(
                target, global::Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
            var rectHwnd = root.IsNull ? target : root;

            global::Windows.Win32.Foundation.RECT r;
            bool ok;
            unsafe
            {
                ok = global::Windows.Win32.PInvoke.GetWindowRect(rectHwnd, &r);
            }

            if (!ok)
            {
                return false;
            }

            rect = new PointerRect(r.left, r.top, r.right, r.bottom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<(nint Hwnd, int Pid, string Title)> EnumerateWindows(Func<int, string, bool> filter)
    {
        var results = new List<(nint, int, string)>();
        var hwnd = global::Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = global::Windows.Win32.PInvoke.FindWindowEx(
                global::Windows.Win32.Foundation.HWND.Null, hwnd, null, (string?)null);
            if (hwnd.IsNull)
            {
                break;
            }

            if (!global::Windows.Win32.PInvoke.IsWindowVisible(hwnd))
            {
                continue;
            }

            unsafe
            {
                uint pid = 0;
                global::Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);

                // Allocate buffer outside the hot path (CA2014: no stackalloc in loop)
                var titleChars = new char[512];
                fixed (char* buffer = titleChars)
                {
                    var len = global::Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                    var title = len > 0 ? new string(buffer, 0, len) : "";

                    if (filter((int)pid, title))
                    {
                        results.Add(((nint)hwnd.Value, (int)pid, title));
                    }
                }
            }
        }
        return results;
    }

    public Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Inspecting process {Pid} at depth {Depth}", uiTarget.ProcessId, depth);
        var nextElementId = 0;

        var root = GetRootElement(uiTarget);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        var resolvedRootHwnd = GetTopLevelWindowHandle(root);
        var mainHwnd = resolvedRootHwnd != 0
            ? resolvedRootHwnd
            : (nint)uiTarget.WindowHandle;

        // If a selector is provided, scope the tree walk to that element
        IUIAutomationElement startElement = root;
        if (!string.IsNullOrEmpty(elementId))
        {
            IUIAutomationElement? target = null;

            // Try as a slug first
            var slugParsed = SlugGenerator.ParseSlug(elementId);
            if (slugParsed is not null)
            {
                var slugResult = FindElementBySlug(elementId, root);
                if (slugResult is not null)
                {
                    target = GetAutomationElement(uiTarget, slugResult);
                }
            }
            else
            {
                // Try as a legacy selector
                var selectorParser = _selectorParser;
                var selector = selectorParser.Parse(elementId);
                var condition = BuildCondition(selector);
                if (condition is not null)
                {
                    target = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                }
            }

            if (target is not null)
            {
                startElement = target;
                var scopedHwnd = ResolveTopLevelWindowHandle(startElement);
                if (scopedHwnd != 0)
                {
                    mainHwnd = scopedHwnd;
                }
            }
        }

        // A process-target inspect must keep each top-level HWND in its own output group. UIA can
        // expose an owned window as a descendant of the selected root; prune those roots from the
        // main walk and add them through the existing per-window path below.
        var independentWindows = new List<(nint Hwnd, int Pid, string Title, IUIAutomationElement Root, bool IsInSelectedTree)>();
        if (string.IsNullOrEmpty(elementId) && !uiTarget.IsExplicitWindow)
        {
            foreach (var (hwnd, pid, title) in GetAllAppWindows(uiTarget))
            {
                if (hwnd == mainHwnd) { continue; }

                var className = UiTargetResolver.GetWindowClassName(hwnd);
                if (IsInternalWindow(className)) { continue; }

                var windowRoot = GetRootElementForHwnd(hwnd);
                if (windowRoot is not null)
                {
                    var isInSelectedTree = false;
                    try
                    {
                        var hwndCondition = _automation.CreatePropertyCondition(
                            UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId,
                            ComVariant.Create((int)hwnd));
                        isInSelectedTree =
                            root.FindFirst(TreeScope.TreeScope_Descendants, hwndCondition) is not null;
                    }
                    catch (COMException)
                    {
                        // If reachability cannot be proven, keep the precise slug selector.
                    }
                    independentWindows.Add((hwnd, pid, title, windowRoot, isInSelectedTree));
                }
            }
        }

        var topLevelWindowHandles = independentWindows.Count > 0
            ? independentWindows.Select(window => window.Hwnd).ToHashSet()
            : null;
        topLevelWindowHandles?.Add(mainHwnd);
        var promotableWindowHandles = new HashSet<nint> { mainHwnd };
        foreach (var window in independentWindows.Where(window => window.IsInSelectedTree))
        {
            promotableWindowHandles.Add(window.Hwnd);
        }
        var elements = new List<UiElement>();
        WalkTree(
            startElement,
            depth,
            0,
            "",
            elements,
            ref nextElementId,
            topLevelWindowHandles: topLevelWindowHandles,
            currentWindowHandle: mainHwnd);

        // Set WindowHandle on all elements from main window
        foreach (var el in elements)
        {
            el.WindowHandle = mainHwnd;
        }

        // Also walk popup/owned windows (when inspecting full tree, not scoped to element,
        // and the user did not explicitly target a single HWND — see issue #472).
        if (independentWindows.Count > 0)
        {
            // Add header for main window when there are other independent windows
            var mainInfo = UiTargetResolver.GetWindowInfo(mainHwnd);
            var mainTitle = uiTarget.WindowTitle ?? "";
            elements.Insert(0, new UiElement
            {
                Id = $"--- HWND {mainHwnd}",
                Type = "---",
                Name = $"HWND {mainHwnd}: \"{mainTitle}\" ({mainInfo.Label}, {mainInfo.ClassName})",
                Depth = 0,
                WindowHandle = mainHwnd
            });

            foreach (var (hwnd, pid, title, initialRoot, _) in independentWindows)
            {
                // Re-resolve immediately before walking so a transient window is not held through
                // the potentially long selected-window walk. The initial root preserves the
                // already-pruned subtree if the refresh transiently fails.
                var windowRoot = GetRootElementForHwnd(hwnd) ?? initialRoot;

                var popupElements = new List<UiElement>();
                try
                {
                    WalkTree(
                        windowRoot,
                        depth,
                        0,
                        "",
                        popupElements,
                        ref nextElementId,
                        topLevelWindowHandles: topLevelWindowHandles,
                        currentWindowHandle: hwnd);
                }
                catch (COMException ex)
                {
                    _logger.LogDebug(ex, "Skipping unavailable popup/owned window HWND {Hwnd}", hwnd);
                    continue;
                }

                // Add a separator element to visually distinguish windows
                var info = UiTargetResolver.GetWindowInfo(hwnd);
                var ownerSuffix = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                elements.Add(new UiElement
                {
                    Id = $"--- HWND {hwnd}",
                    Type = "---",
                    Name = $"HWND {hwnd}: \"{title}\" ({info.Label}, {info.ClassName}{ownerSuffix})",
                    Depth = 0,
                    WindowHandle = hwnd
                });

                foreach (var el in popupElements)
                {
                    el.WindowHandle = hwnd;
                }
                elements.AddRange(popupElements);
            }
        }

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, elements, mainHwnd, promotableWindowHandles);

        var result = elements.ToArray();
        return Task.FromResult(result);
    }

    public Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Inspecting ancestors of {ElementId}", elementId);
        var nextElementId = 0;

        var root = GetRootElement(uiTarget);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        // Try as slug first, then legacy selector
        IUIAutomationElement? target = null;

        var slugParsed = SlugGenerator.ParseSlug(elementId);
        if (slugParsed is not null)
        {
            var slugResult = FindElementBySlug(elementId, root);
            if (slugResult is not null)
            {
                target = GetAutomationElement(uiTarget, slugResult);
            }
        }
        else
        {
            var selector = _selectorParser.Parse(elementId);
            var condition = BuildCondition(selector);
            if (condition is not null)
            {
                target = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            }
        }

        if (target is null)
        {
            throw new InvalidOperationException($"No element found matching '{elementId}'.");
        }

        // Walk up via TreeWalker
        var ancestors = new List<UiElement>();
        var walker = _automation.get_ControlViewWalker();
        var current = target;

        // Add the target element itself first
        var windowHandle = ResolveTopLevelWindowHandle(current);
        ancestors.Add(ToUiElement(current, "", ref nextElementId));

        while (true)
        {
            IUIAutomationElement? parent;
            try
            {
                parent = walker.GetParentElement(current);
            }
            catch
            {
                break;
            }

            if (parent is null)
            {
                break;
            }

            // Stop at desktop root (PID 0 or no process)
            try
            {
                var rect = parent.get_CurrentBoundingRectangle();
                // Check if this is the desktop root (has no meaningful parent)
                var parentParent = walker.GetParentElement(parent);
                if (parentParent is null)
                {
                    break;
                }
            }
            catch
            {
                break;
            }

            ancestors.Add(ToUiElement(parent, "", ref nextElementId));
            current = parent;
        }

        // Reverse so root is first, target is last
        ancestors.Reverse();
        windowHandle = windowHandle != 0 ? windowHandle : GetTopLevelWindowHandle(root);
        windowHandle = windowHandle != 0 ? windowHandle : (nint)uiTarget.WindowHandle;
        foreach (var ancestor in ancestors)
        {
            ancestor.WindowHandle = windowHandle;
        }

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, ancestors);

        var result = ancestors.ToArray();
        return Task.FromResult(result);
    }

    public Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Searching in process {Pid}", uiTarget.ProcessId);
        var nextElementId = 0;

        var root = GetRootElement(uiTarget);
        if (root is null)
        {
            return Task.FromResult<UiElement[]>([]);
        }

        var mainResults = new List<UiElement>();

        // A slug names exactly one element, and every branch below matches on Query only - so
        // without this a valid slug silently returns nothing. Resolve it the same way
        // FindSingleElementAsync does and return zero or one result.
        if (selector.IsSlug)
        {
            var slugMatch = FindElementBySlug(selector.Slug!, root);
            if (slugMatch is null)
            {
                return Task.FromResult<UiElement[]>([]);
            }

            slugMatch.WindowHandle = uiTarget.WindowHandle;
            return Task.FromResult<UiElement[]>(maxResults > 0 ? [slugMatch] : []);
        }

        if (selector.Query is not null)
        {
            var exactMatches = FindExactAutomationIdMatches(root, selector.Query, maxResults, ct);
            var found = exactMatches.Count > 0
                ? exactMatches
                : FindPreferredQueryMatches(root, selector, maxResults, ct);
            foreach (var el in found)
            {
                var uiEl = ToUiElement(el, "", ref nextElementId);
                uiEl.WindowHandle = uiTarget.WindowHandle;

                if (!IsInvokable(el))
                {
                    var ancestor = FindInvokableAncestor(el, root);
                    if (ancestor is not null)
                    {
                        uiEl.InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
                    }
                }
                mainResults.Add(uiEl);
            }
        }

        // If no results on main window, search popup/owned windows — unless the user
        // explicitly scoped the session to a single HWND via --window (issue #472).
        if (mainResults.Count == 0 && !uiTarget.IsExplicitWindow)
        {
            var allWindows = GetAllAppWindows(uiTarget);
            var mainHwnd = (nint)uiTarget.WindowHandle;
            foreach (var (hwnd, pid, title) in allWindows)
            {
                ct.ThrowIfCancellationRequested();
                if (hwnd == mainHwnd) { continue; }
                try
                {
                    var windowRoot = GetRootElementForHwnd(hwnd);
                    if (windowRoot is null) { continue; }

                    if (selector.Query is not null)
                    {
                        var remaining = maxResults - mainResults.Count;
                        var exactMatches = FindExactAutomationIdMatches(windowRoot, selector.Query, remaining, ct);
                        var windowFound = exactMatches.Count > 0
                            ? exactMatches
                            : FindPreferredQueryMatches(windowRoot, selector, remaining, ct);
                        foreach (var el in windowFound)
                        {
                            var uiEl = ToUiElement(el, "", ref nextElementId);
                            uiEl.WindowHandle = hwnd;
                            mainResults.Add(uiEl);
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    _logger.LogDebug("COM error searching HWND {Hwnd}: {Message}", hwnd, ex.Message);
                }
                if (mainResults.Count >= maxResults) { break; }
            }
        }

        var results = mainResults.ToArray();

        // Promote unique AutomationIds to selectors (more stable than slugs)
        PromoteUniqueAutomationIds(root, results, uiTarget.WindowHandle);

        return Task.FromResult(results);
    }

    public Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Finding single element in process {Pid}", uiTarget.ProcessId);

        var root = GetRootElement(uiTarget);
        if (root is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        // Slug resolution: walk tree, regenerate slugs, match and validate hash
        if (selector.IsSlug)
        {
            var (slugResult, slugElement) = FindElementBySlugWithCom(selector.Slug!, root);
            if (slugResult is not null && slugElement is not null)
            {
                SetResolvedWindowHandle(slugResult, slugElement, uiTarget.WindowHandle);
                return Task.FromResult<UiElement?>(slugResult);
            }
            // Not found on main window — search other windows (unless --window scoped us to one)
            if (!uiTarget.IsExplicitWindow)
            {
                var otherResult = FindElementOnOtherWindows(uiTarget, selector, ct);
                if (otherResult is not null)
                {
                    return Task.FromResult<UiElement?>(otherResult);
                }
            }
            return Task.FromResult<UiElement?>(null);
        }

        // Try exact AutomationId match first (fast, unambiguous — used when inspect promoted a unique AutomationId)
        if (selector.Query is not null)
        {
            var exactAidCondition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(selector.Query));
            var exactMatch = root.FindFirst(TreeScope.TreeScope_Descendants, exactAidCondition);
            if (exactMatch is not null)
            {
                var nextId = 0;
                var exactResult = ToUiElement(exactMatch, "", ref nextId);
                SetResolvedWindowHandle(exactResult, exactMatch, uiTarget.WindowHandle);
                return Task.FromResult<UiElement?>(exactResult);
            }
        }

        var condition = BuildCondition(selector);
        if (condition is null)
        {
return Task.FromResult<UiElement?>(null);
        }

        var matches = FindAllDescendantMatches(
            root,
            condition,
            int.MaxValue,
            () => ManualTreeSearch(root, selector.Query!, int.MaxValue, ct));
        if (matches.Count == 0)
        {
            // Element not found on main window — search popup/owned windows
            // (unless --window scoped us to a single HWND).
            if (!uiTarget.IsExplicitWindow)
            {
                var otherResult = FindElementOnOtherWindows(uiTarget, selector, ct);
                if (otherResult is not null)
                {
                    return Task.FromResult<UiElement?>(otherResult);
                }
            }
            return Task.FromResult<UiElement?>(null);
        }

        var recoveredExactMatch = FindExactAutomationIdMatch(matches, selector.Query!);
        if (recoveredExactMatch is not null)
        {
            var nextId = 0;
            var exactResult = ToUiElement(recoveredExactMatch, "", ref nextId);
            exactResult.WindowHandle = uiTarget.WindowHandle;
            return Task.FromResult<UiElement?>(exactResult);
        }

        if (matches.Count > 1)
        {
            // When multiple elements match, prefer the invokable one (e.g., Button over Group/Text
            // in SettingsExpander where all children share the same Name)
            IUIAutomationElement? invokableMatch = null;
            int invokableCount = 0;
            foreach (var m in matches)
            {
                if (IsInvokable(m))
                {
                    invokableMatch = m;
                    invokableCount++;
                }
            }

            if (invokableCount == 1 && invokableMatch is not null)
            {
                _logger.LogDebug("Disambiguated {Count} matches by picking the only invokable element", matches.Count);
                var nextId = 0;
                var invokableResult = ToUiElement(invokableMatch, "", ref nextId);
                SetResolvedWindowHandle(invokableResult, invokableMatch, uiTarget.WindowHandle);
                return Task.FromResult<UiElement?>(invokableResult);
            }

            var matchCount = matches.Count;
            var listing = new System.Text.StringBuilder();
            listing.AppendLine($"Selector matched {matchCount} elements:");
            for (int i = 0; i < Math.Min(matchCount, 5); i++)
            {
                var m = matches[i];
                var mName = SafeGetBstr(() => m.get_CurrentName());
                var mType = GetControlTypeName(m.get_CurrentControlType());
                var mAutoId = SafeGetBstr(() => m.get_CurrentAutomationId());
                var mRect = m.get_CurrentBoundingRectangle();
                var nameStr = mName is not null ? $" \"{mName}\"" : "";
                var boundsStr = $" ({mRect.left},{mRect.top} {mRect.right - mRect.left}x{mRect.bottom - mRect.top})";
                // Generate slug for suggestion
                string slugSuggestion;
                try
                {
                    unsafe
                    {
                        var runtimeId = m.GetRuntimeId();
                        slugSuggestion = SlugGenerator.GenerateSlugFromSafeArray(mType, mAutoId, mName, runtimeId);
                    }
                }
                catch { slugSuggestion = $"{SlugGenerator.GetPrefix(mType)}[{i}]"; }
                listing.AppendLine($"  [{i}] {mType}{nameStr}{boundsStr}  -> {slugSuggestion}");
            }
            if (matchCount > 5)
            {
                listing.AppendLine($"  ... and {matchCount - 5} more");
            }
            listing.Append("Use a slug from 'inspect' to target a specific element.");
            throw new UiAmbiguousSelectorException(listing.ToString());
        }

        var element = matches[0];
        var nextElementId = 0;
        var result = ToUiElement(element, "", ref nextElementId);
        SetResolvedWindowHandle(result, element, uiTarget.WindowHandle);

        // Surface invokable ancestor for non-invokable elements
        if (!IsInvokable(element))
        {
            var ancestor = FindInvokableAncestor(element, root);
            if (ancestor is not null)
            {
                result.InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
            }
        }

        return Task.FromResult<UiElement?>(result);
    }

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Basic properties from the UiElement model
        var props = new Dictionary<string, object?>
        {
            ["Name"] = element.Name,
            ["AutomationId"] = element.AutomationId,
            ["ControlType"] = element.Type,
            ["ClassName"] = element.ClassName,
            ["IsEnabled"] = element.IsEnabled,
            ["IsOffscreen"] = element.IsOffscreen,
            ["BoundingRectangle"] = $"{element.X},{element.Y},{element.Width},{element.Height}"
        };

        // Include Value from the element model (captured at inspect/search time)
        if (element.Value is not null)
        {
            props["Value"] = element.Value;
        }

        // Query the live COM element for additional properties
        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is not null)
        {
            // General UIA properties (convert COM BOOL to C# bool)
            try { props["HasKeyboardFocus"] = (bool)comElement.get_CurrentHasKeyboardFocus(); } catch { }
            try { props["IsKeyboardFocusable"] = (bool)comElement.get_CurrentIsKeyboardFocusable(); } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentAcceleratorKey()); if (v is not null) { props["AcceleratorKey"] = v; } } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentAccessKey()); if (v is not null) { props["AccessKey"] = v; } } catch { }
            try { var v = SafeGetBstr(() => comElement.get_CurrentHelpText()); if (v is not null) { props["HelpText"] = v; } } catch { }
            try { props["IsPassword"] = comElement.get_CurrentIsContentElement() && comElement.get_CurrentControlType() == UIA_CONTROLTYPE_ID.UIA_EditControlTypeId; } catch { }

            // Pattern-specific properties
            try
            {
                var pattern = (IUIAutomationTogglePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
                props["ToggleState"] = pattern.get_CurrentToggleState() switch
                {
                    global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Off => "Off",
                    global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_On => "On",
                    global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Indeterminate => "Indeterminate",
                    _ => pattern.get_CurrentToggleState().ToString()
                };
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
                var v = pattern.get_CurrentValue();
                props["Value"] = v.ToString();
                props["IsReadOnly"] = (bool)pattern.get_CurrentIsReadOnly();
            }
            catch { }

            try
            {
                if (comElement is IUIAutomationSelectionItemPattern selPattern)
                {
                    props["IsSelected"] = (bool)selPattern.get_CurrentIsSelected();
                }
                else
                {
                    var pattern = (IUIAutomationSelectionItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
                    props["IsSelected"] = (bool)pattern.get_CurrentIsSelected();
                }
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationExpandCollapsePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
                props["ExpandCollapseState"] = pattern.get_CurrentExpandCollapseState() switch
                {
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Collapsed => "Collapsed",
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Expanded => "Expanded",
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_PartiallyExpanded => "PartiallyExpanded",
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_LeafNode => "LeafNode",
                    _ => pattern.get_CurrentExpandCollapseState().ToString()
                };
            }
            catch { }

            try
            {
                var pattern = (IUIAutomationScrollPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                props["ScrollHorizontalPercent"] = pattern.get_CurrentHorizontalScrollPercent();
                props["ScrollVerticalPercent"] = pattern.get_CurrentVerticalScrollPercent();
                props["HorizontallyScrollable"] = pattern.get_CurrentHorizontallyScrollable();
                props["VerticallyScrollable"] = pattern.get_CurrentVerticallyScrollable();
            }
            catch { }
        }

        if (propertyName is not null)
        {
            if (props.TryGetValue(propertyName, out var val))
            {
                return Task.FromResult(new Dictionary<string, object?> { [propertyName] = val });
            }
            return Task.FromResult(new Dictionary<string, object?> { [propertyName] = null });
        }

        return Task.FromResult(props);
    }

    public Task<string> InvokeAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Invoking element {ElementId}", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // Try InvokePattern
        try
        {
            var pattern = (IUIAutomationInvokePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_InvokePatternId);
            pattern.Invoke();
            return Task.FromResult("InvokePattern");
        }
        catch { }

        // Try TogglePattern
        try
        {
            var pattern = (IUIAutomationTogglePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            pattern.Toggle();
            return Task.FromResult("TogglePattern");
        }
        catch { }

        // Try SelectionItemPattern
        try
        {
            var pattern = (IUIAutomationSelectionItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
            pattern.Select();
            return Task.FromResult("SelectionItemPattern");
        }
        catch { }

        // Try ExpandCollapsePattern
        try
        {
            var pattern = (IUIAutomationExpandCollapsePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            pattern.Expand();
            return Task.FromResult("ExpandCollapsePattern");
        }
        catch { }

        var hint = element.InvokableAncestor is null
            ? "No invokable ancestor was found either — this element is display-only and cannot be activated."
            : $"Try the invokable ancestor: {element.InvokableAncestor.Selector ?? element.InvokableAncestor.Id}";

        throw new InvalidOperationException(
            $"Element {element.Selector ?? element.Id} ({element.Type}) does not support any invoke pattern. {hint}");
    }

    public Task SetValueAsync(UiTarget uiTarget, UiElement element, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Setting value on element {ElementId}", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // The fallback ordering (ValuePattern → RangeValuePattern → LegacyIAccessible/put_accValue,
        // then a send-keys hint) lives in the pure, unit-tested ValueSetter; ComValueSetStrategy
        // supplies the live UIA COM mechanics.
        ValueSetter.Apply(new ComValueSetStrategy(comElement, _logger), element, text);
        return Task.CompletedTask;
    }

    public Task FocusAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Focusing element {ElementId}", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        comElement.SetFocus();
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Getting text from element {ElementId}", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // 1. Try TextPattern (RichEditBox, Document controls — full text with formatting support)
        try
        {
            var pattern = (IUIAutomationTextPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
            var range = pattern.get_DocumentRange();
            var text = range.GetText(-1);
            if (text.Length > 0)
            {
                return Task.FromResult<string?>(text.ToString());
            }
        }
        catch { }

        // 2. Try ValuePattern (TextBox, ComboBox — simple text)
        try
        {
            var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            var bstr = pattern.get_CurrentValue();
            var text = bstr.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                return Task.FromResult<string?>(text);
            }
        }
        catch { }

        // 3. Try TogglePattern (ToggleSwitch, CheckBox — on/off/indeterminate)
        try
        {
            var pattern = (IUIAutomationTogglePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            var state = pattern.get_CurrentToggleState();
            return Task.FromResult<string?>(state switch
            {
                global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_On => "On",
                global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Off => "Off",
                _ => "Indeterminate"
            });
        }
        catch { }

        // 4. Try SelectionPattern (ComboBox, RadioButton, TabView, ListView — selected item name)
        try
        {
            var pattern = (IUIAutomationSelectionPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionPatternId);
            var selection = pattern.GetCurrentSelection();
            if (selection.get_Length() > 0)
            {
                var selected = selection.GetElement(0);
                var name = selected.get_CurrentName();
                if (!string.IsNullOrEmpty(name.ToString()))
                {
                    return Task.FromResult<string?>(name.ToString());
                }
            }
        }
        catch { }

        // 5. Fall back to element Name (static text, labels)
        if (!string.IsNullOrEmpty(element.Name))
        {
            return Task.FromResult<string?>(element.Name);
        }

        return Task.FromResult<string?>(null);
    }

    public Task ScrollIntoViewAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Scrolling element {ElementId} into view", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        // Record position before scroll for verification
        var rectBefore = comElement.get_CurrentBoundingRectangle();

        try
        {
            var pattern = (IUIAutomationScrollItemPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollItemPatternId);
            pattern.ScrollIntoView();

            // Brief wait for scroll animation
            Thread.Sleep(100);

            // Verify position changed
            var rectAfter = comElement.get_CurrentBoundingRectangle();
            if (rectBefore.top == rectAfter.top && rectBefore.left == rectAfter.left)
            {
                _logger.LogWarning("Element position unchanged after ScrollIntoView — the element may already be visible or the container didn't respond.");
            }

            return Task.CompletedTask;
        }
        catch
        {
            // ScrollItemPattern not supported — try ScrollPattern on parent as fallback
            try
            {
                var walker = _automation.get_ControlViewWalker();
                var parent = walker.GetParentElement(comElement);
                var maxWalk = 5;
                while (parent is not null && maxWalk-- > 0)
                {
                    try
                    {
                        var scrollPattern = (IUIAutomationScrollPattern)parent.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                        // Calculate how much to scroll — try to bring element into view
                        var elRect = comElement.get_CurrentBoundingRectangle();
                        var parentRect = parent.get_CurrentBoundingRectangle();

                        if (elRect.top < parentRect.top || elRect.bottom > parentRect.bottom)
                        {
                            scrollPattern.SetScrollPercent(-1, // horizontal: no change
                                Math.Max(0, Math.Min(100,
                                    scrollPattern.get_CurrentVerticalScrollPercent() +
                                    ((double)(elRect.top - parentRect.top) / (parentRect.bottom - parentRect.top) * 100))));
                        }

                        return Task.CompletedTask;
                    }
                    catch
                    {
                        parent = walker.GetParentElement(parent);
                    }
                }
            }
            catch { }

            throw new InvalidOperationException(
                $"Element {element.Id} ({element.Type}) does not support ScrollItemPattern and no scrollable ancestor found.");
        }
    }

    public Task ScrollContainerAsync(UiTarget uiTarget, UiElement element, string? direction, string? destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Scrolling container {ElementId}", element.Id);

        var comElement = GetAutomationElement(uiTarget, element);
        if (comElement is null)
        {
            throw new InvalidOperationException($"Element {element.Id} is stale. Re-run 'inspect' or 'search'.");
        }

        IUIAutomationScrollPattern? scrollPattern = null;
        try
        {
            scrollPattern = (IUIAutomationScrollPattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
        }
        catch { }

        // If target doesn't support ScrollPattern, walk up to find a scrollable parent
        if (scrollPattern is null)
        {
            var walker = _automation.get_ControlViewWalker();
            var parent = walker.GetParentElement(comElement);
            var maxWalk = 10;
            while (parent is not null && maxWalk-- > 0)
            {
                try
                {
                    scrollPattern = (IUIAutomationScrollPattern)parent.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
                    break;
                }
                catch
                {
                    parent = walker.GetParentElement(parent);
                }
            }

            if (scrollPattern is null)
            {
                throw new InvalidOperationException(
                    $"Element {element.Selector ?? element.Id} ({element.Type}) and its ancestors do not support ScrollPattern.");
            }
        }

        if (destination is not null)
        {
            var canScrollV = (bool)scrollPattern.get_CurrentVerticallyScrollable();
            if (!canScrollV)
            {
                throw new InvalidOperationException(
                    $"Element {element.Selector ?? element.Id} cannot scroll vertically (required for --to top/bottom).");
            }

            switch (destination.ToLowerInvariant())
            {
                case "top":
                    scrollPattern.SetScrollPercent(-1, 0);
                    break;
                case "bottom":
                    scrollPattern.SetScrollPercent(-1, 100);
                    break;
                default:
                    throw new ArgumentException($"Invalid --to value '{destination}'. Use 'top' or 'bottom'.");
            }
        }
        else if (direction is not null)
        {
            var currentV = scrollPattern.get_CurrentVerticalScrollPercent();
            var currentH = scrollPattern.get_CurrentHorizontalScrollPercent();
            var canScrollV = (bool)scrollPattern.get_CurrentVerticallyScrollable();
            var canScrollH = (bool)scrollPattern.get_CurrentHorizontallyScrollable();
            const double pageStep = 20.0;

            switch (direction.ToLowerInvariant())
            {
                case "down":
                    if (!canScrollV)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll vertically. " +
                            (canScrollH ? "It can scroll horizontally — try --direction right." : ""));
                    }
                    scrollPattern.SetScrollPercent(-1, Math.Min(100, currentV + pageStep));
                    break;
                case "up":
                    if (!canScrollV)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll vertically. " +
                            (canScrollH ? "It can scroll horizontally — try --direction left." : ""));
                    }
                    scrollPattern.SetScrollPercent(-1, Math.Max(0, currentV - pageStep));
                    break;
                case "right":
                    if (!canScrollH)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll horizontally. " +
                            (canScrollV ? "It can scroll vertically — try --direction down." : ""));
                    }
                    scrollPattern.SetScrollPercent(Math.Min(100, currentH + pageStep), -1);
                    break;
                case "left":
                    if (!canScrollH)
                    {
                        throw new InvalidOperationException(
                            $"Element {element.Selector ?? element.Id} cannot scroll horizontally. " +
                            (canScrollV ? "It can scroll vertically — try --direction up." : ""));
                    }
                    scrollPattern.SetScrollPercent(Math.Max(0, currentH - pageStep), -1);
                    break;
                default:
                    throw new ArgumentException($"Invalid --direction '{direction}'. Use up, down, left, or right.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<UiElement?> GetFocusedElementAsync(UiTarget uiTarget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Getting focused element for process {Pid}", uiTarget.ProcessId);

        IUIAutomationElement? focused;
        try
        {
            focused = s_getFocusedElement(this);
        }
        catch
        {
            return Task.FromResult<UiElement?>(null);
        }

        if (focused is null)
        {
            return Task.FromResult<UiElement?>(null);
        }

        // Verify the focused element belongs to the target process
        try
        {
            var pid = s_getElementProcessId(focused);
            if (pid != uiTarget.ProcessId)
            {
                return Task.FromResult<UiElement?>(null);
            }
        }
        catch
        {
            return Task.FromResult<UiElement?>(null);
        }

        var nextElementId = 0;
        var result = ToUiElement(focused, "", ref nextElementId);
        return Task.FromResult<UiElement?>(result);
    }

    /// <summary>
    /// Resolves a slug selector by walking the tree, regenerating slugs for each element,
    /// and matching + validating the RuntimeId hash.
    /// Returns both the UiElement model and the live COM element.
    /// </summary>
    private (UiElement? Model, IUIAutomationElement? ComElement) FindElementBySlugWithCom(string targetSlug, IUIAutomationElement root)
    {
        var parsed = SlugGenerator.ParseSlug(targetSlug);
        if (parsed is null)
        {
            return (null, null);
        }

        var (targetPrefix, targetNameSlug, targetHash) = parsed.Value;
        var nextElementId = 0;
        const int maxRecursionDepth = 100;

        // DFS walk to find matching slug
        IUIAutomationElement? matchedCom = null;
        UiElement? matchedUi = null;
        bool hashMismatchFound = false;

        void Walk(IUIAutomationElement element, int depth)
        {
            if (matchedCom is not null || depth > maxRecursionDepth)
            {
                return;
            }

            var type = GetControlTypeName(element.get_CurrentControlType());
            var name = SafeGetBstr(() => element.get_CurrentName());
            var automationId = SafeGetBstr(() => element.get_CurrentAutomationId());

            var prefix = SlugGenerator.GetPrefix(type);
            var nameSlug = SlugGenerator.Normalize(automationId) ?? SlugGenerator.Normalize(name);

            // Check prefix and name match
            if (prefix == targetPrefix && nameSlug == targetNameSlug)
            {
                // Validate RuntimeId hash
                try
                {
                    unsafe
                    {
                        var runtimeId = element.GetRuntimeId();
                        var hash = SlugGenerator.ComputeHashFromSafeArray(runtimeId);
                        if (hash == targetHash)
                        {
                            matchedCom = element;
                            matchedUi = ToUiElement(element, "", ref nextElementId);
                            return;
                        }
                        else
                        {
                            hashMismatchFound = true;
                        }
                    }
                }
                catch { }
            }

            // Also handle nameless elements: prefix-hash (no name slug)
            if (targetNameSlug is null && prefix == targetPrefix)
            {
                try
                {
                    unsafe
                    {
                        var runtimeId = element.GetRuntimeId();
                        var hash = SlugGenerator.ComputeHashFromSafeArray(runtimeId);
                        if (hash == targetHash)
                        {
                            matchedCom = element;
                            matchedUi = ToUiElement(element, "", ref nextElementId);
                            return;
                        }
                    }
                }
                catch { }
            }

            // Recurse children
            var walker = _automation.get_ControlViewWalker();
            var child = walker.GetFirstChildElement(element);
            while (child is not null && matchedCom is null)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSiblingElement(child); }
                catch { break; }
            }
        }

        Walk(root, 0);

        if (matchedUi is not null)
        {
            // Surface invokable ancestor
            if (matchedCom is not null && !IsInvokable(matchedCom))
            {
                var ancestor = FindInvokableAncestor(matchedCom, root);
                if (ancestor is not null)
                {
                    matchedUi.InvokableAncestor = ToUiElement(ancestor, "", ref nextElementId);
                }
            }
            return (matchedUi, matchedCom);
        }

        if (hashMismatchFound)
        {
            throw new InvalidOperationException(
                $"Element with slug '{targetSlug}' found by name but RuntimeId hash doesn't match — " +
                "the UI may have changed. Re-run 'inspect' to get updated selectors.");
        }

        return (null, null);
    }

    /// <summary>
    /// Convenience wrapper that returns only the UiElement model from slug resolution.
    /// </summary>
    private UiElement? FindElementBySlug(string targetSlug, IUIAutomationElement root)
    {
        return FindElementBySlugWithCom(targetSlug, root).Model;
    }

    // --- Private helpers ---

    /// <summary>
    /// Uses the provider element retained when the model was created. Touching ProcessId before
    /// returning it keeps stale/provider failures explicit even in operation paths that probe
    /// optional properties or patterns inside narrow fallback catches.
    /// </summary>
    private IUIAutomationElement? GetAutomationElement(UiTarget uiTarget, UiElement element)
    {
        if (element.Context is { } context)
        {
            try
            {
                _ = s_getElementProcessId(context.AutomationElement);
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == UiaElementNotAvailable)
            {
                throw new InvalidOperationException(
                    $"Element {element.Id} is stale. Re-run 'inspect' or 'search'.",
                    ex);
            }
            return context.AutomationElement;
        }

        return ResolveComElement(uiTarget, element);
    }

    /// <summary>
    /// Re-finds a live COM UIA element from our serialized UiElement model.
    /// Uses slug-based resolution first (most precise), then falls back to
    /// AutomationId or Name+Type property matching.
    /// </summary>
    private IUIAutomationElement? ResolveComElement(UiTarget uiTarget, UiElement element)
    {
        Interlocked.Increment(ref _serializedElementResolutionCount);

        // Use the element's source HWND if it came from a different window (popup/dialog)
        IUIAutomationElement? root;
        if (element.WindowHandle is { } elHwnd && elHwnd != 0 && elHwnd != uiTarget.WindowHandle)
        {
            root = GetRootElementForHwnd((nint)elHwnd);
            _logger.LogDebug("Resolving element on source HWND {Hwnd}", elHwnd);
        }
        else
        {
            root = GetRootElement(uiTarget);
        }

        if (root is null)
        {
            return null;
        }

        // Try slug-based resolution first (most precise — uses RuntimeId hash)
        if (element.Selector is not null)
        {
            var (_, comElement) = FindElementBySlugWithCom(element.Selector, root);
            if (comElement is not null)
            {
                return comElement;
            }
        }

        // Fall back to AutomationId (stable but not unique across duplicates)
        if (element.AutomationId is not null)
        {
            var condition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(element.AutomationId));
            var found = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (found is not null)
            {
                return found;
            }
        }

        // Fall back to Name + ControlType
        if (element.Name is not null)
        {
            var typeId = MapControlType(element.Type);
            var nameCondition = _automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_NamePropertyId,
                ComVariant.Create(element.Name));

            IUIAutomationCondition condition;
            if (typeId != 0)
            {
                var typeCondition = _automation.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_ControlTypePropertyId,
                    ComVariant.Create(typeId));
                condition = _automation.CreateAndCondition(nameCondition, typeCondition);
            }
            else
            {
                condition = nameCondition;
            }

            var found = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all windows associated with an app: same-PID windows + cross-process owned windows.
    /// Excludes internal system windows (PseudoConsoleWindow, IME, etc.).
    /// </summary>
    private List<(nint Hwnd, int Pid, string Title)> GetAllAppWindows(UiTarget uiTarget)
    {
        return s_getAllAppWindows(this, uiTarget);
    }

    private List<(nint Hwnd, int Pid, string Title)> GetAllAppWindowsCore(UiTarget uiTarget)
    {
        var windows = FindWindowsByPid(uiTarget.ProcessId);

        // Remove internal system windows from same-PID results
        windows.RemoveAll(w => IsInternalWindow(UiTargetResolver.GetWindowClassName(w.Hwnd)));

        // Find cross-process owned windows (file pickers, system dialogs)
        var appHwnds = new HashSet<nint>(windows.Select(w => w.Hwnd));
        var hwnd = global::Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = global::Windows.Win32.PInvoke.FindWindowEx(
                global::Windows.Win32.Foundation.HWND.Null, hwnd, null, (string?)null);
            if (hwnd.IsNull) { break; }
            if (!global::Windows.Win32.PInvoke.IsWindowVisible(hwnd)) { continue; }
            if (appHwnds.Contains((nint)hwnd)) { continue; }

            var owner = global::Windows.Win32.PInvoke.GetWindow(hwnd,
                global::Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);
            if (!owner.IsNull && appHwnds.Contains((nint)owner))
            {
                // Skip internal system windows
                var className = UiTargetResolver.GetWindowClassName((nint)hwnd);
                if (IsInternalWindow(className)) { continue; }

                unsafe
                {
                    uint pid = 0;
                    global::Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                    var titleChars = new char[512];
                    fixed (char* buffer = titleChars)
                    {
                        var len = global::Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                        var title = len > 0 ? new string(buffer, 0, len) : "";
                        windows.Add(((nint)hwnd, (int)pid, title));
                    }
                }
            }
        }

        return windows;
    }

    /// <summary>Window classes that are internal system/framework windows with no useful UI elements.</summary>
    private static readonly HashSet<string> InternalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PseudoConsoleWindow",
        "IME",
        "MSCTFIME UI",
    };

    private static bool IsInternalWindow(string? className) =>
        className is not null && InternalWindowClasses.Contains(className);

    /// <summary>Get UIA root element for a specific HWND.</summary>
    private IUIAutomationElement? GetRootElementForHwnd(nint hwnd)
    {
        return s_getRootElementForHwnd(this, hwnd);
    }

    private IUIAutomationElement? GetRootElementForHwndCore(nint hwnd)
    {
        try
        {
            return s_elementFromHandle(this, hwnd);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Search for an element across all popup/owned windows of the app.
    /// Called when FindSingleElementAsync fails to find the element on the main window.
    /// </summary>
    private UiElement? FindElementOnOtherWindows(UiTarget uiTarget, UiSelector selector, CancellationToken ct)
    {
        return s_findElementOnOtherWindows(this, uiTarget, selector, ct);
    }

    private UiElement? FindElementOnOtherWindowsCore(UiTarget uiTarget, UiSelector selector, CancellationToken ct)
    {
        var allWindows = GetAllAppWindows(uiTarget);
        var mainHwnd = (nint)uiTarget.WindowHandle;

        foreach (var (hwnd, pid, title) in allWindows)
        {
            ct.ThrowIfCancellationRequested();
            if (hwnd == mainHwnd) { continue; }

            try
            {
            var windowRoot = GetRootElementForHwnd(hwnd);
            if (windowRoot is null) { continue; }

            _logger.LogDebug("Searching popup/owned window HWND {Hwnd} \"{Title}\"", hwnd, title);

            UiElement? found = null;

            if (selector.IsSlug)
            {
                found = FindElementBySlug(selector.Slug!, windowRoot);
            }
            else if (selector.Query is not null)
            {
                // Try exact AutomationId first
                var exactAidCondition = _automation.CreatePropertyCondition(
                    UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                    ComVariant.Create(selector.Query));
                var exactMatch = windowRoot.FindFirst(TreeScope.TreeScope_Descendants, exactAidCondition);
                if (exactMatch is not null)
                {
                    var nextId = 0;
                    found = ToUiElement(exactMatch, "", ref nextId);
                }
                else
                {
                    var matches = FindQueryMatches(windowRoot, selector, int.MaxValue, ct);
                    var recoveredExactMatch = FindExactAutomationIdMatch(matches, selector.Query);
                    if (recoveredExactMatch is not null)
                    {
                        var nextId = 0;
                        found = ToUiElement(recoveredExactMatch, "", ref nextId);
                    }
                    else if (matches.Count == 1)
                    {
                        var nextId = 0;
                        found = ToUiElement(matches[0], "", ref nextId);
                    }
                    else if (matches.Count > 1)
                    {
                        // Disambiguate: prefer the only invokable element
                        IUIAutomationElement? invokable = null;
                        int invokableCount = 0;
                        foreach (var match in matches)
                        {
                            if (IsInvokable(match)) { invokable = match; invokableCount++; }
                        }
                        if (invokableCount == 1 && invokable is not null)
                        {
                            var nextId = 0;
                            found = ToUiElement(invokable, "", ref nextId);
                        }
                    }
                }
            }

            if (found is not null)
            {
                found.WindowHandle = hwnd;
                _logger.LogDebug("Found element on HWND {Hwnd} \"{Title}\"", hwnd, title);
                return found;
            }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                _logger.LogDebug("COM error searching HWND {Hwnd}: {Message}", hwnd, ex.Message);
            }
        }

        return null;
    }

    private IUIAutomationElement? GetRootElement(UiTarget uiTarget)
    {
        return s_getRootElement(this, uiTarget);
    }

    private IUIAutomationElement? GetRootElementCore(UiTarget uiTarget)
    {
        // If we have a specific window handle, use it directly
        if (uiTarget.WindowHandle != 0)
        {
            try
            {
                var element = s_elementFromHandle(this, (nint)uiTarget.WindowHandle);
                if (element is not null)
                {
                    var name = SafeGetBstr(() => element.get_CurrentName());
                    _logger.LogDebug("ElementFromHandle(stored HWND {Hwnd}): \"{Name}\"", uiTarget.WindowHandle, name ?? "(null)");
                    return element;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Stored HWND {Hwnd} failed: {Error}", uiTarget.WindowHandle, ex.Message);
            }
        }

        var root = s_getDesktopRootElement(this);
        if (root is null)

        {

            return null;

        }

        var condition = _automation.CreatePropertyCondition(
            UIA_PROPERTY_ID.UIA_ProcessIdPropertyId,
            ComVariant.Create(uiTarget.ProcessId));

        var all = root.FindAll(TreeScope.TreeScope_Children, condition);
        var count = all?.get_Length() ?? 0;
        _logger.LogDebug("UIA FindAll for PID {Pid}: {Count} top-level elements", uiTarget.ProcessId, count);

        if (count > 0)
        {
            // Log all found elements
            for (int i = 0; i < count; i++)
            {
                var el = all!.GetElement(i);
                var name = SafeGetBstr(() => el.get_CurrentName());
                var rect = el.get_CurrentBoundingRectangle();
                _logger.LogDebug("  [{Index}] \"{Name}\" bounds=({L},{T},{R},{B})", i, name ?? "(null)", rect.left, rect.top, rect.right, rect.bottom);
            }

            if (count == 1)
            {
                return all!.GetElement(0);
            }

            // Multiple top-level elements — try matching by window title
            var titleQuery = uiTarget.WindowTitle;
            if (titleQuery is not null && !int.TryParse(titleQuery, out _))
            {
                for (int i = 0; i < count; i++)
                {
                    var el = all!.GetElement(i);
                    var name = SafeGetBstr(() => el.get_CurrentName());
                    if (name is not null && name.Contains(titleQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Matched window by query \"{Query}\": \"{Name}\"", titleQuery, name);
                        return el;
                    }
                }
            }

            // Fall back to largest bounds
            IUIAutomationElement? best = null;
            long bestArea = 0;
            for (int i = 0; i < count; i++)
            {
                var el = all!.GetElement(i);
                var r = el.get_CurrentBoundingRectangle();
                long area = (long)(r.right - r.left) * (r.bottom - r.top);
                if (area > bestArea) { best = el; bestArea = area; }
            }
            return best ?? all!.GetElement(0);
        }

        // PID-based search failed — fallback: find HWND via Process and use ElementFromHandle
        _logger.LogDebug("PID search returned 0 elements, trying ElementFromHandle fallback");
        try
        {
            var mainWindowHandle = s_getMainWindowHandleForProcessId(uiTarget.ProcessId);
            if (mainWindowHandle != 0)
            {
                var element = s_elementFromHandle(this, mainWindowHandle);
                if (element is not null)
                {
                    var name = SafeGetBstr(() => element.get_CurrentName());
                    _logger.LogDebug("ElementFromHandle found: \"{Name}\"", name ?? "(null)");
                    return element;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("ElementFromHandle failed: {Error}", ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Manual tree walk search using TreeWalker. Slower than FindAll but reliable —
    /// works around UIA FindAll bugs where WebView2 controls stall the tree traversal
    /// and cause sibling elements after the WebView to be skipped.
    /// </summary>
    private List<IUIAutomationElement> ManualTreeSearch(
        IUIAutomationElement root,
        string query,
        int maxResults,
        CancellationToken ct)
    {
        return s_manualTreeSearch(this, root, query, maxResults, ct);
    }

    private List<IUIAutomationElement> ManualTreeSearchCore(
        IUIAutomationElement root,
        string query,
        int maxResults,
        CancellationToken ct)
    {
        return ManualTreeSearchCore(
            root,
            maxResults,
            element =>
            {
                var name = SafeGetBstr(() => element.get_CurrentName());
                var aid = SafeGetBstr(() => element.get_CurrentAutomationId());
                return (aid is not null && aid.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (name is not null && name.Contains(query, StringComparison.OrdinalIgnoreCase));
            },
            ct);
    }

    private List<IUIAutomationElement> ManualTreeSearchByAutomationId(
        IUIAutomationElement root,
        string automationId,
        int maxResults,
        CancellationToken ct)
    {
        return ManualTreeSearchCore(
            root,
            maxResults,
            element => string.Equals(
                SafeGetBstr(() => element.get_CurrentAutomationId()),
                automationId,
                StringComparison.Ordinal),
            ct);
    }

    private List<IUIAutomationElement> ManualTreeSearchCore(
        IUIAutomationElement root,
        int maxResults,
        Func<IUIAutomationElement, bool> matches,
        CancellationToken ct)
    {
        var results = new List<IUIAutomationElement>();
        if (maxResults <= 0)
        {
            return results;
        }

        var walker = s_getControlViewWalker(this);
        var pending = new Stack<IUIAutomationElement>();
        TryPushTraversalElement(
            () => walker.GetFirstChildElement(root),
            pending,
            "first child of the search root");

        while (pending.Count > 0 && results.Count < maxResults)
        {
            ct.ThrowIfCancellationRequested();
            var element = pending.Pop();

            TryPushTraversalElement(
                () => walker.GetNextSiblingElement(element),
                pending,
                "next sibling");
            TryPushTraversalElement(
                () => walker.GetFirstChildElement(element),
                pending,
                "first child");

            if (matches(element))
            {
                results.Add(element);
            }
        }

        return results;
    }

    private void TryPushTraversalElement(
        Func<IUIAutomationElement?> getElement,
        Stack<IUIAutomationElement> pending,
        string relationship)
    {
        try
        {
            var element = getElement();
            if (element is not null)
            {
                pending.Push(element);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _logger.LogDebug("UIA Control View traversal could not read {Relationship}: {Message}", relationship, ex.Message);
        }
    }

    /// <summary>
    /// Uses the fast bulk descendant query first, then completes a result set that has not reached
    /// its requested cap with the reliable Control View walk. UIA providers can return a nonzero
    /// but incomplete bulk result, so zero-only fallback is insufficient.
    /// </summary>
    private List<IUIAutomationElement> FindAllDescendantMatches(
        IUIAutomationElement root,
        IUIAutomationCondition condition,
        int maxResults,
        Func<List<IUIAutomationElement>> manualSearch,
        bool completeEmptyResults = true)
    {
        var results = new List<IUIAutomationElement>();
        if (maxResults <= 0)
        {
            return results;
        }

        var bulkMatches = s_findAllDescendants(root, condition);
        if (bulkMatches is not null)
        {
            var bulkCount = Math.Min(bulkMatches.get_Length(), maxResults);
            for (var i = 0; i < bulkCount; i++)
            {
                results.Add(bulkMatches.GetElement(i));
            }
        }

        if (results.Count >= maxResults)
        {
            return results;
        }

        if (results.Count == 0 && !completeEmptyResults)
        {
            return results;
        }

        var bulkResultCount = results.Count;
        var identities = new HashSet<string>();
        var unidentifiedResults = new List<IUIAutomationElement>();
        foreach (var result in results)
        {
            var identity = TryGetElementIdentity(result);
            if (identity is not null)
            {
                identities.Add(identity);
            }
            else
            {
                unidentifiedResults.Add(result);
            }
        }

        foreach (var candidate in manualSearch())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var identity = TryGetElementIdentity(candidate);
            if (identity is not null)
            {
                if (!identities.Add(identity))
                {
                    continue;
                }

                if (ContainsElement(unidentifiedResults, candidate))
                {
                    identities.Remove(identity);
                    continue;
                }

                results.Add(candidate);
            }
            else if (!ContainsElement(results, candidate))
            {
                results.Add(candidate);
                unidentifiedResults.Add(candidate);
            }
        }

        if (results.Count > bulkResultCount)
        {
            _logger.LogDebug(
                "Control View walk supplemented {BulkCount} bulk matches with {AdditionalCount} omitted matches",
                bulkResultCount,
                results.Count - bulkResultCount);
        }

        return results;
    }

    private List<IUIAutomationElement> FindExactAutomationIdMatches(
        IUIAutomationElement root,
        string automationId,
        int maxResults,
        CancellationToken ct)
    {
        var condition = _automation.CreatePropertyCondition(
            UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
            ComVariant.Create(automationId));
        return FindAllDescendantMatches(
            root,
            condition,
            maxResults,
            () => ManualTreeSearchByAutomationId(root, automationId, maxResults, ct),
            completeEmptyResults: false);
    }

    private List<IUIAutomationElement> FindQueryMatches(
        IUIAutomationElement root,
        UiSelector selector,
        int maxResults,
        CancellationToken ct)
    {
        var condition = BuildCondition(selector);
        return condition is null || selector.Query is null
            ? []
            : FindAllDescendantMatches(
                root,
                condition,
                maxResults,
                () => ManualTreeSearch(root, selector.Query, maxResults, ct));
    }

    private List<IUIAutomationElement> FindPreferredQueryMatches(
        IUIAutomationElement root,
        UiSelector selector,
        int maxResults,
        CancellationToken ct)
    {
        var matches = FindQueryMatches(root, selector, int.MaxValue, ct);
        var preferredMatches = PreferExactAutomationIdMatches(matches, selector.Query!);
        return preferredMatches.Count <= maxResults
            ? preferredMatches
            : preferredMatches.GetRange(0, maxResults);
    }

    private static List<IUIAutomationElement> PreferExactAutomationIdMatches(
        List<IUIAutomationElement> matches,
        string automationId)
    {
        var exactMatches = matches
            .Where(match => string.Equals(
                SafeGetBstr(() => match.get_CurrentAutomationId()),
                automationId,
                StringComparison.Ordinal))
            .ToList();
        return exactMatches.Count > 0 ? exactMatches : matches;
    }

    private static IUIAutomationElement? FindExactAutomationIdMatch(
        IEnumerable<IUIAutomationElement> matches,
        string automationId)
    {
        return matches.FirstOrDefault(match => string.Equals(
            SafeGetBstr(() => match.get_CurrentAutomationId()),
            automationId,
            StringComparison.Ordinal));
    }

    private static unsafe string? TryGetElementIdentity(IUIAutomationElement element)
    {
        global::Windows.Win32.System.Com.SAFEARRAY* runtimeId = null;
        try
        {
            runtimeId = element.GetRuntimeId();
            if (runtimeId == null)
            {
                return null;
            }

            var count = (int)runtimeId->rgsabound[0].cElements;
            var data = (int*)runtimeId->pvData;
            if (count <= 0 || data is null)
            {
                return null;
            }

            var identity = new System.Text.StringBuilder(count * 12);
            for (var i = 0; i < count; i++)
            {
                identity.Append(data[i]).Append(';');
            }
            return identity.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (runtimeId != null)
            {
                _ = SafeArrayDestroy(runtimeId);
            }
        }
    }

    [LibraryImport("oleaut32.dll")]
    private static unsafe partial int SafeArrayDestroy(global::Windows.Win32.System.Com.SAFEARRAY* safeArray);

    private bool ContainsElement(List<IUIAutomationElement> elements, IUIAutomationElement candidate)
    {
        foreach (var element in elements)
        {
            try
            {
                if (s_compareElements(this, element, candidate))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                if (ReferenceEquals(element, candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private IUIAutomationCondition? BuildCondition(UiSelector selector)
    {
        if (selector.Query is not null)
        {
            // Plain text search: substring match on Name OR AutomationId (case-insensitive)
            var nameCondition = _automation.CreatePropertyConditionEx(
                UIA_PROPERTY_ID.UIA_NamePropertyId,
                ComVariant.Create(selector.Query),
                PropertyConditionFlags.PropertyConditionFlags_MatchSubstring | PropertyConditionFlags.PropertyConditionFlags_IgnoreCase);

            var autoIdCondition = _automation.CreatePropertyConditionEx(
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId,
                ComVariant.Create(selector.Query),
                PropertyConditionFlags.PropertyConditionFlags_MatchSubstring | PropertyConditionFlags.PropertyConditionFlags_IgnoreCase);

            return _automation.CreateOrCondition(nameCondition, autoIdCondition);
        }

        return null;
    }


    /// <summary>
    /// Checks if an element supports any invokable pattern (Invoke, Toggle, SelectionItem, ExpandCollapse).
    /// </summary>
    private static bool IsInvokable(IUIAutomationElement element)
    {
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_InvokePatternId);
            if (obj is IUIAutomationInvokePattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            if (obj is IUIAutomationTogglePattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionItemPatternId);
            if (obj is IUIAutomationSelectionItemPattern) { return true; }
        }
        catch { }
        try
        {
            var obj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            if (obj is IUIAutomationExpandCollapsePattern) { return true; }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Walks up the tree from an element to find the nearest ancestor that supports an invoke pattern.
    /// Stops at the root element to avoid walking past the target window.
    /// </summary>
    private IUIAutomationElement? FindInvokableAncestor(IUIAutomationElement element, IUIAutomationElement root)
    {
        return s_findInvokableAncestor(this, element, root);
    }

    private IUIAutomationElement? FindInvokableAncestorCore(IUIAutomationElement element, IUIAutomationElement root)
    {
        var walker = _automation.get_ControlViewWalker();
        var current = walker.GetParentElement(element);
        var maxDepth = 10; // prevent runaway walks

        while (current is not null && maxDepth-- > 0)
        {
            // Stop at the root window
            try
            {
                if (_automation.CompareElements(current, root))
                {
                    break;
                }
            }
            catch
            {
                break;
            }

            if (IsInvokable(current))
            {
                return current;
            }

            try
            {
                current = walker.GetParentElement(current);
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    private void WalkTree(IUIAutomationElement element, int maxDepth, int currentDepth, string path, List<UiElement> results, ref int nextElementId,
                          string? parentSelector = null, List<string>? ancestorTypes = null,
                          HashSet<nint>? topLevelWindowHandles = null, nint currentWindowHandle = 0)
    {
        if (currentDepth > 0 && topLevelWindowHandles is not null)
        {
            try
            {
                var hwnd = (nint)element.get_CurrentNativeWindowHandle();
                if (hwnd != 0 && hwnd != currentWindowHandle && topLevelWindowHandles.Contains(hwnd))
                {
                    return;
                }
            }
            catch (COMException)
            {
                // Keep walking when a provider cannot report the native handle. The independent
                // HWND is still emitted below, matching the previous best-effort COM behavior.
            }
        }

        var uiElement = ToUiElement(element, path, ref nextElementId);
        uiElement.Depth = currentDepth;
        uiElement.ParentSelector = parentSelector;
        if (ancestorTypes is { Count: > 0 })
        {
            uiElement.AncestorPath = ancestorTypes.ToArray();
        }
        results.Add(uiElement);

        if (currentDepth >= maxDepth)
        {
            // Peek for children so we can hint that the tree was truncated.
            try
            {
                var peekWalker = _automation.get_ControlViewWalker();
                if (peekWalker.GetFirstChildElement(element) is not null)
                {
                    uiElement.HasMoreChildren = true;
                }
            }
            catch { /* COM errors here are non-fatal — leave HasMoreChildren null */ }
            return;
        }

        var walker = _automation.get_ControlViewWalker();
        var child = walker.GetFirstChildElement(element);
        var childIndex = 0;

        // Build ancestor list for children: parent's ancestors + this element's type.
        var childAncestors = ancestorTypes is null ? new List<string>(currentDepth + 1) : new List<string>(ancestorTypes);
        childAncestors.Add(uiElement.Type);
        var childParentSelector = uiElement.Selector ?? uiElement.Id;

        while (child is not null)
        {
            var childPath = string.IsNullOrEmpty(path) ? $"/{childIndex}" : $"{path}/{childIndex}";
            WalkTree(
                child,
                maxDepth,
                currentDepth + 1,
                childPath,
                results,
                ref nextElementId,
                childParentSelector,
                childAncestors,
                topLevelWindowHandles,
                currentWindowHandle);

            IUIAutomationElement? next;
            try
            {
                next = walker.GetNextSiblingElement(child);
            }
            catch
            {
                next = null;
            }
            child = next;
            childIndex++;
        }
    }

    private static UiElement ToUiElement(IUIAutomationElement element, string path, ref int nextElementId)
    {
        var id = $"e{nextElementId++}";
        var rect = element.get_CurrentBoundingRectangle();
        var type = GetControlTypeName(element.get_CurrentControlType());
        var name = SafeGetBstr(() => element.get_CurrentName());
        var automationId = SafeGetBstr(() => element.get_CurrentAutomationId());

        // Try to get current value for editable elements (TextBox, ComboBox, etc.)
        string? value = null;
        try
        {
            var valuePattern = (IUIAutomationValuePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            var bstr = valuePattern.get_CurrentValue();
            var v = bstr.ToString();
            if (!string.IsNullOrEmpty(v))
            {
                value = v;
            }
        }
        catch { }

        // Try to get toggle state for checkboxes/toggles
        string? toggleState = null;
        try
        {
            var pattern = (IUIAutomationTogglePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId);
            toggleState = pattern.get_CurrentToggleState() switch
            {
                global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_On => "on",
                global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Off => "off",
                global::Windows.Win32.UI.Accessibility.ToggleState.ToggleState_Indeterminate => "indeterminate",
                _ => null
            };
        }
        catch { }

        // Try to get expand/collapse state
        string? expandState = null;
        try
        {
            var pattern = (IUIAutomationExpandCollapsePattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
            var state = pattern.get_CurrentExpandCollapseState();
            if (state != global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_LeafNode)
            {
                expandState = state switch
                {
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Expanded => "expanded",
                    global::Windows.Win32.UI.Accessibility.ExpandCollapseState.ExpandCollapseState_Collapsed => "collapsed",
                    _ => null
                };
            }
        }
        catch { }

        // Generate semantic slug from RuntimeId
        string? selector = null;
        try
        {
            unsafe
            {
                var runtimeId = element.GetRuntimeId();
                selector = SlugGenerator.GenerateSlugFromSafeArray(type, automationId, name, runtimeId);
            }
        }
        catch { }

        // Check scroll capability
        string? scrollDir = null;
        try
        {
            var sp = (IUIAutomationScrollPattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
            var v = (bool)sp.get_CurrentVerticallyScrollable();
            var h = (bool)sp.get_CurrentHorizontallyScrollable();
            if (v && h) { scrollDir = "vh"; }
            else if (v) { scrollDir = "v"; }
            else if (h) { scrollDir = "h"; }
        }
        catch { }

        // Detect any actionable UIA pattern; used by inspect --interactive to surface
        // truly clickable elements (including framework-Custom controls) instead of relying
        // on a hard-coded ControlType allowlist.
        var isInvokable = toggleState is not null
                       || expandState is not null
                       || HasPattern(element, UIA_PATTERN_ID.UIA_InvokePatternId)
                       || HasPattern(element, UIA_PATTERN_ID.UIA_SelectionItemPatternId);

        return new UiElement
        {
            Context = new UiElementContext(element),
            Id = id,
            Type = type,
            Name = name,
            AutomationId = automationId,
            ClassName = SafeGetBstr(() => element.get_CurrentClassName()),
            IsEnabled = element.get_CurrentIsEnabled(),
            IsOffscreen = element.get_CurrentIsOffscreen(),
            X = rect.left,
            Y = rect.top,
            Width = rect.right - rect.left,
            Height = rect.bottom - rect.top,
            Value = value,
            ToggleState = toggleState,
            ExpandState = expandState,
            ScrollDir = scrollDir,
            Selector = selector,
            IsInvokable = isInvokable,
        };
    }

    private static nint GetTopLevelWindowHandle(IUIAutomationElement element)
    {
        try
        {
            var native = element.get_CurrentNativeWindowHandle();
            if (native.IsNull) { return 0; }

            var root = global::Windows.Win32.PInvoke.GetAncestor(
                native,
                global::Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
            return root.IsNull ? (nint)native : (nint)root;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private nint ResolveTopLevelWindowHandle(IUIAutomationElement element)
    {
        var walker = _automation.get_ControlViewWalker();
        IUIAutomationElement? current = element;
        var remaining = 40;
        while (current is not null && remaining-- > 0)
        {
            var hwnd = GetTopLevelWindowHandle(current);
            if (hwnd != 0) { return hwnd; }

            try
            {
                current = walker.GetParentElement(current);
            }
            catch (COMException)
            {
                return 0;
            }
        }

        return 0;
    }

    private void SetResolvedWindowHandle(
        UiElement model,
        IUIAutomationElement element,
        long fallbackWindowHandle)
    {
        var hwnd = ResolveTopLevelWindowHandle(element);
        model.WindowHandle = hwnd != 0 ? hwnd : fallbackWindowHandle;
    }

    private static bool HasPattern(IUIAutomationElement element, UIA_PATTERN_ID patternId)
    {
        try
        {
            return element.GetCurrentPattern(patternId) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Promotes unique AutomationIds to selectors. When an element's AutomationId is unique
    /// across the full UIA tree, use it directly as the selector instead of a generated slug.
    /// AutomationIds are developer-set, stable across layout changes, and more readable.
    /// </summary>
    private void PromoteUniqueAutomationIds(
        IUIAutomationElement root,
        IList<UiElement> elements,
        long mainWindowHandle = 0,
        HashSet<nint>? promotableWindowHandles = null)
    {
        // Collect AutomationIds from the inspected elements that could be promoted
        var candidateAids = new HashSet<string>();
        foreach (var el in elements)
        {
            if (el.AutomationId is not null)
            {
                candidateAids.Add(el.AutomationId);
            }
        }

        if (candidateAids.Count == 0)
        {
            return;
        }

        // Build frequency map from the FULL tree to check global uniqueness
        var aidCounts = new Dictionary<string, int>();
        try
        {
            var allElements = root.FindAll(
                TreeScope.TreeScope_Descendants,
                _automation.CreateTrueCondition());

            if (allElements is not null)
            {
                var count = allElements.get_Length();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var aid = SafeGetBstr(() => allElements.GetElement(i).get_CurrentAutomationId());
                        if (aid is not null && candidateAids.Contains(aid))
                        {
                            aidCounts[aid] = aidCounts.GetValueOrDefault(aid) + 1;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("AutomationId uniqueness check failed: {Message}", ex.Message);
            return;
        }

        // Promote elements with globally unique AutomationIds
        // Skip elements from other windows — the frequency map only covers the main window tree
        foreach (var el in elements)
        {
            if (el.AutomationId is not null &&
                (promotableWindowHandles is not null
                    ? el.WindowHandle is { } hwnd && promotableWindowHandles.Contains((nint)hwnd)
                    : mainWindowHandle == 0 || el.WindowHandle == mainWindowHandle) &&
                aidCounts.TryGetValue(el.AutomationId, out var count) && count == 1)
            {
                el.Selector = el.AutomationId;
            }
        }
    }

    private static string? SafeGetBstr(Func<global::Windows.Win32.Foundation.BSTR> getter)
    {
        try
        {
            var bstr = getter();
            var val = bstr.ToString();
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch
        {
            return null;
        }
    }

    internal static string GetControlTypeName(UIA_CONTROLTYPE_ID controlType) => controlType switch
    {
        UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId => "Button",
        UIA_CONTROLTYPE_ID.UIA_CalendarControlTypeId => "Calendar",
        UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId => "CheckBox",
        UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId => "ComboBox",
        UIA_CONTROLTYPE_ID.UIA_EditControlTypeId => "Edit",
        UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId => "Hyperlink",
        UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId => "Image",
        UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId => "ListItem",
        UIA_CONTROLTYPE_ID.UIA_ListControlTypeId => "List",
        UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId => "Menu",
        UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId => "MenuBar",
        UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId => "MenuItem",
        UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId => "ProgressBar",
        UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId => "RadioButton",
        UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId => "ScrollBar",
        UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId => "Slider",
        UIA_CONTROLTYPE_ID.UIA_SpinnerControlTypeId => "Spinner",
        UIA_CONTROLTYPE_ID.UIA_StatusBarControlTypeId => "StatusBar",
        UIA_CONTROLTYPE_ID.UIA_TabControlTypeId => "Tab",
        UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId => "TabItem",
        UIA_CONTROLTYPE_ID.UIA_TextControlTypeId => "Text",
        UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId => "ToolBar",
        UIA_CONTROLTYPE_ID.UIA_ToolTipControlTypeId => "ToolTip",
        UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId => "Tree",
        UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId => "TreeItem",
        UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId => "Group",
        UIA_CONTROLTYPE_ID.UIA_ThumbControlTypeId => "Thumb",
        UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId => "DataGrid",
        UIA_CONTROLTYPE_ID.UIA_DataItemControlTypeId => "DataItem",
        UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId => "Document",
        UIA_CONTROLTYPE_ID.UIA_SplitButtonControlTypeId => "SplitButton",
        UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId => "Window",
        UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId => "Pane",
        UIA_CONTROLTYPE_ID.UIA_HeaderControlTypeId => "Header",
        UIA_CONTROLTYPE_ID.UIA_HeaderItemControlTypeId => "HeaderItem",
        UIA_CONTROLTYPE_ID.UIA_TableControlTypeId => "Table",
        UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId => "TitleBar",
        UIA_CONTROLTYPE_ID.UIA_SeparatorControlTypeId => "Separator",
        UIA_CONTROLTYPE_ID.UIA_AppBarControlTypeId => "AppBar",
        UIA_CONTROLTYPE_ID.UIA_SemanticZoomControlTypeId => "SemanticZoom",
        _ => $"Unknown({(int)controlType})"
    };

    internal static int MapControlType(string typeName) => typeName switch
    {
        "Button" => (int)UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId,
        "CheckBox" => (int)UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId,
        "ComboBox" => (int)UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId,
        "Edit" or "TextBox" => (int)UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
        "Hyperlink" => (int)UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId,
        "Image" => (int)UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId,
        "ListItem" => (int)UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId,
        "List" => (int)UIA_CONTROLTYPE_ID.UIA_ListControlTypeId,
        "Menu" => (int)UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId,
        "MenuBar" => (int)UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId,
        "MenuItem" => (int)UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId,
        "ProgressBar" => (int)UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId,
        "RadioButton" => (int)UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId,
        "ScrollBar" => (int)UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId,
        "Slider" => (int)UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId,
        "Tab" => (int)UIA_CONTROLTYPE_ID.UIA_TabControlTypeId,
        "TabItem" => (int)UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId,
        "Text" or "TextBlock" => (int)UIA_CONTROLTYPE_ID.UIA_TextControlTypeId,
        "ToolBar" => (int)UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId,
        "Tree" => (int)UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId,
        "TreeItem" => (int)UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId,
        "Group" => (int)UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId,
        "DataGrid" => (int)UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId,
        "Window" => (int)UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId,
        "Pane" => (int)UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId,
        "Table" => (int)UIA_CONTROLTYPE_ID.UIA_TableControlTypeId,
        "TitleBar" => (int)UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId,
        _ => 0
    };
}
