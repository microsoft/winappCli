// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

// Out-of-process probes used by scripts/devtools-perf.ps1. They work the same whether or not
// DevTools is attached, so every app (including ones we cannot modify) gets comparable numbers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class DtpWindowHit
{
    public int Pid;
    public long Hwnd;
    public DateTime VisibleUtc;
    // Filled by the startup monitor that keeps pinging the new window while winapp is still attaching.
    public DateTime? SettledUtc;
    public double MaxPingMs;
    public int PingsOver50Ms;
}

public static class DtpNative
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const string WinUIWindowClass = "WinUIDesktopWin32WindowClass";

    // Polls every 5 ms for the first visible WinUI top-level window of a process with this image
    // name that started after `afterUtc`, then runs MonitorStartup on it. Start it before launching so the timestamp is not late.
    public static Task<DtpWindowHit> WatchForWindow(string processName, DateTime afterUtc, int timeoutMs, int monitorMs)
    {
        return Task.Run(() =>
        {
            var deadline = Stopwatch.StartNew();
            var eligible = new Dictionary<uint, bool>();
            while (deadline.ElapsedMilliseconds < timeoutMs)
            {
                DtpWindowHit hit = null;
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    var cls = new StringBuilder(64);
                    GetClassName(hwnd, cls, cls.Capacity);
                    if (cls.ToString() != WinUIWindowClass) return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (!eligible.TryGetValue(pid, out var ok))
                    {
                        try
                        {
                            using var p = Process.GetProcessById((int)pid);
                            ok = string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                                 p.StartTime.ToUniversalTime() >= afterUtc;
                        }
                        catch { ok = false; }
                        eligible[pid] = ok;
                    }
                    if (!ok || !GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
                    hit = new DtpWindowHit { Pid = (int)pid, Hwnd = hwnd.ToInt64(), VisibleUtc = DateTime.UtcNow };
                    return false;
                }, IntPtr.Zero);
                if (hit != null)
                {
                    MonitorStartup(hit, monitorMs);
                    return hit;
                }
                Thread.Sleep(5);
            }
            return null;
        });
    }

    // Pings the new window every 16 ms for at least monitorMs (and until it settles, up to 60 s):
    // records when the UI thread first stays responsive (30 pings under 16 ms in a row) and the
    // worst stall, independently of when winapp itself returns.
    private static void MonitorStartup(DtpWindowHit hit, int monitorMs)
    {
        var clock = Stopwatch.StartNew();
        var streak = 0;
        var streakStart = DateTime.UtcNow;
        while (clock.ElapsedMilliseconds < 60000 && (hit.SettledUtc == null || clock.ElapsedMilliseconds < monitorMs))
        {
            var now = DateTime.UtcNow;
            var v = Ping(hit.Hwnd, 2000);
            if (v < 0) v = 2000;
            if (v > hit.MaxPingMs) hit.MaxPingMs = v;
            if (v > 50) hit.PingsOver50Ms++;
            if (v < 16)
            {
                if (streak == 0) streakStart = now;
                if (++streak >= 30 && hit.SettledUtc == null) hit.SettledUtc = streakStart;
            }
            else streak = 0;
            Thread.Sleep(16);
        }
    }

    // Round trip of WM_NULL through the window's message queue: how long the UI thread takes to
    // get back to its message loop. -1 when it does not answer within the timeout.
    public static double Ping(long hwnd, uint timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var ok = SendMessageTimeout(new IntPtr(hwnd), 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, timeoutMs, out _);
        return ok == IntPtr.Zero ? -1 : clock.Elapsed.TotalMilliseconds;
    }

    // Pings every intervalMs for durationMs; returns each round trip (timeouts recorded as timeoutMs).
    public static double[] PingSeries(long hwnd, int durationMs, int intervalMs, uint timeoutMs)
    {
        var values = new List<double>();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < durationMs)
        {
            var v = Ping(hwnd, timeoutMs);
            values.Add(v < 0 ? timeoutMs : v);
            Thread.Sleep(intervalMs);
        }
        return values.ToArray();
    }

    // Background ping recorder for scenarios driven by another thread (e.g. a UIA navigation walk).
    public sealed class PingRecorder
    {
        private readonly List<double> _values = new List<double>();
        private volatile bool _running = true;
        private readonly Thread _thread;
        public PingRecorder(long hwnd, int intervalMs, uint timeoutMs)
        {
            _thread = new Thread(() =>
            {
                while (_running)
                {
                    var v = Ping(hwnd, timeoutMs);
                    lock (_values) _values.Add(v < 0 ? timeoutMs : v);
                    Thread.Sleep(intervalMs);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        public double[] Stop()
        {
            _running = false;
            _thread.Join();
            lock (_values) return _values.ToArray();
        }
    }

    public static void Place(long hwnd, int x, int y, int width, int height)
    {
        // SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(new IntPtr(hwnd), IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
    }

    public static int[] WindowRect(long hwnd)
    {
        GetWindowRect(new IntPtr(hwnd), out var r);
        return new[] { r.Left, r.Top, r.Right, r.Bottom };
    }
}
