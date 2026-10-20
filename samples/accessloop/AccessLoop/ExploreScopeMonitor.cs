namespace AccessLoop;

internal sealed record ExploreScopeWindow(long Handle, int ProcessId, long Owner, string? Title = null);
internal sealed record ExploreWindowBoundary(long ForegroundWindow, IReadOnlyList<ExploreScopeWindow> Windows);
internal sealed record ExploreWindowSignal(string Kind, long WindowHandle = 0);

internal interface IExploreWindowSource : IDisposable
{
    ExploreWindowBoundary Read(AuditTarget target);
    bool TryForeground(AuditTarget target);
    void Start(AuditTarget target, Action<ExploreWindowSignal> changed);
}

internal sealed class ExploreStoppedException(string message) : InvalidOperationException(message);

// This monitor reads native identities and desktop metadata, never another application's UIA tree.
internal sealed class ExploreScopeMonitor(
    IAuditPlatform platform, ISurveyNativeContext native, IExploreWindowSource windows) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly List<ExploreEvent> events = [];
    private AuditTarget? original;
    private SurveyNativeWindow? initial;
    private ExploreWindowBoundary? boundary;
    private ExploreEvent? violation;
    private ExploreScopeWindow? violationWindow;
    private long? approvedPopup;
    private bool closingPopup;
    private bool provisional;
    private DateTimeOffset provisionalDeadline;
    private bool disposed;

    public CancellationToken StopToken => stopped.Token;
    public ExploreEvent? Violation { get { lock (gate) return violation; } }
    public ExploreScopeWindow? ViolationWindow { get { lock (gate) return violationWindow; } }
    public bool Provisional { get { lock (gate) return provisional; } }
    public IReadOnlyList<ExploreEvent> Events { get { lock (gate) return events.ToArray(); } }
    public long? PopupWindow
    {
        get
        {
            lock (gate)
                return boundary?.Windows.SingleOrDefault(window => window.Handle != original?.WindowHandle)?.Handle;
        }
    }

    public void Start(AuditTarget target)
    {
        lock (gate)
        {
            if (original is not null)
                throw new InvalidOperationException("A scope monitor cannot be rebound.");
            original = target;
            try
            {
                platform.VerifyTarget(target);
                initial = native.ReadWindow(target);
                SurveyNativeContext.ValidateWindow(initial);
                boundary = windows.Read(target);
                RequireSingleInitialWindow(boundary);
                // This is the sole permitted foreground request, before the monitored interaction run.
                if (boundary.ForegroundWindow != target.WindowHandle && !windows.TryForeground(target))
                    throw new ExploreStoppedException("The approved window could not be foregrounded once. Activate it manually and start a new run.");
            }
            catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
            {
                Latch("scope-unavailable", ex.Message, target.WindowHandle);
                ThrowIfStopped();
            }
        }
        try
        {
            windows.Start(target, OnSignal);
            Check(target);
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            Stop("monitor-unavailable", ex.Message);
            throw new ExploreStoppedException(Violation!.Message);
        }
    }

    public void Check(AuditTarget target, bool input = false, bool audit = false)
    {
        lock (gate)
        {
            ThrowIfStopped();
            try
            {
                Refresh();
                if (target.ProcessId != original!.ProcessId
                    || target.ProcessStartedAtUtc != original.ProcessStartedAtUtc
                    || !string.Equals(target.ProcessName, original.ProcessName, StringComparison.OrdinalIgnoreCase)
                    || (target.WindowHandle != original.WindowHandle
                        && target.WindowHandle != approvedPopup
                        && !(provisional && target.WindowHandle == PopupWindow)))
                    Latch("scope-changed", "A command attempted to leave the original process and explicitly verified window scope.", target.WindowHandle);
                ThrowIfStopped();
            }
            catch (Exception ex) when (ex is not ExploreStoppedException && AuditRunner.IsExecutionFailure(ex))
            {
                Latch("scope-unavailable", ex.Message, original!.WindowHandle);
                ThrowIfStopped();
            }
            if (input && provisional)
                throw new InvalidOperationException("No further input is permitted while a declared popup is awaiting ownership and marker verification.");
            if (audit && (provisional || (approvedPopup is not null && target.WindowHandle == original!.WindowHandle)))
                throw new InvalidOperationException("An open top-level popup must be verified and audited by its own exact HWND, never through the parent window.");
        }
    }

    // Called only after the trigger's input guard has passed, immediately before that one command.
    public void BeginPopupTransition(TimeSpan timeout)
    {
        lock (gate)
        {
            ThrowIfStopped();
            Refresh();
            ThrowIfStopped();
            if (provisional || approvedPopup is not null)
                throw new InvalidOperationException("Nested popup transitions are not supported.");
            provisional = true;
            provisionalDeadline = DateTimeOffset.UtcNow + timeout;
        }
    }

    public void ApprovePopup(long? hwnd)
    {
        lock (gate)
        {
            ThrowIfStopped();
            Refresh();
            ThrowIfStopped();
            if (!provisional || PopupWindow != hwnd)
                throw new InvalidOperationException("The verified popup does not match the pending exact native window.");
            approvedPopup = hwnd;
            provisional = false;
            events.Add(new(DateTimeOffset.UtcNow, "popup-verified",
                hwnd is null ? "Declared markers verified in the original window."
                    : "Declared markers and same-process native ownership verified for the exact popup window.",
                hwnd ?? original!.WindowHandle, original!.ProcessId));
        }
    }

    public void EndPopup()
    {
        lock (gate)
        {
            ThrowIfStopped();
            Refresh();
            ThrowIfStopped();
            if (PopupWindow is not null)
                throw new InvalidOperationException("The declared popup is still visible; underlying-page input is not permitted.");
            provisional = false;
            approvedPopup = null;
            closingPopup = false;
        }
    }

    public void BeginPopupClose()
    {
        lock (gate)
        {
            ThrowIfStopped();
            Refresh();
            ThrowIfStopped();
            if (provisional)
                throw new InvalidOperationException("An unverified external popup cannot be closed.");
            closingPopup = approvedPopup is not null;
        }
    }

    public void Stop(string kind, string reason, long? windowHandle = null)
    {
        lock (gate)
            Latch(kind, reason, windowHandle ?? original?.WindowHandle);
    }

    private void OnSignal(ExploreWindowSignal signal)
    {
        lock (gate)
        {
            if (disposed || original is null || violation is not null)
                return;
            try
            {
                Refresh();
                // Retain a foreground excursion even if focus returned before the snapshot was read.
                if (signal.Kind == "foreground" && signal.WindowHandle != original.WindowHandle
                    && signal.WindowHandle != approvedPopup
                    && !(provisional && signal.WindowHandle == PopupWindow))
                    Latch("foreground-changed", "Foreground moved outside the approved window boundary. No automatic focus restoration is allowed.", signal.WindowHandle);
                if (signal.Kind == "destroyed" && signal.WindowHandle == original.WindowHandle)
                    Latch("scope-changed", "The original top-level window was destroyed.", signal.WindowHandle);
            }
            catch (Exception ex)
            {
                // Exceptions cannot cross a WinEvent callback; failure of the guard is itself a stop.
                Latch("monitor-failed", $"The window monitor failed closed: {ex.Message}", signal.WindowHandle);
            }
        }
    }

    private void Refresh()
    {
        if (original is null)
            throw new InvalidOperationException("The scope monitor has not started.");
        platform.VerifyTarget(original);
        var current = native.ReadWindow(original);
        SurveyNativeContext.ValidateWindow(current);
        if (initial is null || current.Width != initial.Width || current.Height != initial.Height || current.Dpi != initial.Dpi)
        {
            Latch("environment-changed", "The original window dimensions or DPI changed.", original.WindowHandle);
            return;
        }
        boundary = windows.Read(original);
        if (boundary.Windows.Count(window => window.Handle == original.WindowHandle && window.ProcessId == original.ProcessId) != 1
            || boundary.Windows.Select(window => window.Handle).Distinct().Count() != boundary.Windows.Count)
        {
            Latch("scope-changed", "The original native window identity is missing or ambiguous.", original.WindowHandle);
            return;
        }
        var extra = boundary.Windows.Where(window => window.Handle != original.WindowHandle).ToArray();
        if (approvedPopup is not null && !closingPopup && !extra.Any(window => window.Handle == approvedPopup))
        {
            Latch("window-boundary-changed", "The verified popup disappeared outside an authorized dismissal action.", approvedPopup);
            return;
        }
        if (provisional && DateTimeOffset.UtcNow > provisionalDeadline)
        {
            Latch("popup-unverified", "The bounded popup observation period expired without an approved scope.", extra.FirstOrDefault()?.Handle);
            return;
        }
        if (extra.Length > 1 || extra.Any(window => window.ProcessId != original.ProcessId || window.Owner != original.WindowHandle
            || (!provisional && window.Handle != approvedPopup)))
        {
            Latch("window-boundary-changed", "An unapproved target-process or owned top-level window appeared. No input or automatic restoration is allowed.", extra.FirstOrDefault()?.Handle);
            return;
        }
        if (boundary.ForegroundWindow != original.WindowHandle
            && boundary.ForegroundWindow != approvedPopup
            && !(provisional && extra.Length == 1 && boundary.ForegroundWindow == extra[0].Handle))
            Latch("foreground-changed", "Foreground moved outside the approved window boundary. No automatic focus restoration is allowed.", boundary.ForegroundWindow);
    }

    private void RequireSingleInitialWindow(ExploreWindowBoundary value)
    {
        if (value.Windows.Count != 1 || value.Windows[0].Handle != original!.WindowHandle
            || value.Windows[0].ProcessId != original.ProcessId)
            throw new ExploreStoppedException("Start with only the exact approved top-level application window visible; existing additional windows are not authorized.");
    }

    private void Latch(string kind, string reason, long? hwnd)
    {
        if (violation is not null)
            return;
        violationWindow = boundary?.Windows.FirstOrDefault(window => window.Handle == hwnd);
        violation = new(DateTimeOffset.UtcNow, kind, reason, hwnd,
            violationWindow?.ProcessId ?? (hwnd == original?.WindowHandle ? original?.ProcessId : null));
        events.Add(violation);
        stopped.Cancel();
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (violation is not null)
            throw new ExploreStoppedException(violation.Message);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        windows.Dispose();
        stopped.Dispose();
    }
}
