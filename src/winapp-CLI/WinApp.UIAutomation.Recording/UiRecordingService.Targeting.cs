// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

internal sealed partial class UiRecordingService
{
    /// <summary>Retargets capture to an element's popup or owned top-level window.</summary>
    internal static nint ResolvePopupCaptureHwnd(
        long? elementWindowHandle,
        nint targetWindowHwnd,
        ref int captureOriginLeft,
        ref int captureOriginTop,
        ref int srcWidth,
        ref int srcHeight,
        Func<nint, nint>? getAncestorRoot = null,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        if (!elementWindowHandle.HasValue || elementWindowHandle.Value == targetWindowHwnd)
        {
            return targetWindowHwnd;
        }

        var rawElementHwnd = (nint)elementWindowHandle.Value;
        nint elementOwnerHwnd;
        if (getAncestorRoot is not null)
        {
            var root = getAncestorRoot(rawElementHwnd);
            elementOwnerHwnd = root != 0 ? root : rawElementHwnd;
        }
        else
        {
            var rootHwnd = global::Windows.Win32.PInvoke.GetAncestor(
                new global::Windows.Win32.Foundation.HWND(rawElementHwnd),
                global::Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
            elementOwnerHwnd = rootHwnd.IsNull ? rawElementHwnd : (nint)rootHwnd;
        }

        if (elementOwnerHwnd == targetWindowHwnd)
        {
            return targetWindowHwnd;
        }

        if (getWindowRect is not null)
        {
            var (left, top, right, bottom) = getWindowRect(elementOwnerHwnd);
            captureOriginLeft = left;
            captureOriginTop = top;
            srcWidth = Math.Max(1, right - left);
            srcHeight = Math.Max(1, bottom - top);
        }
        else
        {
            global::Windows.Win32.PInvoke.GetWindowRect(
                new global::Windows.Win32.Foundation.HWND(elementOwnerHwnd),
                out var popupRect);
            captureOriginLeft = popupRect.left;
            captureOriginTop = popupRect.top;
            srcWidth = Math.Max(1, popupRect.right - popupRect.left);
            srcHeight = Math.Max(1, popupRect.bottom - popupRect.top);
        }

        return elementOwnerHwnd;
    }

    internal static bool IsElementOffscreen(
        double elementX,
        double elementY,
        double elementWidth,
        double elementHeight,
        int captureOriginLeft,
        int captureOriginTop,
        int sourceWidth,
        int sourceHeight)
    {
        if (elementWidth <= 0 || elementHeight <= 0)
        {
            return true;
        }

        var intersectionLeft = Math.Max((int)elementX, captureOriginLeft);
        var intersectionTop = Math.Max((int)elementY, captureOriginTop);
        var intersectionRight = Math.Min((int)elementX + (int)elementWidth, captureOriginLeft + sourceWidth);
        var intersectionBottom = Math.Min((int)elementY + (int)elementHeight, captureOriginTop + sourceHeight);
        return intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop;
    }

    /// <summary>Retargets capture to a resolved element window.</summary>
    internal static nint DeriveElementCaptureHwnd(
        nint targetWindowHwnd,
        ref int captureOriginLeft,
        ref int captureOriginTop,
        ref int srcWidth,
        ref int srcHeight,
        Func<nint> getElementTopLevelHwnd,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        var derived = getElementTopLevelHwnd();
        if (derived == 0 || derived == targetWindowHwnd)
        {
            return targetWindowHwnd;
        }

        if (getWindowRect is not null)
        {
            var (left, top, right, bottom) = getWindowRect(derived);
            captureOriginLeft = left;
            captureOriginTop = top;
            srcWidth = Math.Max(1, right - left);
            srcHeight = Math.Max(1, bottom - top);
        }
        else
        {
            global::Windows.Win32.PInvoke.GetWindowRect(
                new global::Windows.Win32.Foundation.HWND(derived),
                out var derivedRect);
            captureOriginLeft = derivedRect.left;
            captureOriginTop = derivedRect.top;
            srcWidth = Math.Max(1, derivedRect.right - derivedRect.left);
            srcHeight = Math.Max(1, derivedRect.bottom - derivedRect.top);
        }

        return derived;
    }
}
