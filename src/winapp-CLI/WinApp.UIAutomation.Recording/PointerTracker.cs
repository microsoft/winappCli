// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>The pointer at one instant, in physical screen pixels.</summary>
/// <param name="X">Horizontal position of the pointer's tip.</param>
/// <param name="Y">Vertical position of the pointer's tip.</param>
/// <param name="Visible">False when Windows is hiding the pointer.</param>
/// <param name="Cursor">The cursor image handle, or 0 when there is none to draw.</param>
internal readonly record struct PointerState(int X, int Y, bool Visible, nint Cursor);

/// <summary>A mouse-button press or release, in physical screen pixels.</summary>
/// <param name="Timestamp"><see cref="Stopwatch.GetTimestamp"/> when the event arrived.</param>
/// <param name="Button">"left", "right", "middle", "x1", or "x2".</param>
/// <param name="Down">True for a press, false for a release.</param>
/// <param name="X">Horizontal position of the event.</param>
/// <param name="Y">Vertical position of the event.</param>
internal readonly record struct PointerButtonEvent(long Timestamp, string Button, bool Down, int X, int Y);

/// <summary>Reads the pointer and, optionally, watches mouse-button events for a recording.</summary>
internal interface IPointerTracker : IDisposable
{
    /// <summary>True when button events are being collected.</summary>
    bool TracksClicks { get; }

    PointerState GetState();

    /// <summary>Returns and clears the button events collected since the previous call, oldest first.</summary>
    IReadOnlyList<PointerButtonEvent> DrainButtonEvents();
}

/// <summary>
/// Reads the live pointer through <c>GetCursorInfo</c> and collects button events through a
/// low-level mouse hook on a dedicated thread.
/// </summary>
/// <remarks>
/// A low-level hook sees every mouse event on the desktop, including clicks injected by
/// <c>winapp ui click</c>, and Windows waits for it before delivering each event. The callback only
/// enqueues a struct, so it adds no noticeable input latency. Sampling the button state once per
/// frame instead would miss most clicks: an injected click is shorter than one frame.
/// </remarks>
internal sealed unsafe class MousePointerTracker : IPointerTracker
{
    // Windows calls a low-level hook on the thread that installed it, so each hook delivers
    // events only to the tracker that owns that thread.
    [ThreadStatic]
    private static MousePointerTracker? t_current;

    private readonly ConcurrentQueue<PointerButtonEvent> _events = new();
    private readonly ILogger _logger;
    private readonly Thread? _hookThread;
    private uint _hookThreadId;
    private bool _hookInstalled;
    private bool _disposed;

    /// <remarks>
    /// Coverage ceiling (issue #630): installing a low-level mouse hook needs an interactive desktop
    /// and affects every process on it. Tests use the <see cref="IPointerTracker"/> seam.
    /// </remarks>
    public MousePointerTracker(bool trackClicks, ILogger logger)
    {
        _logger = logger;
        if (!trackClicks)
        {
            return;
        }

        // Not disposable: a hook thread that starts after the timeout still signals it safely.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hookThread = new Thread(() => RunHookThread(ready))
        {
            IsBackground = true,
            Name = "winapp recording mouse hook",
        };
        _hookThread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogDebug("The mouse hook thread did not start in time; clicks will not be tracked.");
        }
    }

    public bool TracksClicks => Volatile.Read(ref _hookInstalled);

    public PointerState GetState()
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(ref info))
        {
            return default;
        }

        var visible = (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) != 0;
        return new PointerState(info.ptScreenPos.X, info.ptScreenPos.Y, visible, (nint)info.hCursor.Value);
    }

    public IReadOnlyList<PointerButtonEvent> DrainButtonEvents()
    {
        if (_events.IsEmpty)
        {
            return [];
        }

        var drained = new List<PointerButtonEvent>();
        while (_events.TryDequeue(out var item))
        {
            drained.Add(item);
        }
        return drained;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Volatile.Write(ref _disposed, true);

        if (_hookThread is { IsAlive: true })
        {
            var threadId = Volatile.Read(ref _hookThreadId);
            if (threadId != 0)
            {
                PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);
            }
            if (!_hookThread.Join(TimeSpan.FromSeconds(2)))
            {
                _logger.LogDebug("The mouse hook thread did not stop in time.");
            }
        }
    }

    private void RunHookThread(TaskCompletionSource ready)
    {
        UnhookWindowsHookExSafeHandle? hook = null;
        try
        {
            Volatile.Write(ref _hookThreadId, PInvoke.GetCurrentThreadId());
            t_current = this;
            hook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_MOUSE_LL,
                &HookProc,
                PInvoke.GetModuleHandle((string?)null),
                0);
            if (hook.IsInvalid)
            {
                _logger.LogDebug("SetWindowsHookEx(WH_MOUSE_LL) failed with error {Error}.", Marshal.GetLastPInvokeError());
                return;
            }
            Volatile.Write(ref _hookInstalled, true);

            // Dispose may have run before this thread could receive WM_QUIT.
            if (Volatile.Read(ref _disposed))
            {
                Volatile.Write(ref _hookInstalled, false);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not install the mouse hook; clicks will not be tracked.");
            return;
        }
        finally
        {
            ready.TrySetResult();
            if (!Volatile.Read(ref _hookInstalled))
            {
                hook?.Dispose();
                t_current = null;
            }
        }

        try
        {
            while (PInvoke.GetMessage(out _, HWND.Null, 0, 0) > 0)
            {
                // The hook is called on this thread, so it must keep pumping messages until disposal.
            }
        }
        finally
        {
            hook.Dispose();
            t_current = null;
            Volatile.Write(ref _hookInstalled, false);
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): Windows calls this only for real mouse events on the input
    /// desktop. The button mapping it delegates to is unit tested.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0 && lParam.Value != 0)
        {
            var data = (MSLLHOOKSTRUCT*)lParam.Value;
            if (t_current is { } tracker
                && ClassifyButtonMessage((uint)wParam.Value, data->mouseData) is { } button)
            {
                tracker._events.Enqueue(new PointerButtonEvent(
                    Stopwatch.GetTimestamp(), button.Button, button.Down, data->pt.X, data->pt.Y));
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    /// <summary>Maps a low-level mouse message to a button name and direction.</summary>
    /// <returns>Null for messages that are not button presses or releases.</returns>
    internal static (string Button, bool Down)? ClassifyButtonMessage(uint message, uint mouseData)
    {
        var xButton = (mouseData >> 16) == PInvoke.XBUTTON1 ? "x1" : "x2";
        return message switch
        {
            PInvoke.WM_LBUTTONDOWN => ("left", true),
            PInvoke.WM_LBUTTONUP => ("left", false),
            PInvoke.WM_RBUTTONDOWN => ("right", true),
            PInvoke.WM_RBUTTONUP => ("right", false),
            PInvoke.WM_MBUTTONDOWN => ("middle", true),
            PInvoke.WM_MBUTTONUP => ("middle", false),
            PInvoke.WM_XBUTTONDOWN => (xButton, true),
            PInvoke.WM_XBUTTONUP => (xButton, false),
            _ => null,
        };
    }
}
