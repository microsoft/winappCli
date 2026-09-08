// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// The app window that automation calls act on. Obtain one from
/// <see cref="IUiTargetResolver.ResolveAsync"/>, or from a window handle you already have
/// via <see cref="FromWindowHandle"/>.
/// </summary>
public sealed class UiTarget
{
    /// <summary>Process that owns the target window.</summary>
    public int ProcessId { get; set; }

    /// <summary>Process name, used in diagnostics and error messages.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Current window title, when known.</summary>
    public string? WindowTitle { get; set; }

    /// <summary>Specific window handle when the process has multiple windows.</summary>
    public long WindowHandle { get; set; }

    /// <summary>
    /// True when this window was targeted explicitly rather than discovered. When set,
    /// inspect/search/find operations stay on this window instead of expanding to other top-level
    /// windows owned by the same process.
    /// </summary>
    public bool IsExplicitWindow { get; set; }

    /// <summary>
    /// Builds a target from a native window handle. This is the bridge from any framework that
    /// already gives you an HWND — for example an MSTest <c>WindowTest</c>, whose
    /// <c>MainWindow.Current.NativeWindowHandle</c> can be passed straight in.
    /// </summary>
    /// <remarks>
    /// The window is treated as explicitly targeted, so element lookups stay scoped to it. The
    /// owning process is resolved from the handle; if that fails, <see cref="ProcessId"/> is 0 and
    /// <see cref="ProcessName"/> is <c>"Unknown"</c>, which is enough for handle-based operations.
    /// </remarks>
    /// <param name="hwnd">Native window handle. Must not be 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hwnd"/> is 0.</exception>
    public static UiTarget FromWindowHandle(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hwnd), "A window handle of 0 does not identify a window.");
        }

        var target = new UiTarget
        {
            WindowHandle = hwnd,
            IsExplicitWindow = true,
            ProcessName = "Unknown",
        };

        var query = new SystemUiQuery();

        var processId = query.GetProcessIdForWindow(hwnd);
        if (processId != 0)
        {
            target.ProcessId = (int)processId;
            if (query.GetProcessById((int)processId) is { } process && !string.IsNullOrEmpty(process.ProcessName))
            {
                target.ProcessName = process.ProcessName;
            }
        }

        var title = query.GetWindowText(hwnd);
        if (!string.IsNullOrEmpty(title))
        {
            target.WindowTitle = title;
        }

        return target;
    }
}
