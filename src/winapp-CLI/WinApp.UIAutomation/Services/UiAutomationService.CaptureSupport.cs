// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Public window-resolution and raw-capture primitives. These expose, without any COM types in the
/// signatures, the pieces a caller needs to drive its own capture loop (for example video recording,
/// which samples frames at a fixed cadence and encodes them itself).
/// </summary>
internal sealed partial class UiAutomationService
{
    /// <summary>
    /// Resolves the target's root UIA window. Returns <see langword="false"/> when no UIA window
    /// exists for the target. <paramref name="hwnd"/> is 0 when the root element has no native
    /// window handle, in which case callers should fall back to
    /// <see cref="UiTarget.WindowHandle"/>.
    /// </summary>
    public bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title)
    {
        hwnd = 0;
        title = null;

        var root = GetRootElement(target);
        if (root is null)
        {
            return false;
        }

        title = SafeGetBstr(() => root.get_CurrentName());
        var native = root.get_CurrentNativeWindowHandle();
        hwnd = native.IsNull ? 0 : (nint)native;
        return true;
    }

    /// <summary>
    /// Resolves the top-level native window an element is drawn in. Returns 0 when no UIA ancestor
    /// exposes a native window handle. Used to retarget capture at the window an element actually
    /// lives in, which for popups and dialogs is not the session window.
    /// </summary>
    public nint ResolveElementTopLevelWindow(UiTarget target, UiElement element)
    {
        try
        {
            var comElement = GetAutomationElement(target, element);
            if (comElement is null)
            {
                return 0;
            }

            return ResolveElementCaptureWindow(comElement);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Deriving element top-level HWND failed; leaving capture on the target window");
            return 0;
        }
    }

    /// <summary>How many <c>GW_OWNER</c> hops may separate a windowed popup from its owner.</summary>
    private const int MaxPopupOwnerHops = 8;

    /// <summary>How many UIA nodes of one candidate popup window are read before giving up on it.</summary>
    private const int MaxPopupHostNodes = 256;

    /// <summary>
    /// The element's top-level window from its UIA ancestors, or the windowed popup that hosts it.
    /// </summary>
    /// <remarks>
    /// A XAML windowed popup (a flyout, menu, tooltip or teaching tip backed by
    /// <c>Xaml_WindowedPopupClass</c>) draws in its own owned top-level window, but the UIA parent of
    /// its content is the XAML island inside the owner. The ancestor walk therefore names the owner,
    /// and capturing it records whatever is behind the popup.
    /// </remarks>
    private nint ResolveElementCaptureWindow(IUIAutomationElement element)
    {
        var hwnd = ResolveTopLevelWindowHandle(element);
        if (hwnd == 0)
        {
            return 0;
        }

        var popupHost = FindWindowedPopupHost(hwnd, element);
        return popupHost != 0 ? popupHost : hwnd;
    }

    /// <summary>
    /// Finds the one popup window of <paramref name="ownerHwnd"/> whose UIA content includes
    /// <paramref name="element"/>, or 0.
    /// </summary>
    private nint FindWindowedPopupHost(nint ownerHwnd, IUIAutomationElement element)
    {
        HashSet<string>? contentIds = null;
        try
        {
            var host = SelectWindowedPopupHost(
                ownerHwnd,
                EnumerateTopLevelWindows(),
                static hwnd => RealOwnedWindowFinder.s_isWindowVisible(new global::Windows.Win32.Foundation.HWND(hwnd)),
                static hwnd => (nint)RealOwnedWindowFinder.s_getWindowOwner(new global::Windows.Win32.Foundation.HWND(hwnd)),
                static hwnd => RealOwnedWindowFinder.s_getWindowProcessId(new global::Windows.Win32.Foundation.HWND(hwnd)),
                candidate =>
                {
                    // Read lazily: most windows own no popups, so most captures never need it.
                    contentIds ??= CollectWindowlessIdentities(element);
                    return contentIds.Count > 0 && WindowHostsAny(candidate, contentIds);
                });

            if (host != 0)
            {
                _logger.LogDebug(
                    "Element content is hosted by popup window HWND 0x{Popup:X} owned by HWND 0x{Owner:X}",
                    host, ownerHwnd);
            }

            return host;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Searching for a windowed popup host failed; keeping HWND 0x{Owner:X}", ownerHwnd);
            return 0;
        }
    }

    /// <summary>
    /// Picks the window to capture for content whose UIA ancestors resolve to
    /// <paramref name="ownerHwnd"/>.
    /// </summary>
    /// <returns>
    /// The only visible window that belongs to the owner's process, has a <c>GW_OWNER</c> chain
    /// reaching the owner, and hosts the element; otherwise 0.
    /// </returns>
    /// <remarks>
    /// Other processes' windows are never candidates, so another app cannot redirect the capture by
    /// owning a window or exposing matching content. When more than one window qualifies the answer
    /// is ambiguous and the caller keeps the owner rather than guessing.
    /// </remarks>
    internal static nint SelectWindowedPopupHost(
        nint ownerHwnd,
        IEnumerable<nint> topLevelWindows,
        Func<nint, bool> isVisible,
        Func<nint, nint> getOwner,
        Func<nint, int> getProcessId,
        Func<nint, bool> hostsElement)
    {
        var ownerProcessId = getProcessId(ownerHwnd);
        if (ownerProcessId == 0)
        {
            return 0;
        }

        nint host = 0;
        foreach (var candidate in topLevelWindows)
        {
            if (candidate == 0
                || candidate == ownerHwnd
                || !isVisible(candidate)
                || getProcessId(candidate) != ownerProcessId
                || !IsOwnedBy(candidate, ownerHwnd, getOwner)
                || !hostsElement(candidate))
            {
                continue;
            }

            if (host != 0)
            {
                return 0;
            }

            host = candidate;
        }

        return host;
    }

    private static bool IsOwnedBy(nint window, nint ownerHwnd, Func<nint, nint> getOwner)
    {
        var owner = getOwner(window);
        for (var hop = 0; hop < MaxPopupOwnerHops && owner != 0; hop++)
        {
            if (owner == ownerHwnd)
            {
                return true;
            }

            owner = getOwner(owner);
        }

        return false;
    }

    private static IEnumerable<nint> EnumerateTopLevelWindows()
    {
        var hwnd = global::Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = RealOwnedWindowFinder.s_findNextTopLevelWindow(hwnd);
            if (hwnd.IsNull)
            {
                yield break;
            }

            yield return hwnd;
        }
    }

    /// <summary>
    /// RuntimeIds of the element and its ancestors up to the first one backed by a native window.
    /// </summary>
    /// <remarks>
    /// Window-backed elements are excluded because their RuntimeIds derive from the HWND and are not
    /// unique to one subtree: a XAML popup window's root reports the same RuntimeId as the island it
    /// belongs to.
    /// </remarks>
    private HashSet<string> CollectWindowlessIdentities(IUIAutomationElement element)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var walker = s_getControlViewWalker(this);
        IUIAutomationElement? current = element;
        for (var remaining = 40; current is not null && remaining > 0; remaining--)
        {
            if (!current.get_CurrentNativeWindowHandle().IsNull)
            {
                break;
            }

            if (TryGetElementIdentity(current) is { } id)
            {
                ids.Add(id);
            }

            current = walker.GetParentElement(current);
        }

        return ids;
    }

    /// <summary>
    /// Whether the UIA subtree under <paramref name="hwnd"/> contains an element with one of
    /// <paramref name="runtimeIds"/>. Reads breadth-first, so a popup's content root is checked first.
    /// </summary>
    private bool WindowHostsAny(nint hwnd, HashSet<string> runtimeIds)
    {
        IUIAutomationElement? root;
        try
        {
            root = s_elementFromHandle(this, hwnd);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        try
        {
            var walker = s_getControlViewWalker(this);
            var pending = new Queue<IUIAutomationElement>();
            pending.Enqueue(root);
            var visited = 0;
            while (pending.TryDequeue(out var parent))
            {
                var child = walker.GetFirstChildElement(parent);
                while (child is not null)
                {
                    if (++visited > MaxPopupHostNodes)
                    {
                        return false;
                    }

                    if (TryGetElementIdentity(child) is { } id && runtimeIds.Contains(id))
                    {
                        return true;
                    }

                    pending.Enqueue(child);
                    child = walker.GetNextSiblingElement(child);
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // A window that cannot be read (a closing popup, a provider that rejects the client) is
            // not evidence that it hosts the element.
        }

        return false;
    }

    /// <summary>
    /// The window's bounds excluding the invisible DWM resize border, falling back to
    /// <paramref name="fallback"/> when the extended frame bounds are unavailable.
    /// </summary>
    public PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback)
    {
        var rect = GetVisibleWindowRect(
            new global::Windows.Win32.Foundation.HWND(hwnd),
            new global::Windows.Win32.Foundation.RECT
            {
                left = fallback.Left,
                top = fallback.Top,
                right = fallback.Right,
                bottom = fallback.Bottom,
            });

        return new PointerRect(rect.left, rect.top, rect.right, rect.bottom);
    }
}
