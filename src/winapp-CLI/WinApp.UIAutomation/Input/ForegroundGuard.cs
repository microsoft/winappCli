// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Helpers for verifying that the window we're about to act on is actually the one the user targeted.
/// </summary>
/// <remarks>
/// <para>
/// There are two questions here, and they have different right answers.
/// </para>
/// <para>
/// <strong>Injection</strong> — <see cref="ForegroundBelongsTo"/>, <see cref="CheckForeground"/>.
/// <c>SendInput</c>-based gestures (send-keys via send-input, drag, scroll --wheel, click, hover) land
/// on whatever window is in the foreground / under the cursor. If <c>SetForegroundWindow</c> silently
/// failed (focus-stealing prevention, a UAC prompt, another app grabbing focus, or the session being
/// locked) the input would hit the wrong window or be dropped. This check is strict on purpose: it
/// accepts only the target itself or its top-level root, because a dialog sitting in front would
/// swallow the keystrokes meant for the window behind it.
/// </para>
/// <para>
/// <strong>Live-screen capture</strong> — <see cref="ForegroundIsCapturableFor"/>. Reading pixels from
/// the screen wants the opposite treatment for that same dialog: a modal dialog the target owns is
/// part of that app's UI and is sitting on the very pixels being captured, which is the reason to read
/// the screen rather than the window. So the capture predicate additionally accepts a foreground
/// window whose <c>GW_OWNER</c> chain reaches the target, within a bound. It is still a real check —
/// an unrelated window from another app has no owner path to the target and is refused, so a capture
/// can never quietly return somebody else's window labelled as yours.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// Native adapter seam: the default body takes one <c>GW_OWNER</c> hop. Tests inject deterministic
    /// owner links so an owned-dialog foreground can be reproduced without real windows.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, global::Windows.Win32.Foundation.HWND> s_getOwner =
        DefaultGetOwner;

    private static global::Windows.Win32.Foundation.HWND DefaultGetOwner(global::Windows.Win32.Foundation.HWND hwnd) =>
        global::Windows.Win32.PInvoke.GetWindow(hwnd, global::Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);

    /// <summary>How many <c>GW_OWNER</c> hops are followed before giving up.</summary>
    /// <remarks>
    /// Owner links are a chain, not a tree — a file picker owned by a document window owned by the
    /// main frame is three deep. The bound stops a corrupted or cyclic chain from spinning; anything
    /// deeper than this is not a dialog relationship worth trusting.
    /// </remarks>
    private const int MaxOwnerHops = 8;

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that reads the live foreground window (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_getForegroundWindow = global::Windows.Win32.PInvoke.GetForegroundWindow;
        s_getRootAncestor = DefaultGetRootAncestor;
        s_getOwner = DefaultGetOwner;
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
    /// Whether the current foreground window is close enough to <paramref name="targetHwnd"/> that a
    /// live-screen capture of the target's rectangle would show the target's own UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately looser than <see cref="ForegroundBelongsTo"/>, and only for capture. Injection has
    /// to be strict: if a dialog is in front, keystrokes land on the dialog, so accepting it would type
    /// into the wrong window. Capture is the opposite case — a modal dialog the target owns is part of
    /// that app's UI and is sitting on the pixels being read, which is precisely what
    /// <c>--capture-screen</c> exists to record. Rejecting it would fail the documented
    /// <c>-w &lt;hwnd&gt;</c> recovery for the most ordinary reason a window is not foreground.
    /// </para>
    /// <para>
    /// It stays a real check. An owned dialog is its own <c>GA_ROOT</c>, so root ancestry alone can
    /// never see it; the owner chain is what proves the relationship. An unrelated window from another
    /// app has no owner path to the target, so the case this guard exists for — capturing somebody
    /// else's window and labelling it as the target's — is still refused.
    /// </para>
    /// <para>
    /// Public rather than internal, deliberately. Sharing it with the sibling Recording package via
    /// <c>InternalsVisibleTo</c> was tried first and does not work: both assemblies generate their own
    /// internal CsWin32 <c>PInvoke</c> type, so making one's internals visible to the other makes that
    /// type ambiguous (CS0436, fatal under the Release warnings-as-errors settings). The alternative
    /// was a second copy of a safety check, which is worse than one more method on a guard class that
    /// already exposes <see cref="ForegroundBelongsTo"/> and <see cref="CheckForeground"/>.
    /// </para>
    /// </remarks>
    public static bool ForegroundIsCapturableFor(long targetHwnd)
    {
        if (ForegroundBelongsTo(targetHwnd))
        {
            return true;
        }

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
        var targetRoot = s_getRootAncestor(target);

        // Walk from the foreground window outwards: the dialog names its owner, not the other way
        // round, so this is the only direction the relationship can be read in.
        var owner = s_getOwner(foreground);
        for (var hop = 0; hop < MaxOwnerHops && !owner.IsNull; hop++)
        {
            if (owner == target || (!targetRoot.IsNull && owner == targetRoot))
            {
                return true;
            }

            owner = s_getOwner(owner);
        }

        return false;
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
