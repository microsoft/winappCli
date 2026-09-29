// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Resolves app, process, title, PID, or window-handle input into the UI Automation target that
/// later operations act on.
/// </summary>
/// <param name="uiAutomation">Window discovery service used for title and process window searches.</param>
/// <param name="systemQuery">System window and process query service used to inspect candidate windows.</param>
/// <param name="logger">Logger used to report automatic target selection decisions.</param>
public sealed class UiTargetResolver(
    IUiAutomation uiAutomation,
    ISystemUiQuery systemQuery,
    ILogger<UiTargetResolver> logger) : IUiTargetResolver
{
    // The window-metadata reads live on ISystemUiQuery so the resolver's selection logic is unit-
    // testable. GetWindowInfo/GetWindowClassName must stay static (they're called statically by
    // UiListWindowsCommand, UiScreenshotCommand, and UiAutomationService), so those shims route
    // through this shared real implementation while instance callers use the injected seam. Both
    // point at the same OS boundary; tests inject a fake for the instance path.
    private static readonly SystemUiQuery s_sharedQuery = new();

    /// <summary>
    /// Resolves the requested app or window handle into the target window for automation calls.
    /// </summary>
    /// <param name="app">Process name, window title, or PID. Required unless <paramref name="hwnd"/> is set.</param>
    /// <param name="hwnd">Specific window handle. When supplied, it takes precedence over <paramref name="app"/>.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The process and, when known, window that matched the request.</returns>
    /// <exception cref="AppNotFoundException">No running process or window matched the request.</exception>
    /// <exception cref="InvalidOperationException">Neither target argument was supplied, or multiple processes matched ambiguously.</exception>
    public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
        => ResolveCore(app, hwnd, processOnly: false);

    /// <inheritdoc/>
    public Task<UiTarget> ResolveProcessAsync(string app, CancellationToken ct)
        => ResolveCore(app, null, processOnly: true);

    private Task<UiTarget> ResolveCore(string? app, long? hwnd, bool processOnly)
    {
        // Direct HWND targeting — most stable, used after discovery
        if (hwnd is not null and > 0)
        {
            return Task.FromResult(ResolveByHwnd(hwnd.Value));
        }

        if (string.IsNullOrWhiteSpace(app))
        {
            throw new InvalidOperationException(processOnly
                ? "Specify --app (process name, title, or PID)."
                : "Specify --app (process name, title, or PID) or --window (HWND).");
        }

        // Try PID or process name first
        var process = TryResolveProcess(app);

        if (process is null)
        {
            // Not a PID or process name — search all windows by title
            var windows = uiAutomation.FindWindowsByTitle(app);
            if (windows.Count == 0)
            {
                throw new AppNotFoundException($"No running app found matching '{app}'.");
            }
            if (processOnly)
            {
                var processes = windows.Select(window => window.Pid).Distinct().ToArray();
                if (processes.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Multiple processes have windows matching '{app}' (PIDs {string.Join(", ", processes)}). Use --app with a PID.");
                }
                return Task.FromResult(CreateTarget(processes[0], 0, null));
            }
            return Task.FromResult(SelectTitleMatch(windows, app));
        }

        var resolved = process.Value;
        if (processOnly)
        {
            return Task.FromResult(new UiTarget { ProcessId = resolved.Id, ProcessName = resolved.ProcessName });
        }

        // Process found — check for multiple windows
        var processWindows = uiAutomation.FindWindowsByPid(resolved.Id);
        if (processWindows.Count > 1)
        {
            return Task.FromResult(AutoSelectWindow(processWindows, app));
        }

        if (processWindows.Count == 1)
        {
            return Task.FromResult(CreateTarget(resolved.Id, processWindows[0].Hwnd, processWindows[0].Title));
        }

        // The process owns no top-level window. Packaged (UWP) apps such as Calculator draw inside
        // a frame window that belongs to ApplicationFrameHost, so look for such a frame by title.
        // Only frames qualify: any other title match ("myapp - Visual Studio Code") is a different app.
        var frames = resolved.MainWindowHandle != 0 || int.TryParse(app, out _)
            ? []
            : FramesOnly(uiAutomation.FindWindowsByTitle(app));
        if (frames.Count > 0)
        {
            logger.LogInformation(
                "'{ProcessName}' (PID {Pid}) has no top-level window of its own; using the app frame titled like '{App}' instead.",
                resolved.ProcessName, resolved.Id, app);
            return Task.FromResult(SelectTitleMatch(frames, app));
        }

        // No window yet (for example, the app is still starting). Keep the process-scoped target so
        // polling commands such as wait-for can find the window once it appears.
        return Task.FromResult(new UiTarget
        {
            ProcessId = resolved.Id,
            ProcessName = resolved.ProcessName,
            WindowTitle = GetMainWindowTitle(resolved)
        });
    }

    private List<(nint Hwnd, int Pid, string Title)> FramesOnly(List<(nint Hwnd, int Pid, string Title)> windows) =>
        windows
            .Where(w => string.Equals(systemQuery.GetWindowClassName((long)w.Hwnd), ApplicationFrameWindowClass, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Picks a window from title matches. ApplicationFrameHost frames win over other matches (a
    /// browser tab or editor titled "calculator" should not beat the Calculator app), and a target
    /// resolved to a frame is scoped to that window, because the host process owns the frames of
    /// every other running packaged app too.
    /// </summary>
    private UiTarget SelectTitleMatch(List<(nint Hwnd, int Pid, string Title)> windows, string app)
    {
        var frames = FramesOnly(windows);
        var candidates = frames.Count > 0 ? frames : windows;

        var target = candidates.Count > 1
            ? AutoSelectWindow(candidates, app)
            : CreateTarget(candidates[0].Pid, candidates[0].Hwnd, candidates[0].Title);

        if (frames.Count > 0)
        {
            target.IsExplicitWindow = true;
        }

        return target;
    }

    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";

    /// <summary>
    /// Auto-selects the best window from multiple candidates silently.
    /// Heuristic: prefer foreground window → prefer largest window.
    /// </summary>
    private UiTarget AutoSelectWindow(List<(nint Hwnd, int Pid, string Title)> windows, string app)
    {
        var foregroundHwnd = systemQuery.GetForegroundWindow();
        var foreground = windows.FirstOrDefault(w => w.Hwnd == foregroundHwnd);
        var selected = foreground != default ? foreground : PickLargestWindow(windows);

        var reason = foreground != default ? "foreground" : "largest";
        logger.LogInformation("Auto-selected HWND {Hwnd} ({Reason}) from {Count} windows for '{App}' — pass -w {ExplicitHwnd} to target this window explicitly.", selected.Hwnd, reason, windows.Count, app, selected.Hwnd);

        return CreateTarget(selected.Pid, selected.Hwnd, selected.Title);
    }

    /// <summary>Pick the window with the largest area (queried through the OS-boundary seam).</summary>
    private (nint Hwnd, int Pid, string Title) PickLargestWindow(List<(nint Hwnd, int Pid, string Title)> windows)
    {
        var best = windows[0];
        long bestArea = 0;

        foreach (var w in windows)
        {
            var (width, height) = systemQuery.GetWindowSize((long)w.Hwnd);
            var area = (long)width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = w;
            }
        }

        return best;
    }

    /// <summary>Get metadata for a window: class name, label, size, owner.</summary>
    public static WindowMetadata GetWindowInfo(nint hwnd)
    {
        var className = GetWindowClassName(hwnd);
        var label = ClassifyWindow(className);
        var (width, height) = s_sharedQuery.GetWindowSize((long)hwnd);
        var ownerHwnd = s_sharedQuery.GetWindowOwner((long)hwnd);

        return new WindowMetadata
        {
            ClassName = className ?? "Unknown",
            Label = label,
            Width = width,
            Height = height,
            OwnerHwnd = ownerHwnd
        };
    }

    /// <summary>Returns the Win32 class name for <paramref name="hwnd"/>, or <see langword="null"/> when it cannot be read.</summary>
    /// <param name="hwnd">Native window handle to inspect.</param>
    public static string? GetWindowClassName(nint hwnd) => s_sharedQuery.GetWindowClassName((long)hwnd);

    /// <summary>
    /// Classifies a Win32 class name as a user-facing window kind for diagnostics.
    /// </summary>
    /// <param name="className">Win32 class name, or <see langword="null"/> when unknown.</param>
    /// <returns><c>"popup"</c> for popup classes, <c>"dialog"</c> for <c>#32770</c>, otherwise <c>"window"</c>.</returns>
    public static string ClassifyWindow(string? className)
    {
        if (className is null) { return "window"; }
        if (className.Contains("Popup", StringComparison.OrdinalIgnoreCase)) { return "popup"; }
        if (className == "#32770") { return "dialog"; }
        return "window";
    }

    /// <summary>Metadata reported for a native window.</summary>
    public record WindowMetadata
    {
        /// <summary>Win32 class name, or <c>"Unknown"</c> when it could not be read.</summary>
        public string ClassName { get; init; } = "Unknown";

        /// <summary>User-facing window kind, such as <c>"window"</c>, <c>"popup"</c>, or <c>"dialog"</c>.</summary>
        public string Label { get; init; } = "window";

        /// <summary>Window width in screen pixels.</summary>
        public int Width { get; init; }

        /// <summary>Window height in screen pixels.</summary>
        public int Height { get; init; }

        /// <summary>Owning window handle, or 0 when the window has no owner.</summary>
        public nint OwnerHwnd { get; init; }
    }

    private UiTarget ResolveByHwnd(long hwnd)
    {
        var pid = systemQuery.GetProcessIdForWindow(hwnd);
        if (pid == 0)
        {
            throw new AppNotFoundException($"Window HWND {hwnd} not found or not accessible.");
        }

        var uiTarget = CreateTarget((int)pid, (nint)hwnd, null);
        uiTarget.IsExplicitWindow = true;
        RefreshWindowTitle(uiTarget);
        return uiTarget;
    }

    private UiTarget CreateTarget(int pid, nint hwnd, string? title)
    {
        var processName = systemQuery.GetProcessById(pid)?.ProcessName;
        if (string.IsNullOrEmpty(processName)) { processName = "Unknown"; }

        return new UiTarget
        {
            ProcessId = pid,
            ProcessName = processName,
            WindowTitle = title,
            WindowHandle = hwnd
        };
    }

    /// <summary>
    /// Refresh the window title from the session's explicit HWND. Only ever called after
    /// <see cref="ResolveByHwnd"/>, which always sets a non-zero <see cref="UiTarget.WindowHandle"/>.
    /// </summary>
    private void RefreshWindowTitle(UiTarget uiTarget)
    {
        var title = systemQuery.GetWindowText(uiTarget.WindowHandle);
        if (!string.IsNullOrEmpty(title))
        {
            uiTarget.WindowTitle = title;
        }
    }

    private UiProcessInfo? TryResolveProcess(string app)
    {
        // Try as PID
        if (int.TryParse(app, out var pid))
        {
            var byPid = systemQuery.GetProcessById(pid);
            if (byPid is null)
            {
                throw new AppNotFoundException($"No process found with PID {pid}.");
            }
            return byPid;
        }

        // Try exact process name
        var exact = SelectProcess(systemQuery.GetProcessesByName(app), app, partial: false);
        if (exact is not null)
        {
            return exact;
        }

        // Try partial process name match (e.g., "imageresizer" matches "PowerToys.ImageResizer")
        return SelectProcess(systemQuery.GetProcessesMatching(app), app, partial: true);
    }

    /// <summary>
    /// Choose a single process from the candidate snapshots. Returns the match (logging a note for
    /// partial matches), <c>null</c> when there is no usable match, or throws with a disambiguation
    /// listing when multiple windowed processes qualify.
    /// </summary>
    private UiProcessInfo? SelectProcess(IReadOnlyList<UiProcessInfo> candidates, string app, bool partial)
    {
        if (candidates.Count == 1)
        {
            var only = candidates[0];
            if (partial) { LogPartialMatch(app, only); }
            return only;
        }

        if (candidates.Count > 1)
        {
            var withWindow = candidates
                .Where(p => p.MainWindowHandle != 0 && !string.IsNullOrEmpty(p.MainWindowTitle))
                .ToList();

            if (withWindow.Count == 1)
            {
                var single = withWindow[0];
                if (partial) { LogPartialMatch(app, single); }
                return single;
            }

            if (withWindow.Count > 1)
            {
                var listing = string.Join("\n  ",
                    withWindow.Select(p => partial
                        ? $"PID {p.Id} ({p.ProcessName}): \"{p.MainWindowTitle}\""
                        : $"PID {p.Id}: \"{p.MainWindowTitle}\""));
                var header = partial
                    ? $"Multiple processes matching '{app}' found:"
                    : $"Multiple '{app}' windows found:";
                throw new InvalidOperationException(
                    $"{header}\n  {listing}\n" +
                    "Use --app with a PID or a more specific window title.");
            }
        }

        return null;
    }

    private void LogPartialMatch(string input, UiProcessInfo matched)
    {
        // Surface partial-name matches so users notice when a short/typoed --app value
        // resolved to an unrelated process (issue #467).
        logger.LogInformation(
            "Partial process-name match: '{Input}' resolved to '{ProcessName}' (PID {Pid}). Pass the full process name or PID to disambiguate.",
            input, matched.ProcessName, matched.Id);
    }

    private static string? GetMainWindowTitle(UiProcessInfo process)
        => string.IsNullOrEmpty(process.MainWindowTitle) ? null : process.MainWindowTitle;
}
