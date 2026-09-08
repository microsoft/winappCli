// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

using System.Runtime.Versioning;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Injects synthetic touch and pen input using the Windows pointer-injection APIs. Touch prefers the
/// synthetic-pointer device (<c>CreateSyntheticPointerDevice(PT_TOUCH)</c>/
/// <c>InjectSyntheticPointerInput</c>) — the same mechanism the pen path uses — and falls back to the
/// legacy <c>InitializeTouchInjection</c>/<c>InjectTouchInput</c> API when a synthetic touch device
/// cannot be created. Pen uses <c>CreateSyntheticPointerDevice(PT_PEN)</c>. Coordinates are screen
/// pixels — the same space <c>ui inspect</c> reports.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class PointerInput
{
    /// <summary>Maximum simultaneous touch contacts we register with the injection subsystem.</summary>
    private const uint MaxContacts = 10;

    /// <summary>Pen pressure range used by the pointer APIs (0..1024).</summary>
    private const uint PenPressureMax = 1024;

    /// <summary>Steps used to interpolate a moving gesture between two waypoints.</summary>
    private const int GlideSteps = 20;

    /// <summary>
    /// Interval in milliseconds between stationary UPDATE frames emitted during a touch long-press hold.
    /// Windows touch injection can cancel or mis-classify a held contact that receives no periodic
    /// frames; this cadence keeps the contact alive and lets the OS recognise it as press-and-hold.
    /// </summary>
    internal const int HoldFrameIntervalMs = 40;

    // touchFlags / touchMask / penFlags / penMask are raw DWORD bitmasks in the generated structs.
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint PEN_FLAG_NONE = 0x00000000;
    private const uint PEN_FLAG_INVERTED = 0x00000002;
    private const uint PEN_FLAG_ERASER = 0x00000004;
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;

    private static readonly object InitLock = new();
    private static volatile bool _touchInitialized;

    /// <summary>Delegate that submits one frame of touch contacts (synthetic device or legacy API).</summary>
    internal delegate void TouchSender(POINTER_TOUCH_INFO[] contacts);

    /// <summary>
    /// Registers this process for legacy touch injection the first time it is needed. Idempotent — the
    /// OS only allows a single successful <c>InitializeTouchInjection</c> per process. On failure the
    /// actual Win32 error is surfaced so callers can tell "unsupported" from "locked desktop".
    /// </summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the <c>InitializeTouchInjection</c> P/Invoke and its Win32
    /// failure branch require a touch-capable, unlocked interactive desktop and cannot be exercised in
    /// the shared CI/test environment without live input injection. The logic that drives this path is
    /// covered deterministically through the injectable <see cref="TouchSender"/> seam
    /// (see PointerInputFrameTests); only the native call is un-coverable here.
    /// </remarks>
    private static void EnsureTouchInitialized()
    {
        if (_touchInitialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_touchInitialized)
            {
                return;
            }

            // TOUCH_FEEDBACK_NONE — suppress the OS touch-visual so automation stays invisible.
            if (!PInvoke.InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_NONE))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"InitializeTouchInjection failed (Win32 error {err}: {Win32Message(err)}) — touch " +
                    "injection is unsupported or unavailable on this desktop. This usually means the " +
                    "device/driver does not support injected touch, or the session is locked / on a secure desktop.");
            }

            _touchInitialized = true;
        }
    }

    /// <summary>Formats a Win32 error code into its system message for honest diagnostics.</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): only reached from the native injection error branches
    /// (<c>InitializeTouchInjection</c> / <c>InjectTouchInput</c> / <c>InjectSyntheticPointerInput</c>
    /// failures), which require a real interactive desktop and live input injection to trigger, so this
    /// helper is transitively un-coverable in the shared CI/test environment.
    /// </remarks>
    private static string Win32Message(int error)
    {
        try { return new Win32Exception(error).Message; }
        catch { return "unknown error"; }
    }

    /// <summary>Injects a synthetic touch gesture, preferring the modern synthetic-pointer device and
    /// falling back to the legacy <c>InjectTouchInput</c> API when it is unavailable.</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the native device path
    /// (<c>CreateSyntheticPointerDevice</c> → inject → <c>DestroySyntheticPointerDevice</c>) and the
    /// legacy fallback both require a touch-capable, unlocked interactive desktop and cannot run in the
    /// shared CI/test environment without live input injection. The gesture-planning and frame-ordering
    /// logic is covered deterministically through the injectable <see cref="TouchSender"/> seam on
    /// <see cref="InjectTouchStroke"/> (see PointerInputFrameTests); only the OS injection calls are
    /// un-coverable here.
    /// </remarks>
    public static void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs)
    {
        // NOTE (M2 — P/Invoke test coverage): The production path below (CreateSyntheticPointerDevice
        // → RunTouchGesture → InjectSyntheticPointerInput / InjectTouchInput → DestroySyntheticPointerDevice)
        // requires an unlocked, interactive desktop and cannot be exercised in this shared CI/test
        // environment without live input injection. Unit tests in PointerInputFrameTests cover the
        // frame-planning and ordering logic via InjectTouchStroke's injectable TouchSender delegate;
        // the P/Invoke device-create → payload-marshal → destroy path requires a dedicated
        // interactive-desktop test lane.
        // Primary path: a synthetic touch pointer device, mirroring the working pen path. This is the
        // modern, better-supported mechanism (Windows 10 1809+).
        var device = PInvoke.CreateSyntheticPointerDevice(
            POINTER_INPUT_TYPE.PT_TOUCH, MaxContacts, POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE);

        if (!device.IsNull)
        {
            try
            {
                RunTouchGesture(gesture, contactPaths, holdMs, durationMs,
                    contacts => SendSyntheticTouch(device, contacts));
                return;
            }
            finally
            {
                PInvoke.DestroySyntheticPointerDevice(device);
            }
        }

        int createErr = Marshal.GetLastPInvokeError();

        // Fallback path: the legacy touch-injection API. EnsureTouchInitialized surfaces an honest
        // Win32 error if even this is unsupported.
        try
        {
            EnsureTouchInitialized();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Synthetic touch injection is unsupported (CreateSyntheticPointerDevice(PT_TOUCH) failed, " +
                $"Win32 error {createErr}: {Win32Message(createErr)}). {ex.Message}");
        }

        RunTouchGesture(gesture, contactPaths, holdMs, durationMs, SendLegacyTouch);
    }

    /// <summary>
    /// Win32 error code 21 (ERROR_NOT_READY): the touch-injection subsystem is temporarily
    /// busy processing a previous frame. Calls less than ~0.1ms apart can return this code
    /// and must retry the identical frame rather than treating it as permanent failure.
    /// </summary>
    private const int ErrorNotReady = 21;

    /// <summary>Maximum number of ERROR_NOT_READY retries per frame before giving up.</summary>
    internal const int MaxErrorNotReadyRetries = 10;

    /// <summary>
    /// Submits one touch frame, retrying up to <see cref="MaxErrorNotReadyRetries"/> times when
    /// Win32 error 21 (ERROR_NOT_READY) is returned. Any other error propagates immediately.
    /// The final attempt is unguarded so the exception surfaces after all retries are exhausted.
    /// </summary>
    private static void SendFrameWithRetry(TouchSender send, POINTER_TOUCH_INFO[] contacts)
    {
        for (int attempt = 0; attempt < MaxErrorNotReadyRetries; attempt++)
        {
            try
            {
                send(contacts);
                return;
            }
            catch (InvalidOperationException ex) when (IsWin32ErrorNotReady(ex))
            {
                Thread.Sleep(1);
            }
        }
        send(contacts); // final attempt — let any exception propagate
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> was thrown because the
    /// touch-injection API returned Win32 error 21 (ERROR_NOT_READY). The message format
    /// produced by <see cref="SendLegacyTouch"/> and <see cref="SendSyntheticTouch"/> includes
    /// <c>"Win32 error 21:"</c>.
    /// </summary>
    internal static bool IsWin32ErrorNotReady(InvalidOperationException ex)
        => ex.Message.Contains("Win32 error 21:", StringComparison.Ordinal);

    /// <summary>
    /// Runs the touch gesture loop (handles double-tap repetition) against the given
    /// <paramref name="send"/> delegate. Exposed internally so tests can inject a recording
    /// sender and an injectable inter-tap sleep to assert the repetition contract.
    /// </summary>
    internal static void RunTouchGesture(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send,
        Action<int>? sleepInter = null)
    {
        var sleepFn = sleepInter ?? Thread.Sleep;
        int repeats = gesture == TouchGesture.DoubleTap ? 2 : 1;
        for (int r = 0; r < repeats; r++)
        {
            InjectTouchStroke(contactPaths, holdMs, durationMs,
                (contacts) => SendFrameWithRetry(send, contacts));
            if (r + 1 < repeats)
            {
                sleepFn(60); // inter-tap gap for double-tap
            }
        }
    }

    internal static void InjectTouchStroke(
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send,
        Action<int>? sleep = null,
        Func<long>? nowMs = null)
    {
        int count = contactPaths.Count;
        var contacts = new POINTER_TOUCH_INFO[count];

        // --- Press down ---
        for (int i = 0; i < count; i++)
        {
            var start = contactPaths[i][0];
            contacts[i] = MakeContact(
                (uint)i, start.X, start.Y,
                POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                primary: i == 0);
        }
        send(contacts);

        bool released = false;
        try
        {
            // --- Hold phase: emit periodic stationary UPDATE frames so Windows does not drop/cancel
            //     the contact or mis-classify the hold as a tap. One frame per HoldFrameIntervalMs,
            //     clamping the final partial interval so the total hold ≈ holdMs. ---
            if (holdMs > 0)
            {
                int elapsed = 0;
                while (elapsed < holdMs)
                {
                    int interval = Math.Min(HoldFrameIntervalMs, holdMs - elapsed);
                    Thread.Sleep(interval);
                    elapsed += interval;

                    for (int i = 0; i < count; i++)
                    {
                        var start = contactPaths[i][0];
                        contacts[i] = MakeContact(
                            (uint)i, start.X, start.Y,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                            primary: i == 0);
                    }
                    send(contacts);
                }
            }

            // --- Glide between waypoints (only if any contact has more than one waypoint) ---
            int maxWaypoints = 0;
            foreach (var path in contactPaths)
            {
                maxWaypoints = Math.Max(maxWaypoints, path.Count);
            }

            if (maxWaypoints > 1)
            {
                if (durationMs > 0)
                {
                    // Cumulative-timestamp scheduling (mirrors InjectPenStroke): for frame index k (1..N),
                    // the target offset is targetMs_k = durationMs * k / N. Before frame k sleep only
                    // max(0, targetMs_k − elapsed) so total wall time ≈ durationMs regardless of step count.
                    // No Math.Max(1,…) floor → --duration-ms 1 stays ≈ 1 ms total. The final frame (k == N)
                    // has targetMs == durationMs and is followed by no trailing sleep.
                    var sleepFn = sleep ?? Thread.Sleep;
                    var sw = Stopwatch.StartNew();
                    var nowFn = nowMs ?? (() => sw.ElapsedMilliseconds);

                    ScheduleGlide(durationMs, GlideSteps, frameIndex =>
                    {
                        double t = frameIndex / (double)GlideSteps;
                        for (int i = 0; i < count; i++)
                        {
                            var path = contactPaths[i];
                            var (x, y) = Interpolate(path, t);
                            contacts[i] = MakeContact(
                                (uint)i, x, y,
                                POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                                primary: i == 0);
                        }
                        send(contacts);
                    }, sleepFn, nowFn);
                }
                else
                {
                    // durationMs <= 0: no timing — send all glide frames immediately.
                    for (int step = 1; step <= GlideSteps; step++)
                    {
                        double t = step / (double)GlideSteps;
                        for (int i = 0; i < count; i++)
                        {
                            var path = contactPaths[i];
                            var (x, y) = Interpolate(path, t);
                            contacts[i] = MakeContact(
                                (uint)i, x, y,
                                POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                                primary: i == 0);
                        }
                        send(contacts);
                    }
                }
            }

            // --- Lift on the normal path: let failure propagate so the caller knows the pointer
            //     may be stuck and the command exits non-zero with a structured error. ---
            for (int i = 0; i < count; i++)
            {
                var last = contactPaths[i][^1];
                contacts[i] = MakeContact((uint)i, last.X, last.Y, POINTER_FLAGS.POINTER_FLAG_UP, primary: i == 0);
            }
            send(contacts);
            released = true;
        }
        finally
        {
            // Best-effort lift when unwinding from an earlier exception (hold/glide frame failed).
            // Swallowing here avoids masking the original, more-informative exception.
            if (!released)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var last = contactPaths[i][^1];
                        contacts[i] = MakeContact((uint)i, last.X, last.Y, POINTER_FLAGS.POINTER_FLAG_UP, primary: i == 0);
                    }
                    send(contacts);
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static POINTER_TOUCH_INFO MakeContact(uint id, int x, int y, POINTER_FLAGS flags, bool primary)
    {
        if (primary)
        {
            flags |= POINTER_FLAGS.POINTER_FLAG_PRIMARY;
        }

        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                pointerId = id,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA,
            rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 },
        };
    }

    /// <summary>Submits one frame of touch contacts via the legacy <c>InjectTouchInput</c> API.</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the <c>InjectTouchInput</c> P/Invoke and its Win32 failure branch
    /// require an unlocked interactive desktop and live touch injection, so this native sender is
    /// un-coverable in the shared CI/test environment. The frames it would submit are validated via the
    /// injectable <see cref="TouchSender"/> seam (see PointerInputFrameTests).
    /// </remarks>
    private static void SendLegacyTouch(POINTER_TOUCH_INFO[] contacts)
    {
        unsafe
        {
            fixed (POINTER_TOUCH_INFO* p = contacts)
            {
                if (!PInvoke.InjectTouchInput((uint)contacts.Length, p))
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException(
                        $"InjectTouchInput failed (Win32 error {err}: {Win32Message(err)}) — touch injection " +
                        "failed or is unsupported on this desktop. The target may be elevated (run this CLI " +
                        "as administrator), the desktop may be locked, or injected touch may not be supported here.");
                }
            }
        }
    }

    /// <summary>
    /// Submits one frame of touch contacts via the synthetic-pointer device
    /// (<c>InjectSyntheticPointerInput</c>) — the modern path shared with pen injection.
    /// </summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the <c>InjectSyntheticPointerInput</c> P/Invoke and its Win32
    /// failure branch require a live synthetic-pointer device on an unlocked interactive desktop, so
    /// this native sender is un-coverable in the shared CI/test environment. The frames it would submit
    /// are validated via the injectable <see cref="TouchSender"/> seam (see PointerInputFrameTests).
    /// </remarks>
    private static void SendSyntheticTouch(HSYNTHETICPOINTERDEVICE device, POINTER_TOUCH_INFO[] contacts)
    {
        var infos = new POINTER_TYPE_INFO[contacts.Length];
        for (int i = 0; i < contacts.Length; i++)
        {
            infos[i] = new POINTER_TYPE_INFO { type = POINTER_INPUT_TYPE.PT_TOUCH };
            infos[i].Anonymous.touchInfo = contacts[i];
        }

        unsafe
        {
            fixed (POINTER_TYPE_INFO* p = infos)
            {
                if (!PInvoke.InjectSyntheticPointerInput(device, p, (uint)infos.Length))
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException(
                        $"InjectSyntheticPointerInput (touch) failed (Win32 error {err}: {Win32Message(err)}) — " +
                        "touch injection failed on this desktop. The target may be elevated (run this CLI as " +
                        "administrator), or the desktop may be locked.");
                }
            }
        }
    }

    /// <summary>
    /// Maps a normalized pen pressure (0..1, where 0 is barely touching and 1 is full force) onto the
    /// Win32 synthetic-pointer pressure range (1..<see cref="PenPressureMax"/>). The value is clamped
    /// into range and rounded to the nearest integer; because in-contact pen frames require a non-zero
    /// pressure, a mapped value of 0 is bumped to 1.
    /// </summary>
    /// <remarks>
    /// Pure arithmetic extracted from <see cref="Pen"/> so it is unit-testable without the native
    /// synthetic-pointer device (issue #630); see PointerInputPressureTests.
    /// </remarks>
    internal static uint MapPenPressure(float pressure)
    {
        uint mapped = (uint)Math.Clamp((int)Math.Round(pressure * PenPressureMax), 0, (int)PenPressureMax);
        return mapped == 0 ? 1u : mapped; // in-contact frames need non-zero pressure
    }

    /// <summary>Injects a synthetic pen/stylus stroke (tap or ink path) with pressure and tilt.</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the native device path
    /// (<c>CreateSyntheticPointerDevice(PT_PEN)</c> → inject → <c>DestroySyntheticPointerDevice</c>)
    /// requires Windows 10 1809+ and an unlocked interactive desktop and cannot run in the shared
    /// CI/test environment without live input injection. The pressure mapping is covered directly by
    /// <see cref="MapPenPressure"/> unit tests, and the frame-ordering logic through the injectable
    /// <see cref="PenFrameSender"/> seam on <see cref="InjectPenStroke"/> (see PointerInputFrameTests
    /// and PointerInputPressureTests); only the OS injection calls (device create / inject / destroy)
    /// remain un-coverable here.
    /// </remarks>
    public static void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs)
    {
        var device = PInvoke.CreateSyntheticPointerDevice(POINTER_INPUT_TYPE.PT_PEN, 1, POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE);
        if (device.IsNull)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice(PT_PEN) failed (Win32 error {err}: {Win32Message(err)}) — " +
                "synthetic pen injection is unavailable on this desktop (requires Windows 10 1809+ and an " +
                "unlocked interactive session).");
        }

        try
        {
            uint mappedPressure = MapPenPressure(pressure);

            InjectPenStroke(path, mappedPressure, durationMs,
                (x, y, p, flags) => SendPen(device, x, y, p, tiltX, tiltY, eraser, flags));
        }
        finally
        {
            PInvoke.DestroySyntheticPointerDevice(device);
        }
    }

    /// <summary>
    /// Delegate that submits one pen frame. <paramref name="pressure"/> is the raw 0..1024 pressure
    /// value (0 for UP frames); <paramref name="flags"/> carries DOWN/UPDATE/UP and contact flags.
    /// </summary>
    internal delegate void PenFrameSender(int x, int y, uint pressure, POINTER_FLAGS flags);

    /// <summary>
    /// Sends the full sequence of DOWN → interpolated UPDATE glide → UP frames for a pen stroke.
    /// Exposed as <see langword="internal"/> for frame-sequence unit tests (no live device needed).
    /// </summary>
    /// <param name="path">Ordered waypoints of the stroke, in screen coordinates. A single point is a tap.</param>
    /// <param name="contactPressure">Tip pressure in the native 0..1024 pen range.</param>
    /// <param name="durationMs">How long the glide between waypoints should take.</param>
    /// <param name="send">Submits one pen frame. Injected so tests can capture frames instead of driving a real device.</param>
    /// <param name="sleep">
    /// Optional sleep function; defaults to <see cref="Thread.Sleep(int)"/>. Inject a fake for tests
    /// so timing assertions complete instantly without real blocking.
    /// </param>
    /// <param name="nowMs">
    /// Optional monotonic clock returning elapsed milliseconds from the start of the timed glide.
    /// Defaults to a <see cref="Stopwatch"/> started when the timed loop begins. Inject a fake for
    /// tests that need to control the apparent clock (should advance by the same amounts as <paramref name="sleep"/>).
    /// </param>
    internal static void InjectPenStroke(
        IReadOnlyList<PointerPoint> path,
        uint contactPressure,
        int durationMs,
        PenFrameSender send,
        Action<int>? sleep = null,
        Func<long>? nowMs = null)
    {
        var first = path[0];

        // Hover-arrival: bring the pen IN RANGE (hovering, not yet in contact) BEFORE the DOWN
        // frame. Windows latches the pen's transducer type — tip vs. inverted/eraser end — at the
        // moment the pointer ENTERS RANGE. A frame that is already in contact (or a flag change made
        // mid-stroke) is ignored, so an eraser stroke that jumps straight to DOWN is delivered as an
        // ordinary tip contact and PEN_FLAG_INVERTED/ERASER never reaches the app. A physical pen
        // always hovers before it touches; emitting this in-range frame first is what lets --eraser
        // be honoured. The send closure stamps the orientation via ComputePenFlags: this hover frame
        // reports PEN_FLAG_INVERTED only (no PEN_FLAG_ERASER), because Windows drops any out-of-
        // contact frame that carries ERASER — which would defeat the latch we are trying to set. That
        // mirrors real hardware, where the flipped tip reports "inverted" on hover and only asserts
        // "eraser" on contact. pressure 0 = not in contact. This mirrors the working reference
        // injectors (draw-stream, EGoTouchRev, zpenflow), which all establish in-range state first.
        send(first.X, first.Y, 0,
            POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_UPDATE);

        // DOWN at the first point.
        send(first.X, first.Y, contactPressure,
            POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);

        bool released = false;
        try
        {
            int segments = path.Count - 1;
            if (segments > 0)
            {
                if (durationMs > 0)
                {
                    // Cumulative-timestamp scheduling via the shared ScheduleGlide helper.
                    // For frame index k (1..N), targetMs_k = durationMs * k / N; sleep only the drift-
                    // corrected delta before each frame. The final frame has targetMs == durationMs and
                    // is followed by no trailing sleep. See ScheduleGlide for the algorithm details.
                    var sleepFn = sleep ?? Thread.Sleep;
                    var sw = Stopwatch.StartNew();
                    var nowFn = nowMs ?? (() => sw.ElapsedMilliseconds);

                    int totalFrames = segments * GlideSteps;

                    ScheduleGlide(durationMs, totalFrames, frameIndex =>
                    {
                        int segIdx = (frameIndex - 1) / GlideSteps;
                        int step   = (frameIndex - 1) % GlideSteps + 1;
                        double t = step / (double)GlideSteps;
                        var from = path[segIdx];
                        var to   = path[segIdx + 1];
                        int x = from.X + (int)Math.Round((to.X - from.X) * t);
                        int y = from.Y + (int)Math.Round((to.Y - from.Y) * t);
                        send(x, y, contactPressure,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
                    }, sleepFn, nowFn);
                }
                else
                {
                    // durationMs <= 0: fall back to a fixed ~10 ms per waypoint (original cadence).
                    for (int i = 1; i < path.Count; i++)
                    {
                        var pt = path[i];
                        send(pt.X, pt.Y, contactPressure,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
                        Thread.Sleep(10);
                    }
                }
            }
            // --- Lift on the normal path: break contact but stay IN RANGE (a clean lift to hover).
            //     The synthetic pen device is destroyed immediately after the stroke, which retracts
            //     the in-range pointer, so this mirrors a real pen lifting off the surface. Let a
            //     failure propagate so the caller knows the pen may be stuck and the command exits
            //     non-zero with a structured error. ---
            var last = path[^1];
            send(last.X, last.Y, 0, POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_INRANGE);
            released = true;
        }
        finally
        {
            // Best-effort lift when unwinding from an earlier exception (glide frame failed).
            // Swallowing here avoids masking the original, more-informative exception.
            if (!released)
            {
                var last = path[^1];
                try { send(last.X, last.Y, 0, POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_INRANGE); }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>Submits one pen frame via the synthetic-pointer device (<c>InjectSyntheticPointerInput</c>).</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the <c>InjectSyntheticPointerInput</c> P/Invoke and its Win32
    /// failure branch require a live synthetic pen device on an unlocked interactive desktop, so this
    /// native sender is un-coverable in the shared CI/test environment. The <c>ComputePenFlags</c>
    /// bitmask logic it applies per frame is covered directly, and the frames it would submit are
    /// validated via the injectable <see cref="PenFrameSender"/> seam (see PointerInputFrameTests).
    /// </remarks>
    private static void SendPen(
        HSYNTHETICPOINTERDEVICE device, int x, int y, uint pressure, int tiltX, int tiltY, bool eraser, POINTER_FLAGS flags)
    {
        var penFlags = ComputePenFlags(eraser, flags);

        var info = new POINTER_TYPE_INFO
        {
            type = POINTER_INPUT_TYPE.PT_PEN,
        };
        info.Anonymous.penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_PEN,
                pointerId = 1,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            penFlags = penFlags,
            penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
            pressure = pressure,
            tiltX = tiltX,
            tiltY = tiltY,
        };

        unsafe
        {
            if (!PInvoke.InjectSyntheticPointerInput(device, &info, 1))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"InjectSyntheticPointerInput (pen) failed (Win32 error {err}: {Win32Message(err)}) — the " +
                    "target may be elevated (run this CLI as administrator) or the desktop is locked.");
            }
        }
    }

    internal static uint ComputePenFlags(bool eraser)
        => ComputePenFlags(eraser, POINTER_FLAGS.POINTER_FLAG_INCONTACT);

    /// <summary>
    /// Computes the <c>penFlags</c> bitmask for a single frame. The eraser transducer is only valid
    /// while the pen is in contact: Windows silently drops any out-of-contact frame (the hover-arrival
    /// and lift frames) that carries <see cref="PEN_FLAG_ERASER"/>. If the hover-arrival frame carried
    /// ERASER it would be dropped, the eraser transducer would never latch on range entry, and the
    /// stroke would be delivered as an ordinary tip — the exact bug that made <c>--eraser</c> a no-op.
    /// So on out-of-contact frames report <see cref="PEN_FLAG_INVERTED"/> only, and assert the full
    /// inverted+eraser flags only on in-contact frames. This mirrors real hardware, where the flipped
    /// tip reports "inverted" while hovering and only asserts "eraser" once it touches the surface.
    /// </summary>
    internal static uint ComputePenFlags(bool eraser, POINTER_FLAGS pointerFlags)
    {
        if (!eraser)
        {
            return PEN_FLAG_NONE;
        }

        bool inContact = (pointerFlags & POINTER_FLAGS.POINTER_FLAG_INCONTACT) != 0;

        // Microsoft pen flags distinguish inverted orientation from eraser input; on contact set both
        // so receivers that check either PointerPointProperties.IsInverted or IsEraser recognize it.
        return inContact ? (PEN_FLAG_INVERTED | PEN_FLAG_ERASER) : PEN_FLAG_INVERTED;
    }

    private static (int X, int Y) Interpolate(IReadOnlyList<PointerPoint> path, double t)
    {
        if (path.Count == 1)
        {
            return (path[0].X, path[0].Y);
        }

        // Treat the path as a single straight segment from first to last waypoint.
        var a = path[0];
        var b = path[^1];
        int x = a.X + (int)Math.Round((b.X - a.X) * t);
        int y = a.Y + (int)Math.Round((b.Y - a.Y) * t);
        return (x, y);
    }

    /// <summary>
    /// Drift-corrected sleep scheduling for a timed glide of <paramref name="totalFrames"/> evenly-spaced
    /// frames over <paramref name="durationMs"/> milliseconds. For frame index k (1..N) the target offset
    /// is <c>durationMs * k / N</c>; before frame k we sleep only <c>max(0, targetMs_k − elapsed)</c>
    /// (no <c>Math.Max(1,…)</c> floor, so sub-ms-per-frame durations yield zero sleep). The final frame
    /// (k == N) has targetMs == durationMs and is sent with no trailing sleep after it.
    /// </summary>
    private static void ScheduleGlide(
        int durationMs,
        int totalFrames,
        Action<int> sendFrame,
        Action<int> sleepFn,
        Func<long> nowFn)
    {
        for (int k = 1; k <= totalFrames; k++)
        {
            long targetMs = (long)durationMs * k / totalFrames;
            long elapsedMs = nowFn();
            int deltaMs = (int)Math.Max(0L, targetMs - elapsedMs);
            if (deltaMs > 0) { sleepFn(deltaMs); }
            sendFrame(k);
        }
    }
}
