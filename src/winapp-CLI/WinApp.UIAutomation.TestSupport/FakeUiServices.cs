// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

/// <summary>
/// Fake UIA service for testing — returns configurable element data without touching real UIA.
/// </summary>
public class FakeUiAutomationService : IUiAutomation
{
    public UiElement[] InspectResult { get; set; } = [];
    public UiElement[] SearchResult { get; set; } = [];
    public UiElement? FindSingleResult { get; set; }

    /// <summary>
    /// Optional per-call results for <see cref="FindSingleElementAsync"/>. When non-empty, the first
    /// resolution of a *new* selector dequeues the next entry (so a single command resolving two
    /// distinct selectors — e.g. a selector→selector drag — returns two distinct elements). Re-reads of
    /// the same selector (the N5 stable-resolve) return the memoized element instead of draining the
    /// queue. Falls back to <see cref="FindSingleResult"/> when empty.
    /// </summary>
    public Queue<UiElement?> FindSingleResults { get; } = new();

    /// <summary>
    /// Per-selector movement sequences for N5 stability tests. Each read of the keyed selector advances
    /// the sequence (simulating an element whose bounds change between reads); once drained, the last
    /// value sticks. Key by the selector's slug or query text. Enqueue the command's initial-resolve
    /// element first, then one element per stability re-read.
    /// </summary>
    public Dictionary<string, Queue<UiElement?>> MovingResults { get; } = new();

    private readonly Dictionary<string, UiElement?> _resolvedBySelector = new();
    private readonly Dictionary<string, UiElement?> _lastMoving = new();
    public Dictionary<string, object?> PropertiesResult { get; set; } = [];
    public string InvokeResult { get; set; } = "InvokePattern";
    public (byte[] Pixels, int Width, int Height) ScreenshotResult { get; set; } = (new byte[4], 1, 1);
    public List<(nint Hwnd, int Pid, string Title)> WindowsByTitleResult { get; set; } = [];
    public List<(nint Hwnd, int Pid, string Title)> WindowsByPidResult { get; set; } = [];

    /// <summary>Text returned by <see cref="GetTextAsync"/>. Set to <see langword="null"/> to exercise
    /// the "no value" branches of get-value / wait-for. Defaults to a non-null sample.</summary>
    public string? GetTextResult { get; set; } = "fake text content";

    /// <summary>Per-call text sequence for <see cref="GetTextAsync"/>: each read dequeues the next entry,
    /// so a wait-for --value poll can see the value change across polls (e.g. "old" then "target"). Once
    /// drained, falls back to <see cref="GetTextResult"/>. Empty by default = use the single result.</summary>
    public Queue<string?> GetTextResults { get; } = new();

    // ---- Additive throw knobs (default null = no-op, so existing tests are unaffected) --------------
    // Each method throws the configured exception at its start, letting a test drive a command's
    // COMException (stale-element) or generic error branches without touching real UIA.
    public Exception? FindSingleThrow { get; set; }

    /// <summary>When &gt; 0, the next N <see cref="FindSingleElementAsync"/> calls throw a transient error
    /// (then decrement), simulating an element that isn't ready on the first poll(s). Drives wait-for's
    /// per-poll catch → keep-polling continuation deterministically. Default 0 = no transient failures.</summary>
    public int FindSingleThrowCount { get; set; }
    public Exception? InspectThrow { get; set; }
    public Exception? SearchThrow { get; set; }
    public Exception? PropertiesThrow { get; set; }
    public Exception? ScreenshotThrow { get; set; }
    public Exception? InvokeThrow { get; set; }
    public Exception? FocusThrow { get; set; }
    public Exception? GetFocusedThrow { get; set; }
    public Exception? GetTextThrow { get; set; }
    public Exception? ScrollContainerThrow { get; set; }
    public Exception? ScrollIntoViewThrow { get; set; }
    public Exception? SetValueThrow { get; set; }
    public Exception? FindWindowsThrow { get; set; }

    /// <summary>When set, the primary <see cref="InvokeAsync"/> throws this for an element that carries an
    /// <see cref="UiElement.InvokableAncestor"/> (simulating "element not invokable"), while the follow-up
    /// call on the ancestor itself (no InvokableAncestor) succeeds — driving invoke's ancestor-fallback.</summary>
    public bool InvokeThrowsForAncestorFallback { get; set; }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery)
    {
        if (FindWindowsThrow is not null) { throw FindWindowsThrow; }
        return WindowsByTitleResult;
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid)
    {
        if (FindWindowsThrow is not null) { throw FindWindowsThrow; }
        return WindowsByPidResult;
    }

    /// <summary>When non-null, <see cref="FindSingleElementAsync"/> throws this exception instead of
    /// returning a result. Use to simulate selector-ambiguity or other UIA failures.</summary>
    public Exception? FindSingleElementThrowException { get; set; }

    /// <summary>The rectangle returned for any nonzero handle (default: 0,0 – 1920,1080).</summary>
    public PointerRect WindowRect { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>When <see langword="false"/>, reports the window rect as unreadable (returns false).</summary>
    public bool WindowRectAllow { get; set; } = true;

    /// <summary>Records each <see cref="TryGetWindowRect"/> call so tests can distinguish the
    /// hwnd-0 rejection (no lookup) from the out-of-bounds rejection (lookup happened).</summary>
    public List<long> WindowRectCalls { get; } = [];

    public bool TryGetWindowRect(long hwnd, out PointerRect rect)
    {
        WindowRectCalls.Add(hwnd);
        if (!WindowRectAllow || hwnd == 0)
        {
            rect = default;
            return false;
        }

        rect = WindowRect;
        return true;
    }

    public Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct)
    {
        if (InspectThrow is not null) { throw InspectThrow; }
        return Task.FromResult(InspectResult);
    }

    public Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct)
    {
        if (InspectThrow is not null) { throw InspectThrow; }
        return Task.FromResult(InspectResult);
    }

    public Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct)
    {
        if (SearchThrow is not null) { throw SearchThrow; }
        return Task.FromResult(SearchResult.Take(maxResults).ToArray());
    }

    public Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct)
    {
        if (FindSingleElementThrowException is not null) { throw FindSingleElementThrowException; }
        if (FindSingleThrow is not null) { throw FindSingleThrow; }
        if (FindSingleThrowCount > 0)
        {
            FindSingleThrowCount--;
            throw new InvalidOperationException("Transient element lookup failure (test).");
        }
        var key = selector.Slug ?? selector.Query ?? string.Empty;

        // Per-selector movement sequence (N5 stability tests): advance each read, last value sticks.
        if (MovingResults.TryGetValue(key, out var seq))
        {
            var moving = seq.Count > 0 ? seq.Dequeue() : _lastMoving.GetValueOrDefault(key);
            _lastMoving[key] = moving;
            return Task.FromResult(moving);
        }

        // Re-read of an already-resolved selector → return the memoized element so the N5 re-resolve
        // doesn't drain the cross-selector queue.
        if (_resolvedBySelector.TryGetValue(key, out var memo))
        {
            return Task.FromResult(memo);
        }

        // First resolution of a new selector: dequeue the next distinct cross-selector result.
        if (FindSingleResults.Count > 0)
        {
            var dequeued = FindSingleResults.Dequeue();
            _resolvedBySelector[key] = dequeued;
            return Task.FromResult(dequeued);
        }

        return Task.FromResult(FindSingleResult);
    }

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct)
    {
        if (PropertiesThrow is not null) { throw PropertiesThrow; }
        return Task.FromResult(PropertiesResult);
    }

    public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget uiTarget, string? elementId, bool captureScreen, bool focus, CancellationToken ct)
    {
        if (ScreenshotThrow is not null) { throw ScreenshotThrow; }
        return Task.FromResult(ScreenshotResult);
    }

    public nint RootWindowHandle { get; set; }

    public string? RootWindowTitle { get; set; }

    public bool RootWindowResolves { get; set; } = true;

    public bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title)
    {
        hwnd = RootWindowHandle != 0 ? RootWindowHandle : (nint)target.WindowHandle;
        title = RootWindowTitle;
        return RootWindowResolves;
    }

    public nint ElementTopLevelWindow { get; set; }

    public nint ResolveElementTopLevelWindow(UiTarget target, UiElement element) => ElementTopLevelWindow;

    public PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback) => fallback;

    public Task<string> InvokeAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        if (InvokeThrow is not null) { throw InvokeThrow; }
        if (InvokeThrowsForAncestorFallback && element.InvokableAncestor is not null)
        {
            throw new InvalidOperationException("Element does not support an actionable pattern (test).");
        }
        return Task.FromResult(InvokeResult);
    }

    public Task SetValueAsync(UiTarget uiTarget, UiElement element, string text, CancellationToken ct)
    {
        if (SetValueThrow is not null) { throw SetValueThrow; }
        return Task.CompletedTask;
    }

    public Task FocusAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        if (FocusThrow is not null) { throw FocusThrow; }
        return Task.CompletedTask;
    }

    public Task ScrollIntoViewAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        if (ScrollIntoViewThrow is not null) { throw ScrollIntoViewThrow; }
        return Task.CompletedTask;
    }

    public Task ScrollContainerAsync(UiTarget uiTarget, UiElement element, string? direction, string? destination, CancellationToken ct)
    {
        if (ScrollContainerThrow is not null) { throw ScrollContainerThrow; }
        return Task.CompletedTask;
    }

    public UiElement? FocusedResult { get; set; } = new UiElement { Id = "e0", Type = "Edit", Name = "FocusedElement" };

    public Task<UiElement?> GetFocusedElementAsync(UiTarget uiTarget, CancellationToken ct)
    {
        if (GetFocusedThrow is not null) { throw GetFocusedThrow; }
        return Task.FromResult(FocusedResult);
    }

    public Task<string?> GetTextAsync(UiTarget uiTarget, UiElement element, CancellationToken ct)
    {
        if (GetTextThrow is not null) { throw GetTextThrow; }
        if (GetTextResults.Count > 0) { return Task.FromResult(GetTextResults.Dequeue()); }
        return Task.FromResult(GetTextResult);
    }
}

/// <summary>
/// Fake session service for testing — returns a configurable session without process resolution.
/// </summary>
public class FakeUiTargetResolver : IUiTargetResolver
{
    public UiTarget TargetResult { get; set; } = new()
    {
        ProcessId = 1234,
        ProcessName = "TestApp",
        WindowTitle = "Test Window"
    };

    /// <summary>When non-null, <see cref="ResolveAsync"/> throws this exception instead
    /// of returning <see cref="TargetResult"/>. Use to test command-level exception handling.</summary>
    public Exception? ThrowException { get; set; }

    /// <summary>When set, <see cref="ResolveAsync"/> throws this — drives a command's generic
    /// (or COMException) catch from inside its <c>try</c>, before any element work. Default null = no-op.</summary>
    public Exception? ResolveThrow { get; set; }

    public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
    {
        if (ThrowException is not null) { throw ThrowException; }
        if (ResolveThrow is not null) { throw ResolveThrow; }
        return Task.FromResult(TargetResult);
    }
}

/// <summary>
/// Fake mouse input for testing — records calls instead of issuing real SendInput.
/// </summary>
public class FakeMouseInput : IMouseInput
{
    public record HoverCall(int ScreenX, int ScreenY);
    public record MoveCursorCall(int ScreenX, int ScreenY);
    public record ClickCall(int ScreenX, int ScreenY, bool DoubleClick, bool RightClick, int SettleMs = 0);
    public record DragCall(int FromX, int FromY, int ToX, int ToY, bool RightButton, int HoldMs = 0, int DwellMs = 0, int SettleMs = 50);
    public record ScrollWheelCall(int ScreenX, int ScreenY, int Delta, int SettleMs = 30);

    public List<HoverCall> HoverCalls { get; } = [];
    public List<MoveCursorCall> MoveCursorCalls { get; } = [];
    public List<ClickCall> ClickCalls { get; } = [];
    public List<DragCall> DragCalls { get; } = [];
    public List<ScrollWheelCall> ScrollWheelCalls { get; } = [];

    public void Hover(int screenX, int screenY) => HoverCalls.Add(new(screenX, screenY));
    public void MoveCursor(int screenX, int screenY) => MoveCursorCalls.Add(new(screenX, screenY));
    public void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50)
        => ClickCalls.Add(new(screenX, screenY, doubleClick, rightClick, settleMs));
    public void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0, int settleMs = 50)
        => DragCalls.Add(new(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton, holdMs, dwellMs, settleMs));
    public void ScrollWheel(int screenX, int screenY, int delta, int settleMs = 30)
        => ScrollWheelCalls.Add(new(screenX, screenY, delta, settleMs));
}

/// <summary>
/// Fake pointer input for testing — records injected touch contacts and pen strokes instead of
/// issuing real synthetic-pointer injection.
/// </summary>
public class FakePointerInput : IPointerInput
{
    public record TouchCall(
        TouchGesture Gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> ContactPaths,
        int HoldMs,
        int DurationMs);

    public record PenCall(
        IReadOnlyList<PointerPoint> Path,
        float Pressure,
        int TiltX,
        int TiltY,
        bool Eraser,
        int DurationMs);

    public List<TouchCall> TouchCalls { get; } = [];
    public List<PenCall> PenCalls { get; } = [];

    /// <summary>When non-null, both Touch() and Pen() throw this exception instead of recording the call.
    /// Use to test command-level exception handling without a live injection path.</summary>
    public Exception? ThrowException { get; set; }

    public void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs)
    {
        if (ThrowException is not null) { throw ThrowException; }
        TouchCalls.Add(new(gesture, contactPaths, holdMs, durationMs));
    }

    public void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs)
    {
        if (ThrowException is not null) { throw ThrowException; }
        PenCalls.Add(new(path, pressure, tiltX, tiltY, eraser, durationMs));
    }
}

/// <summary>
/// Fake keyboard input for testing — records the actions/transport instead of issuing real input.
/// </summary>
public class FakeKeyboardInput : IKeyboardInput
{
    public record SendCall(long Hwnd, IReadOnlyList<KeyAction> Actions, KeyTransport Transport);

    public List<SendCall> SendCalls { get; } = [];

    /// <summary>When set, <see cref="Send"/> records the call then throws this — lets a test drive the
    /// command's exception mapping (e.g. a mid-injection <c>ForegroundLostException</c> → foreground_not_target).</summary>
    public Exception? SendException { get; set; }

    public void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport)
    {
        SendCalls.Add(new(hwnd, actions, transport));
        if (SendException is not null)
        {
            throw SendException;
        }
    }
}

/// <summary>
/// Fake foreground guard for testing — lets a test force the pre-injection foreground decision so the
/// coordinate-gesture verbs can be exercised without a live, unlocked desktop. Proceeds by default.
/// </summary>
public class FakeForegroundGuard : IForegroundGuard
{
    public record EnsureCall(long TargetHwnd);

    public List<EnsureCall> Calls { get; } = [];

    /// <summary>When <see langword="false"/>, denies the gesture with <see cref="DenyReason"/>.</summary>
    public bool Allow { get; set; } = true;

    /// <summary>When set, denies exactly the Nth call (1-based) regardless of <see cref="Allow"/>, letting
    /// a test drive the *final* foreground gate (e.g. click/scroll/drag check the foreground twice —
    /// first gate passes, final gate denies). Other calls fall back to <see cref="Allow"/>.</summary>
    public int? DenyOnCallNumber { get; set; }

    /// <summary>Outcome returned on denial — defaults to the locked-desktop reason.</summary>
    public ForegroundCheck DenyReason { get; set; } = ForegroundCheck.NoInteractiveDesktop;

    /// <summary>Value returned by <see cref="IsRemoteSession"/> — drives the remote-session delivery warning on touch/pen.</summary>
    public bool IsRemoteSessionResult { get; set; }

    public bool IsRemoteSession() => IsRemoteSessionResult;

    public ForegroundCheck CheckForeground(long targetHwnd)
    {
        Calls.Add(new(targetHwnd));

        var deny = DenyOnCallNumber is int n ? Calls.Count == n : !Allow;
        return deny ? DenyReason : ForegroundCheck.Proceed;
    }
}

/// <summary>
/// Fake <see cref="IOwnedWindowFinder"/> — returns a configurable owned-window list
/// (default empty) so the screenshot command's owned-dialog / multi-window discovery can be exercised
/// without a live desktop. The real finder issues a Win32 window walk.
/// </summary>
public class FakeOwnedWindowFinder : IOwnedWindowFinder
{
    /// <summary>Owned windows returned for any input. Default empty = "no owned dialogs".</summary>
    public List<(nint Hwnd, int Pid, string Title)> OwnedWindowsResult { get; set; } = [];

    public List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows)
        => OwnedWindowsResult;
}

/// <summary>
/// Fake <see cref="ISystemUiQuery"/> — drives <see cref="UiTargetResolver"/>'s OS boundaries
/// (process enumeration + a few Win32 window queries) from in-memory data so its resolver logic can
/// be exercised deterministically. Every knob defaults to "nothing found" so a bare instance yields
/// the same behavior a headless box would (no processes, no foreground, no window title).
/// </summary>
public sealed class FakeSystemUiQuery : ISystemUiQuery
{
    /// <summary>Explicit per-PID lookups. A key mapped to <c>null</c> models "no such process".</summary>
    public Dictionary<int, UiProcessInfo?> ProcessesById { get; } = [];

    /// <summary>When set, any PID not in <see cref="ProcessesById"/> resolves to this snapshot
    /// (with its <see cref="UiProcessInfo.Id"/> swapped to the requested PID). Lets CreateTarget's
    /// name lookup succeed for arbitrary PIDs without seeding each one.</summary>
    public UiProcessInfo? DefaultProcessById { get; set; }

    /// <summary>Result for <see cref="GetProcessesByName"/> (exact-name match). Default empty.</summary>
    public List<UiProcessInfo> ByNameResult { get; set; } = [];

    /// <summary>Result for <see cref="GetProcessesMatching"/> (partial-name match). Default empty.</summary>
    public List<UiProcessInfo> MatchingResult { get; set; } = [];

    /// <summary>Handle returned by <see cref="GetForegroundWindow"/>. Default 0 = "no foreground".</summary>
    public nint ForegroundWindowResult { get; set; }

    /// <summary>PID returned by <see cref="GetProcessIdForWindow"/>. Default 0 = "window not found".</summary>
    public uint ProcessIdForWindowResult { get; set; }

    /// <summary>Title returned by <see cref="GetWindowText"/>. Default null = "no/empty title".</summary>
    public string? WindowTextResult { get; set; }

    /// <summary>Per-HWND window sizes for <see cref="GetWindowSize"/>. Unmapped handles report (0, 0),
    /// matching a headless box; seed distinct areas to drive the "largest window" auto-select heuristic.</summary>
    public Dictionary<long, (int Width, int Height)> WindowSizeByHwnd { get; } = [];

    /// <summary>Per-HWND class names for <see cref="GetWindowClassName"/>. Unmapped handles report null.</summary>
    public Dictionary<long, string?> WindowClassNameByHwnd { get; } = [];

    /// <summary>Per-HWND owner handles for <see cref="GetWindowOwner"/>. Unmapped handles report 0 (no owner).</summary>
    public Dictionary<long, nint> WindowOwnerByHwnd { get; } = [];

    /// <summary>Per-HWND focused child handles for <see cref="GetFocusedWindow"/>. Unmapped handles report 0
    /// (no resolvable focus → the command keeps the passed target HWND).</summary>
    public Dictionary<long, long> FocusedWindowByHwnd { get; } = [];

    /// <summary>Per-HWND top-level root handles for <see cref="GetRootWindow"/>. Unmapped handles report the
    /// handle itself (a top-level window is its own root); map a child to its top-level window to model
    /// <c>GetAncestor(GA_ROOT)</c>.</summary>
    public Dictionary<long, long> RootWindowByHwnd { get; } = [];

    public UiProcessInfo? GetProcessById(int pid)
    {
        if (ProcessesById.TryGetValue(pid, out var info)) { return info; }
        if (DefaultProcessById is { } d) { return d with { Id = pid }; }
        return null;
    }

    public IReadOnlyList<UiProcessInfo> GetProcessesByName(string name) => ByNameResult;

    public IReadOnlyList<UiProcessInfo> GetProcessesMatching(string substring) => MatchingResult;

    public nint GetForegroundWindow() => ForegroundWindowResult;

    public uint GetProcessIdForWindow(long hwnd) => ProcessIdForWindowResult;

    public string? GetWindowText(long hwnd) => WindowTextResult;

    public (int Width, int Height) GetWindowSize(long hwnd)
        => WindowSizeByHwnd.TryGetValue(hwnd, out var size) ? size : (0, 0);

    public string? GetWindowClassName(long hwnd)
        => WindowClassNameByHwnd.TryGetValue(hwnd, out var name) ? name : null;

    public nint GetWindowOwner(long hwnd)
        => WindowOwnerByHwnd.TryGetValue(hwnd, out var owner) ? owner : 0;

    public long GetFocusedWindow(long hwnd)
        => FocusedWindowByHwnd.TryGetValue(hwnd, out var focused) ? focused : 0;

    public long GetRootWindow(long hwnd)
        => RootWindowByHwnd.TryGetValue(hwnd, out var root) ? root : hwnd;
}

/// <summary>
/// Fake <see cref="IPollDelay"/> — replaces wait-for's inter-poll wall-clock wait so
/// the retry-loop continuations run deterministically. Uses a 1ms yield (not a busy no-op) to keep poll
/// counts bounded without depending on real 100ms sleeps. Records how many times it was awaited.
/// </summary>
public sealed class FakePollDelay : IPollDelay
{
    /// <summary>Number of inter-poll delays awaited — one per "condition not met, keep polling" iteration.</summary>
    public int CallCount { get; private set; }

    public Task DelayAsync(int milliseconds, CancellationToken ct)
    {
        CallCount++;
        return Task.Delay(1, ct);
    }
}
