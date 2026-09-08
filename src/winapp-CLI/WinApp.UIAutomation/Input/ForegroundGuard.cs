// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Helpers for verifying that the window we're about to inject OS-wide input into is actually the
/// one the user targeted. <c>SendInput</c>-based gestures (send-keys via send-input, drag, scroll
/// --wheel, click, hover) land on whatever window is in the foreground / under the cursor — if
/// <c>SetForegroundWindow</c> silently failed (focus-stealing prevention, a UAC prompt, another app
/// grabbing focus, or the session being locked) the input would hit the wrong window or be dropped.
/// </summary>
public static class ForegroundGuard
{
    /// <remarks>
    /// Native adapter seam for issue #630: the default body reads the live foreground HWND from the
    /// interactive desktop. Tests inject deterministic handles to cover foreground classification
    /// without depending on desktop focus.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND> s_getForegroundWindow =
        global::Windows.Win32.PInvoke.GetForegroundWindow;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body walks Win32 HWND ancestry. Tests inject
    /// deterministic roots so no real windows are required.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, global::Windows.Win32.Foundation.HWND> s_getRootAncestor =
        DefaultGetRootAncestor;

    private static global::Windows.Win32.Foundation.HWND DefaultGetRootAncestor(global::Windows.Win32.Foundation.HWND hwnd) =>
        global::Windows.Win32.PInvoke.GetAncestor(hwnd, global::Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that reads the live foreground window (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_getForegroundWindow = global::Windows.Win32.PInvoke.GetForegroundWindow;
        s_getRootAncestor = DefaultGetRootAncestor;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the current foreground window is <paramref name="targetHwnd"/>
    /// or the top-level root window that owns it. A <paramref name="targetHwnd"/> of 0 (no resolvable
    /// window) is treated as "can't verify" and returns <see langword="false"/>.
    /// </summary>
    public static bool ForegroundBelongsTo(long targetHwnd)
    {
        if (targetHwnd == 0)
        {
            return false;
        }

        var foreground = s_getForegroundWindow();
        if (foreground.IsNull)
        {
            return false;
        }

        var target = new global::Windows.Win32.Foundation.HWND((nint)targetHwnd);
        if (foreground == target)
        {
            return true;
        }

        // The resolved element HWND is frequently a child / host window (a WinUI 3 input-site bridge,
        // a control HWND); the window that actually holds the foreground is its top-level root. Accept
        // only when the target's root window IS the foreground window. Compare by window ancestry, not
        // by owning process: a PID match would also accept a *different* top-level window of the same
        // process (common in multi-window apps) that merely happens to be foreground, which would let
        // the injection land on the wrong window.
        var targetRoot = s_getRootAncestor(target);
        return !targetRoot.IsNull && targetRoot == foreground;
    }

    /// <summary>
    /// Returns <see langword="true"/> when there is no foreground window at all — the signature of a
    /// locked workstation or a secure desktop (LogonUI / UAC), where a user-session process cannot
    /// inject input. Distinguishes "session locked" from "wrong window" / "elevated target".
    /// </summary>
    public static bool NoInteractiveDesktop()
        => s_getForegroundWindow().IsNull;

    /// <summary>
    /// Returns <see langword="true"/> when this process is running inside a remote session (Remote
    /// Desktop / Terminal Services), detected via <c>GetSystemMetrics(SM_REMOTESESSION)</c>. Synthetic
    /// pointer injection (<c>ui touch</c> / <c>ui pen</c> via <c>InjectSyntheticPointerInput</c>) is
    /// frequently accepted by the API — the call reports success — yet not routed to applications over
    /// the remote-desktop transport (pen in particular). Callers use this to attach an honest
    /// "delivery not guaranteed" advisory so a reported success is not mistaken for confirmed delivery.
    /// </summary>
    public static bool IsRemoteSession()
        => global::Windows.Win32.PInvoke.GetSystemMetrics(
               global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;

    /// <summary>
    /// Pure composition of the remote-session delivery advisory for synthetic pointer injection, or
    /// <see langword="null"/> when none is warranted (a local, physically-attached session). Kept
    /// side-effect-free (no PInvoke) so the message is unit-testable without a live remote session.
    /// </summary>
    /// <param name="isRemoteSession">Whether the current session is remote (see <see cref="IsRemoteSession"/>).</param>
    /// <param name="inputKind">Human word for the injected input, e.g. "touch" or "pen".</param>
    public static string? RemoteInjectionWarning(bool isRemoteSession, string inputKind)
        => isRemoteSession
            ? $"Injected in a remote/RDP session — synthetic {inputKind} input is often not delivered to the target " +
              "application over Remote Desktop (pen especially), so this success does not guarantee the gesture " +
              "reached the app. Verify the effect (e.g. 'ui screenshot' or 'ui inspect'). Delivery is reliable on a " +
              "local, physically-attached session."
            : null;

    /// <summary>
    /// Pure decision behind <c>TryEnsureForeground</c>: given whether there is a target window
    /// to verify, whether that target currently holds the foreground, and whether any foreground
    /// window exists at all, choose the outcome. Side-effect-free (no PInvoke) so the locked-desktop
    /// (<c>no_interactive_desktop</c>) vs. wrong-window (<c>foreground_not_target</c>) selection is
    /// unit-testable without a live desktop.
    /// </summary>
    internal static ForegroundCheck Classify(bool hasTarget, bool targetIsForeground, bool anyForegroundWindow)
    {
        if (!hasTarget || targetIsForeground)
        {
            return ForegroundCheck.Proceed;
        }

        return anyForegroundWindow ? ForegroundCheck.ForegroundNotTarget : ForegroundCheck.NoInteractiveDesktop;
    }

    /// <summary>
    /// Verifies the target is foreground before an OS-wide injection. A <paramref name="targetHwnd"/>
    /// of 0 means there is no window to verify against (e.g. a bare coordinate target) and is allowed
    /// through. Distinguishes a locked / secure desktop
    /// (<see cref="ForegroundCheck.NoInteractiveDesktop"/>) from another window holding the foreground
    /// (<see cref="ForegroundCheck.ForegroundNotTarget"/>), so callers never report the misleading
    /// "target may be elevated" cause for a simply-locked session.
    /// </summary>
    public static ForegroundCheck CheckForeground(long targetHwnd)
    {
        bool hasTarget = targetHwnd != 0;
        return Classify(
            hasTarget,
            targetIsForeground: hasTarget && ForegroundBelongsTo(targetHwnd),
            anyForegroundWindow: !NoInteractiveDesktop());
    }
}
