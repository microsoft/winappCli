// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

using System.Diagnostics;

internal readonly record struct TopLevelWindowSnapshot(
    long WindowHandle,
    int ProcessId,
    bool IsVisible,
    WindowResponseProbeResult? Response = null,
    int? ThreadId = null);

internal interface ITopLevelWindowProbe
{
    IReadOnlyList<TopLevelWindowSnapshot> Snapshot(IReadOnlySet<int> processIds);
}

/// <summary>
/// Enumerates native top-level windows without entering UI Automation or reading window text.
/// Avoiding UIA keeps startup observation independent of provider responsiveness and avoids
/// collecting user-visible titles.
/// </summary>
internal sealed class TopLevelWindowProbe(IWindowResponseProbe responseProbe) : ITopLevelWindowProbe
{
    public IReadOnlyList<TopLevelWindowSnapshot> Snapshot(IReadOnlySet<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return [];
        }

        var windows = new List<TopLevelWindowSnapshot>();
        var window = global::Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            window = global::Windows.Win32.PInvoke.FindWindowEx(
                global::Windows.Win32.Foundation.HWND.Null,
                window,
                null,
                (string?)null);
            if (window.IsNull)
            {
                break;
            }

            uint processId = 0;
            uint threadId;
            unsafe
            {
                threadId = global::Windows.Win32.PInvoke.GetWindowThreadProcessId(
                    window,
                    &processId);
            }

            if (processId <= int.MaxValue && processIds.Contains((int)processId))
            {
                var isVisible = global::Windows.Win32.PInvoke.IsWindowVisible(window);
                windows.Add(new(
                    (long)(nint)window,
                    (int)processId,
                    isVisible,
                    ThreadId: threadId is > 0 and <= int.MaxValue
                        ? (int)threadId
                        : null));
            }
        }

        var probeStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < windows.Count; index++)
        {
            if (!windows[index].IsVisible)
            {
                continue;
            }
            if (Stopwatch.GetElapsedTime(probeStarted) >= responseProbe.Timeout)
            {
                break;
            }

            windows[index] = windows[index] with
            {
                Response = responseProbe.Probe(windows[index].WindowHandle),
            };
        }

        return windows;
    }
}
