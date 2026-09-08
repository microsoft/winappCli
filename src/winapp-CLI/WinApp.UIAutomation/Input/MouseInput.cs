// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Simulates mouse clicks at screen coordinates using SendInput.
/// </summary>
internal static class MouseInput
{
    internal delegate uint SendInputHook(INPUT[] inputs);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body is the innermost <c>SendInput</c> OS
    /// boundary, which cannot be unit-tested without moving/clicking the real desktop. Unit tests
    /// replace this delegate and cover batching, normalization, and error handling.
    /// </remarks>
    internal static SendInputHook s_sendInput = DefaultSendInput;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body moves the real cursor. Tests replace it
    /// so public methods can be exercised without mutating machine state.
    /// </remarks>
    internal static Func<int, int, bool> s_setCursorPos = DefaultSetCursorPos;

    /// <remarks>
    /// Native adapter seam for issue #630: virtual desktop metrics come from User32 and vary by
    /// machine. Tests inject deterministic dimensions for coordinate-normalization coverage.
    /// </remarks>
    internal static Func<global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX, int> s_getSystemMetrics =
        PInvoke.GetSystemMetrics;

    /// <remarks>
    /// Native adapter seam for issue #630: foreground detection probes the live desktop only when
    /// formatting a native SendInput failure message. Tests inject this predicate.
    /// </remarks>
    internal static Func<bool> s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;

    internal static Action<int> s_sleep = Thread.Sleep;

    private static unsafe uint DefaultSendInput(INPUT[] inputs)
    {
        fixed (INPUT* pInputs = inputs)
        {
            return PInvoke.SendInput((uint)inputs.Length, pInputs, sizeof(INPUT));
        }
    }

    private static bool DefaultSetCursorPos(int x, int y) => PInvoke.SetCursorPos(x, y);

    private static bool DefaultForegroundWindowIsNull() => PInvoke.GetForegroundWindow().IsNull;

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that exercises real mouse input (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_sendInput = DefaultSendInput;
        s_setCursorPos = DefaultSetCursorPos;
        s_getSystemMetrics = PInvoke.GetSystemMetrics;
        s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;
        s_sleep = Thread.Sleep;
    }

    /// <summary>
    /// Moves the cursor to the target position with a small wiggle to trigger hover/tooltip detection.
    /// Uses SendInput MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE to ensure apps see real WM_MOUSEMOVE messages.
    /// </summary>
    public static void Hover(int screenX, int screenY)
    {
        // Move to target via SendInput (absolute coordinates)
        SendMove(screenX, screenY);
        s_sleep(30);

        // Small wiggle (±2px) to ensure the app registers mouse entry
        SendMove(screenX + 2, screenY);
        s_sleep(20);
        SendMove(screenX, screenY + 2);
        s_sleep(20);

        // Return to center and stop — dwell timer starts now
        SendMove(screenX, screenY);
    }

    /// <summary>
    /// Moves the cursor to the target position (no button action). Lets a caller position the pointer
    /// and run its own settle + final confirm read before issuing the button-down via
    /// <see cref="Click"/> with <c>settleMs: 0</c>, closing the settle-window race.
    /// </summary>
    public static void MoveCursor(int screenX, int screenY)
    {
        s_setCursorPos(screenX, screenY);
    }

    public static void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50)
    {
        // Move cursor to the target position
        s_setCursorPos(screenX, screenY);
        if (settleMs > 0)
        {
            s_sleep(settleMs); // small delay for cursor settle
        }

        // Build input events
        var (downFlag, upFlag) = ButtonFlags(rightClick);

        // Single click
        SendClick(downFlag, upFlag);

        if (doubleClick)
        {
            s_sleep(50); // inter-click delay
            SendClick(downFlag, upFlag);
        }
    }

    public static void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0, int settleMs = 50)
    {
        var (downFlag, upFlag) = ButtonFlags(rightButton);

        // Settle on the start point, then press the button. Pass settleMs: 0 when the caller has already
        // moved the cursor to the from-point and confirmed the element hasn't drifted, so the button-down
        // happens immediately after that fresh check rather than reopening an unguarded settle window.
        SendMove(fromScreenX, fromScreenY);
        if (settleMs > 0)
        {
            s_sleep(settleMs);
        }
        SendButton(downFlag);

        var released = false;
        try
        {
            s_sleep(50);

            // Optional press-and-hold at the start before moving: drives long-press / press-and-hold
            // detection. With from == to (no movement) this is a pure long-press gesture.
            if (holdMs > 0)
            {
                s_sleep(holdMs);
            }

            // Move toward the destination in steps so the app sees a stream of WM_MOUSEMOVE messages
            foreach (var (x, y) in BuildDragPath(fromScreenX, fromScreenY, toScreenX, toScreenY))
            {
                SendMove(x, y);
                s_sleep(10);
            }

            // Optional dwell at the destination before releasing, so drop targets / merge overlays
            // that arm from a sustained hover (rather than the instant the cursor arrives) can latch.
            if (dwellMs > 0)
            {
                s_sleep(dwellMs);
            }

            s_sleep(50);
            SendButton(upFlag);
            released = true;
        }
        finally
        {
            // If a move or the up-event threw, make sure the button doesn't stay logically held down
            // (which would wreck the user's whole session). Best-effort — swallow a secondary failure.
            if (!released)
            {
                try { SendButton(upFlag); }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static void ScrollWheel(int screenX, int screenY, int delta, int settleMs = 30)
    {
        // Position the cursor over the target so the wheel message is routed to the element under it.
        // Pass settleMs: 0 when the caller already moved the cursor and confirmed the target is stable.
        SendMove(screenX, screenY);
        if (settleMs > 0)
        {
            s_sleep(settleMs);
        }

        SendInputs([CreateWheelInput(delta)]);
    }

    private static void SendButton(MOUSE_EVENT_FLAGS flag)
    {
        SendInputs([CreateButtonInput(flag)]);
    }

    private static void SendMove(int screenX, int screenY)
    {
        // Normalize against the full virtual desktop to support multi-monitor setups
        var metrics = ReadVirtualDesktopMetrics();
        SendInputs([CreateMoveInput(screenX, screenY, metrics)]);
    }

    private static void SendClick(MOUSE_EVENT_FLAGS downFlag, MOUSE_EVENT_FLAGS upFlag)
    {
        SendInputs(CreateClickInputs(downFlag, upFlag));
    }

    internal readonly record struct VirtualDesktopMetrics(int X, int Y, int Width, int Height);

    internal static VirtualDesktopMetrics ReadVirtualDesktopMetrics()
        => new(
            s_getSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            s_getSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            s_getSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            s_getSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    internal static (int X, int Y) NormalizeAbsolute(int screenX, int screenY, VirtualDesktopMetrics metrics)
        => (
            Math.Clamp((int)Math.Round(((screenX - metrics.X) * 65535.0) / Math.Max(metrics.Width - 1, 1)), 0, 65535),
            Math.Clamp((int)Math.Round(((screenY - metrics.Y) * 65535.0) / Math.Max(metrics.Height - 1, 1)), 0, 65535));

    internal static INPUT CreateMoveInput(int screenX, int screenY, VirtualDesktopMetrics metrics)
    {
        var (absoluteX, absoluteY) = NormalizeAbsolute(screenX, screenY, metrics);
        return new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = { mi = new MOUSEINPUT
            {
                dx = absoluteX,
                dy = absoluteY,
                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK
            }}
        };
    }

    internal static INPUT CreateButtonInput(MOUSE_EVENT_FLAGS flag)
        => new()
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = { mi = new MOUSEINPUT { dwFlags = flag } }
        };

    internal static INPUT[] CreateClickInputs(MOUSE_EVENT_FLAGS downFlag, MOUSE_EVENT_FLAGS upFlag)
        => [CreateButtonInput(downFlag), CreateButtonInput(upFlag)];

    internal static INPUT CreateWheelInput(int delta)
        => new()
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = { mi = new MOUSEINPUT
            {
                mouseData = unchecked((uint)delta),
                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL
            }}
        };

    internal static (MOUSE_EVENT_FLAGS Down, MOUSE_EVENT_FLAGS Up) ButtonFlags(bool rightButton)
        => rightButton
            ? (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP)
            : (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP);

    internal static IReadOnlyList<(int X, int Y)> BuildDragPath(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY)
    {
        const int steps = 20;
        var points = new List<(int X, int Y)>(steps);
        for (int i = 1; i <= steps; i++)
        {
            int x = fromScreenX + (int)Math.Round((toScreenX - fromScreenX) * (i / (double)steps));
            int y = fromScreenY + (int)Math.Round((toScreenY - fromScreenY) * (i / (double)steps));
            points.Add((x, y));
        }

        return points;
    }

    /// <summary>
    /// Dispatches the given input events via SendInput, throwing if the OS rejects them
    /// (e.g. the session is locked, or the target window is elevated and this process is not).
    /// </summary>
    private static void SendInputs(Span<INPUT> inputs)
    {
        var array = inputs.ToArray();
        var sent = s_sendInput(array);
        if (sent != (uint)array.Length)
        {
            throw new InvalidOperationException(sent == 0
                ? (s_foregroundWindowIsNull()
                    // No foreground window at all → the workstation is locked or on a secure
                    // desktop (LogonUI/UAC), where a user-session process simply can't inject.
                    // Don't blame elevation in that case.
                    ? "SendInput failed — no interactive desktop is available (the session is locked " +
                      "or on a secure desktop). Unlock the session and retry."
                    : "SendInput failed — the target window may be elevated (running as admin). " +
                      "Try running this CLI as administrator.")
                : $"SendInput delivered only {sent} of {array.Length} mouse events — the gesture was " +
                  "partially applied.");
        }
    }
}
