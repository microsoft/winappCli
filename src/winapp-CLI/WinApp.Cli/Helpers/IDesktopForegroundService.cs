// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// The single place any <c>winapp ui</c> code path may change the shared desktop's foreground window or
/// restore a minimized window.
/// </summary>
/// <remarks>
/// <para>
/// These two Win32 calls are what make concurrent <c>winapp ui</c> processes interfere: they steal
/// focus, dismiss another workflow's transient menu, and invalidate a target another process has
/// already resolved. Routing every call site through one service means "is this coordinated?" is a
/// question about one file rather than about a dozen scattered <c>PInvoke.SetForegroundWindow</c>
/// calls, and a fake can assert ordering in unit tests without a live desktop.
/// </para>
/// <para>
/// Callers must already be inside a desktop section (holding <c>active.lock</c>). Cursor movement,
/// <c>SendInput</c> and synthetic pointer injection are reached only from
/// <see cref="Services.InteractiveDesktop.UiTurnMode.DesktopExclusive"/> command bodies, which enter a
/// section before touching them.
/// </para>
/// </remarks>
internal interface IDesktopForegroundService
{
    /// <summary>
    /// Requests that <paramref name="hwnd"/> becomes the foreground window. Windows may refuse
    /// (focus-stealing prevention, a UAC prompt, a locked session); callers must still verify with
    /// <see cref="IForegroundGuard"/> before injecting OS-wide input.
    /// </summary>
    void RequestForeground(long hwnd);

    /// <summary>
    /// Whether <paramref name="hwnd"/> (or its top-level root) currently holds the foreground.
    /// </summary>
    /// <remarks>
    /// <see cref="RequestForeground"/> is advisory — Windows silently refuses it under focus-stealing
    /// prevention, a UAC prompt, a locked session, or when another app activates itself in the same
    /// instant. Any code that reads the <em>screen</em> rather than a specific window (a screen-DC
    /// BitBlt) or injects OS-wide input must confirm the request actually took, immediately before it
    /// acts, or it will silently capture/type into whatever window really is in front.
    /// </remarks>
    bool IsForeground(long hwnd);

    /// <summary>Whether <paramref name="hwnd"/> is currently minimized.</summary>
    bool IsMinimized(long hwnd);

    /// <summary>Restores a minimized window so it can be captured or interacted with.</summary>
    void Restore(long hwnd);
}

/// <summary>Production <see cref="IDesktopForegroundService"/> over the Win32 window APIs.</summary>
/// <remarks>
/// Coverage ceiling (issue #630): every member is a direct Win32 call against the shared desktop.
/// Callers are covered through the interface with a fake.
/// </remarks>
internal sealed class DesktopForegroundService : IDesktopForegroundService
{
    public void RequestForeground(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)hwnd));
    }

    public bool IsForeground(long hwnd)
        => hwnd != 0 && ForegroundGuard.ForegroundBelongsTo(hwnd);

    public bool IsMinimized(long hwnd)
        => hwnd != 0 && Windows.Win32.PInvoke.IsIconic(new Windows.Win32.Foundation.HWND((nint)hwnd));

    public void Restore(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        Windows.Win32.PInvoke.ShowWindow(
            new Windows.Win32.Foundation.HWND((nint)hwnd),
            Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
    }
}
