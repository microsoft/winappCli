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
    /// Resolves the element's top-level native window by walking its UIA ancestors. Returns 0 when no
    /// ancestor exposes a native window handle. Used to retarget capture at the window an element
    /// actually lives in, which for popups and dialogs is not the session window.
    /// </summary>
    public nint ResolveElementTopLevelWindow(UiTarget target, UiElement element)
    {
        try
        {
            var comElement = ResolveComElement(target, element);
            if (comElement is null)
            {
                return 0;
            }

            var walker = _automation.get_ControlViewWalker();
            var current = comElement;
            var maxWalk = 40;
            while (current is not null && maxWalk-- > 0)
            {
                var native = current.get_CurrentNativeWindowHandle();
                if (!native.IsNull)
                {
                    var root = global::Windows.Win32.PInvoke.GetAncestor(
                        native,
                        global::Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
                    return root.IsNull ? (nint)native : (nint)root;
                }
                current = walker.GetParentElement(current);
            }

            return 0;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Deriving element top-level HWND failed; leaving capture on the target window");
            return 0;
        }
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
