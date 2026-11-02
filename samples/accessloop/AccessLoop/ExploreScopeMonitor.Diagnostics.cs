using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AccessLoop;

internal sealed record ExploreWindowDiagnostic(
    DateTimeOffset AtUtc, long Timestamp, string Source,
    string? StepId = null, long? ForegroundWindow = null, uint? ProcessId = null,
    string? ProcessName = null, string? IdentityError = null, string? InputDesktop = null,
    long? EventWindow = null, uint? EventId = null, uint? EventThread = null, uint? EventTime = null,
    string? ExceptionType = null, int? ErrorCode = null, string? StackTrace = null, string? Reason = null,
    bool? InputDesktopReceivesInput = null, string? DesktopError = null);

internal sealed partial class ExploreNativeWindowSource
{
    private const int DiagnosticCapacity = 256;
    private readonly object diagnosticGate = new();
    private readonly Queue<ExploreWindowDiagnostic> diagnosticBefore = new();
    private readonly List<ExploreWindowDiagnostic> diagnosticAfter = [];
    private string? diagnosticPath;
    private string? diagnosticStep;
    private ExploreEvent? diagnosticStop;
    private long diagnosticStopTimestamp;
    private int diagnosticBeforeDropped;
    private int diagnosticAfterDropped;
    private bool diagnosticHasNativeRead;

    internal void EnableDiagnostics(string path) => diagnosticPath = path;
    internal void SetDiagnosticStep(string step) => Volatile.Write(ref diagnosticStep, step);

    internal void RecordDiagnostic(ExploreWindowDiagnostic observation)
    {
        if (diagnosticPath is null) return;
        lock (diagnosticGate)
        {
            observation = observation with { StepId = Volatile.Read(ref diagnosticStep) };
            if (diagnosticStop is not null)
            {
                if (diagnosticAfter.Count < DiagnosticCapacity)
                    diagnosticAfter.Add(observation);
                else
                    diagnosticAfterDropped++;
                return;
            }
            while (diagnosticBefore.TryPeek(out var oldest)
                && observation.Timestamp - oldest.Timestamp > Stopwatch.Frequency)
                diagnosticBefore.Dequeue();
            if (diagnosticBefore.Count == DiagnosticCapacity)
            {
                diagnosticBefore.Dequeue();
                diagnosticBeforeDropped++;
            }
            diagnosticBefore.Enqueue(observation);
        }
    }

    internal void RecordDiagnosticStop(ExploreEvent stop)
    {
        if (diagnosticPath is null) return;
        lock (diagnosticGate)
        {
            if (diagnosticStop is not null) return;
            diagnosticStop = stop;
            diagnosticStopTimestamp = Stopwatch.GetTimestamp();
            RecordDiagnostic(new(stop.AtUtc, diagnosticStopTimestamp, "guard-stop", Reason: stop.Message));
        }
    }

    internal IReadOnlyList<ExploreWindowDiagnostic> DiagnosticSnapshot()
    {
        lock (diagnosticGate)
            return diagnosticBefore.Concat(diagnosticAfter).OrderBy(entry => entry.Timestamp).ToArray();
    }

    internal void WriteDiagnostics()
    {
        if (diagnosticPath is null) return;
        lock (diagnosticGate)
            AuditFiles.WriteNew(diagnosticPath, new
            {
                schemaVersion = 1,
                timestampFrequency = Stopwatch.Frequency,
                stop = diagnosticStop,
                beforeCapacityDropped = diagnosticBeforeDropped,
                afterCapacityDropped = diagnosticAfterDropped,
                entries = DiagnosticSnapshot()
            });
    }

    private long ReadDiagnosticForeground(string source)
    {
        var foreground = GetForegroundWindow();
        var observedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        if (diagnosticPath is null) return (long)foreground;
        Volatile.Write(ref diagnosticHasNativeRead, true);
        uint? processId = null;
        string? processName = null;
        string? identityError = null;
        if (foreground != 0)
        {
            var ownerThread = GetWindowThreadProcessId(foreground, out var owner);
            if (ownerThread == 0)
                identityError = $"GetWindowThreadProcessId failed with Win32 error {Marshal.GetLastPInvokeError()}.";
            else
            {
                processId = owner;
                try
                {
                    using var process = Process.GetProcessById(checked((int)owner));
                    processName = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    identityError = $"{ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}";
                }
            }
        }
        var desktop = ReadDiagnosticDesktop();
        RecordDiagnostic(new(observedAt, timestamp, source,
            ForegroundWindow: (long)foreground, ProcessId: processId, ProcessName: processName,
            IdentityError: identityError, InputDesktop: desktop.Name,
            InputDesktopReceivesInput: desktop.ReceivesInput, DesktopError: desktop.Error));
        return (long)foreground;
    }

    internal void RecordDiagnosticFailure(string source, Exception error, int? nativeError = null) =>
        RecordDiagnostic(new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), source,
            ExceptionType: error.GetType().FullName,
            ErrorCode: nativeError ?? (error is Win32Exception native ? native.NativeErrorCode : error.HResult),
            StackTrace: error.StackTrace ?? new StackTrace(1, true).ToString()));

    private void ObserveAfterDiagnosticStop()
    {
        long start;
        lock (diagnosticGate)
        {
            if (diagnosticPath is null || diagnosticStop is null || !Volatile.Read(ref diagnosticHasNativeRead)) return;
            start = Stopwatch.GetTimestamp();
        }
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1))
        {
            _ = ReadDiagnosticForeground("post-stop-poll-before-app-cleanup");
            Thread.Sleep(25);
        }
    }

    private (string? Name, bool? ReceivesInput, string? Error) ReadDiagnosticDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0)
            return (null, null, $"OpenInputDesktop failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        try
        {
            var name = new StringBuilder(128);
            if (!GetUserObjectInformation(desktop, 2, name, checked((uint)name.Capacity * 2), out _))
                return (null, null, $"GetUserObjectInformation(UOI_NAME) failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            if (!GetUserObjectInputInformation(desktop, 6, out var receivesInput, sizeof(int), out _))
                return (name.ToString(), null, $"GetUserObjectInformation(UOI_IO) failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            return (name.ToString(), receivesInput != 0, null);
        }
        finally
        {
            if (!CloseDesktop(desktop))
            {
                var error = Marshal.GetLastPInvokeError();
                RecordDiagnosticFailure("CloseDesktop", new Win32Exception(error), error);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, uint length, out uint needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInputInformation(nint handle, int index, out int information, uint length, out uint needed);
}
