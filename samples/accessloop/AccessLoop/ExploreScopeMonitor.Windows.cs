using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AccessLoop;

internal sealed class ExploreNativeWindowSource : IExploreWindowSource
{
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private uint threadId;
    private Action<ExploreWindowSignal>? changed;
    private Exception? startupError;
    private WinEventProc? callback;
    private volatile bool stopping;

    public ExploreWindowBoundary Read(AuditTarget target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Bounded state exploration requires Windows.");
        var result = new List<ExploreScopeWindow>();
        var visited = new HashSet<nint>();
        for (var hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, 2))
        {
            if (!visited.Add(hwnd) || visited.Count > 10000)
                throw new InvalidOperationException("The native window list exceeded the bounded scope-check budget.");
            if (!IsWindowVisible(hwnd))
                continue;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == target.ProcessId || AuditWindowIdentity.HasOwner(hwnd, (nint)target.WindowHandle, window => GetWindow(window, 4)))
            {
                string? title = null;
                if (pid == target.ProcessId)
                {
                    var text = new StringBuilder(256);
                    _ = GetWindowText(hwnd, text, text.Capacity);
                    title = text.ToString();
                }
                result.Add(new((long)hwnd, checked((int)pid), (long)GetWindow(hwnd, 4), title));
            }
        }
        return new((long)GetForegroundWindow(), result);
    }

    public bool TryForeground(AuditTarget target) => SetForegroundWindow(checked((nint)target.WindowHandle));

    public void Start(AuditTarget target, Action<ExploreWindowSignal> changed)
    {
        if (thread is not null)
            throw new InvalidOperationException("The native boundary source has already started.");
        this.changed = changed;
        thread = new(() => MessageLoop(target)) { IsBackground = true, Name = "AccessLoop explore boundary" };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The native boundary monitor did not initialize.");
        if (startupError is not null)
            throw new InvalidOperationException("The native boundary monitor could not install its event hooks.", startupError);
    }

    private void MessageLoop(AuditTarget target)
    {
        nint foregroundHook = 0, windowHook = 0;
        nuint timer = 0;
        try
        {
            threadId = GetCurrentThreadId();
            callback = (_, eventId, hwnd, objectId, childId, _, _) =>
            {
                if (eventId == 3)
                    changed!(new("foreground", (long)hwnd));
                else if (objectId == 0 && childId == 0 && hwnd != 0)
                    changed!(new(eventId == 0x8001 ? "destroyed" : "window", (long)hwnd));
            };
            foregroundHook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
            windowHook = SetWinEventHook(0x8000, 0x800B, 0, callback, checked((uint)target.ProcessId), 0, 0);
            timer = SetTimer(0, 0, 100, 0);
            if (foregroundHook == 0 || windowHook == 0 || timer == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            if (stopping) return;
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Id == 0x0113)
                    changed!(new("tick"));
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
            if (result < 0)
                changed!(new("foreground", 0)); // An unavailable monitor must fail closed.
        }
        catch (Exception ex)
        {
            startupError = ex;
            if (ready.IsSet)
                changed?.Invoke(new("foreground", 0));
        }
        finally
        {
            ready.Set();
            if (timer != 0) _ = KillTimer(0, timer);
            if (foregroundHook != 0) _ = UnhookWinEvent(foregroundHook);
            if (windowHook != 0) _ = UnhookWinEvent(windowHook);
        }
    }

    public void Dispose()
    {
        stopping = true;
        if (thread is not null)
        {
            _ = PostThreadMessage(threadId, 0x0012, 0, 0);
            if (thread.Join(TimeSpan.FromSeconds(2))) ready.Dispose();
        }
        else ready.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)] private static extern int GetMessage(out Message message, nint hwnd, uint first, uint last);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
