// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Services.Performance;

internal static class PerfWindowEventTypes
{
    public const string Observed = "observed";
    public const string Removed = "removed";
    public const string Visible = "visible";
    public const string Hidden = "hidden";
    public const string Responsive = "responsive";
    public const string ResponseDelayed = "response-delayed";
    public const string ResponseRecovered = "response-recovered";
    public const string ProbeFailed = "probe-failed";

    public static bool IsKnown(string type) => type is
        Observed or Removed or Visible or Hidden or Responsive or ResponseDelayed or ResponseRecovered or ProbeFailed;
}

internal sealed record PerfWindowEvent(
    string Type,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    long Qpc,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    long WindowHandle,
    uint? ThreadId = null,
    string? ProbeOutcome = null,
    int? Win32Error = null);

internal sealed class PerfWindowEvidence
{
    public const int CadenceMs = 250;
    public const int TimeoutMs = 100;
    public const int MaximumEvents = 896;

    public string Method { get; set; } = "SendMessageTimeout(WM_NULL)";
    public int RequestedCadenceMs { get; set; } = CadenceMs;
    public int TimeoutMsValue { get; set; } = TimeoutMs;
    public bool Truncated { get; set; }
    public List<PerfWindowEvent> Events { get; set; } = [];

    public bool Add(PerfWindowEvent value)
    {
        if (Events.Count >= MaximumEvents)
        {
            var changed = !Truncated;
            Truncated = true;
            return changed;
        }

        Events.Add(value);
        return true;
    }

    public bool IsValid() =>
        Method == "SendMessageTimeout(WM_NULL)" &&
        RequestedCadenceMs == CadenceMs &&
        TimeoutMsValue == TimeoutMs &&
        Events.Count <= MaximumEvents &&
        Events.All(value =>
            PerfWindowEventTypes.IsKnown(value.Type) &&
            value.WindowHandle != 0 &&
            value.ThreadId is null or > 0 &&
            (value.ProbeOutcome is null ||
                Enum.TryParse<PerfWindowProbeOutcome>(value.ProbeOutcome, out _)));
}

internal enum PerfWindowProbeOutcome
{
    Responsive,
    Timeout,
    InvalidWindowHandle,
    AccessDenied,
    Win32Failure,
}

internal readonly record struct PerfWindowProbeResult(
    PerfWindowProbeOutcome Outcome,
    int? Win32Error = null);

internal readonly record struct PerfWindowSnapshot(
    long WindowHandle,
    uint ThreadId,
    bool IsVisible,
    PerfWindowProbeResult? Response = null);

internal interface IPerfWindowSnapshotSource
{
    IReadOnlyList<PerfWindowSnapshot> Snapshot(int processId);
}

internal readonly record struct PerfWindowMessageResult(
    bool Succeeded,
    int Win32Error);

internal interface IPerfWindowMessageNative
{
    PerfWindowMessageResult SendNullMessage(long windowHandle);
}

internal sealed class PerfWindowMessageNative : IPerfWindowMessageNative
{
    public unsafe PerfWindowMessageResult SendNullMessage(long windowHandle)
    {
        nuint result = 0;
        Marshal.SetLastPInvokeError(0);
        var succeeded = global::Windows.Win32.PInvoke.SendMessageTimeout(
            new HWND((nint)windowHandle),
            0,
            default,
            default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
            PerfWindowEvidence.TimeoutMs,
            &result);
        return new(succeeded != 0, Marshal.GetLastPInvokeError());
    }
}

internal interface IPerfWindowResponseProbe
{
    PerfWindowProbeResult Probe(long windowHandle);
}

internal sealed class PerfWindowResponseProbe(
    IPerfWindowMessageNative native) : IPerfWindowResponseProbe
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidWindowHandle = 1400;
    private const int ErrorTimeout = 1460;

    public PerfWindowProbeResult Probe(long windowHandle)
    {
        var result = native.SendNullMessage(windowHandle);
        if (result.Succeeded)
        {
            return new(PerfWindowProbeOutcome.Responsive);
        }

        var error = result.Win32Error;
        return error switch
        {
            ErrorTimeout => new(PerfWindowProbeOutcome.Timeout, error),
            ErrorInvalidWindowHandle => new(PerfWindowProbeOutcome.InvalidWindowHandle, error),
            ErrorAccessDenied => new(PerfWindowProbeOutcome.AccessDenied, error),
            _ => new(PerfWindowProbeOutcome.Win32Failure, error),
        };
    }
}

internal sealed class PerfWindowSnapshotSource(
    IPerfWindowResponseProbe responseProbe) : IPerfWindowSnapshotSource
{
    private int nextProbe;

    public IReadOnlyList<PerfWindowSnapshot> Snapshot(int processId)
    {
        var windows = new List<PerfWindowSnapshot>();
        var window = HWND.Null;
        while (true)
        {
            window = global::Windows.Win32.PInvoke.FindWindowEx(HWND.Null, window, null, (string?)null);
            if (window.IsNull)
            {
                break;
            }

            uint owner;
            uint thread;
            unsafe
            {
                owner = 0;
                thread = global::Windows.Win32.PInvoke.GetWindowThreadProcessId(window, &owner);
            }
            if (owner == processId && thread != 0)
            {
                windows.Add(new(
                    (long)(nint)window,
                    thread,
                    global::Windows.Win32.PInvoke.IsWindowVisible(window)));
            }
        }

        var visible = windows
            .Select((value, index) => (value, index))
            .Where(item => item.value.IsVisible)
            .ToArray();
        if (visible.Length == 0)
        {
            return windows;
        }

        var item = visible[nextProbe % visible.Length];
        windows[item.index] = item.value with
        {
            Response = responseProbe.Probe(item.value.WindowHandle),
        };
        nextProbe = (nextProbe + 1) % visible.Length;
        return windows;
    }
}

internal sealed class PerfWindowMonitor(
    IPerfWindowSnapshotSource source,
    PerfWindowEvidence evidence)
{
    private sealed class WindowState
    {
        public uint ThreadId { get; set; }
        public bool Visible { get; set; }
        public bool EverResponsive { get; set; }
        public bool? Responsive { get; set; }
        public PerfWindowProbeResult? LastProbeFailure { get; set; }
    }

    private readonly Dictionary<long, WindowState> windows = [];

    public bool Observe(int processId, long qpc)
    {
        var changed = false;
        var snapshots = source.Snapshot(processId);
        var observed = snapshots.Select(value => value.WindowHandle).ToHashSet();
        foreach (var removed in windows.Keys.Where(handle => !observed.Contains(handle)).ToArray())
        {
            var state = windows[removed];
            changed |= evidence.Add(new(
                PerfWindowEventTypes.Removed,
                qpc,
                removed,
                state.ThreadId));
            windows.Remove(removed);
        }

        foreach (var snapshot in snapshots)
        {
            if (!windows.TryGetValue(snapshot.WindowHandle, out var state))
            {
                state = new();
                windows.Add(snapshot.WindowHandle, state);
                changed |= Add(PerfWindowEventTypes.Observed, snapshot, qpc);
            }
            state.ThreadId = snapshot.ThreadId;

            if (snapshot.IsVisible && !state.Visible)
            {
                changed |= Add(PerfWindowEventTypes.Visible, snapshot, qpc);
            }
            else if (!snapshot.IsVisible && state.Visible)
            {
                changed |= Add(PerfWindowEventTypes.Hidden, snapshot, qpc);
                if (state.Responsive == false)
                {
                    state.Responsive = true;
                }
            }
            state.Visible = snapshot.IsVisible;

            if (!snapshot.IsVisible || snapshot.Response is not { } response)
            {
                continue;
            }

            if (response.Outcome == PerfWindowProbeOutcome.Responsive)
            {
                state.LastProbeFailure = null;
                if (!state.EverResponsive)
                {
                    state.EverResponsive = true;
                    changed |= Add(PerfWindowEventTypes.Responsive, snapshot, qpc);
                }
                else if (state.Responsive == false)
                {
                    changed |= Add(PerfWindowEventTypes.ResponseRecovered, snapshot, qpc);
                }
                state.Responsive = true;
            }
            else if (response.Outcome == PerfWindowProbeOutcome.Timeout)
            {
                state.LastProbeFailure = null;
                if (state.Responsive == true)
                {
                    changed |= Add(PerfWindowEventTypes.ResponseDelayed, snapshot, qpc, response);
                }
                state.Responsive = false;
            }
            else
            {
                if (state.LastProbeFailure != response)
                {
                    changed |= Add(PerfWindowEventTypes.ProbeFailed, snapshot, qpc, response);
                    state.LastProbeFailure = response;
                }
            }
        }

        return changed;
    }

    private bool Add(
        string type,
        PerfWindowSnapshot snapshot,
        long qpc,
        PerfWindowProbeResult? response = null) =>
        evidence.Add(new(
            type,
            qpc,
            snapshot.WindowHandle,
            snapshot.ThreadId,
            response?.Outcome.ToString(),
            response?.Win32Error));
}

internal sealed record PerfWindowExperience(
    string Status,
    double? FirstWindowObservedMs,
    double? FirstVisibleMs,
    double? FirstResponsiveMs,
    int ResponseDelayCount,
    int UnrecoveredResponseDelayCount,
    double? LongestObservedResponseDelayMs,
    int ProbeErrorCount,
    int RequestedCadenceMs,
    int ProbeTimeoutMs);

internal static class PerfWindowExperienceBuilder
{
    public static PerfWindowExperience Create(
        PerfCaptureDocument capture,
        PerfRange range)
    {
        if (!capture.WindowEvidenceRecorded)
        {
            return new(
                "not-recorded",
                null,
                null,
                null,
                0,
                0,
                null,
                0,
                PerfWindowEvidence.CadenceMs,
                PerfWindowEvidence.TimeoutMs);
        }

        var ready = capture.ReadyQpc!.Value;
        var frequency = capture.Frequency;
        var ordered = capture.WindowEvidence.Events.OrderBy(value => value.Qpc).ToArray();
        double Time(PerfWindowEvent value) => (value.Qpc - ready) * 1000d / frequency;
        double? First(string type) => ordered
            .Where(value => value.Type == type)
            .Select(Time)
            .Where(value => value >= range.FromMs && value <= range.ToMs)
            .Cast<double?>()
            .FirstOrDefault();

        var active = new Dictionary<long, double>();
        var observed = new HashSet<long>();
        foreach (var value in ordered)
        {
            var time = Time(value);
            if (time > range.FromMs)
            {
                break;
            }
            if (value.Type == PerfWindowEventTypes.Observed)
            {
                observed.Add(value.WindowHandle);
            }
            else if (value.Type == PerfWindowEventTypes.Removed)
            {
                observed.Remove(value.WindowHandle);
            }
        }
        var delays = 0;
        var unrecovered = 0;
        double? longest = null;
        foreach (var value in ordered)
        {
            var time = Time(value);
            if (value.Type == PerfWindowEventTypes.ResponseDelayed)
            {
                active.TryAdd(value.WindowHandle, time);
            }
            else if (value.Type is PerfWindowEventTypes.ResponseRecovered or PerfWindowEventTypes.Removed or
                PerfWindowEventTypes.Hidden &&
                active.Remove(value.WindowHandle, out var start))
            {
                if (AddInterval(start, time) && value.Type is PerfWindowEventTypes.Removed or PerfWindowEventTypes.Hidden)
                {
                    unrecovered++;
                }
            }
        }
        foreach (var start in active.Values)
        {
            if (AddInterval(start, range.ToMs))
            {
                unrecovered++;
            }
        }

        var errors = ordered.Count(value =>
        {
            var time = Time(value);
            return value.Type == PerfWindowEventTypes.ProbeFailed &&
                time >= range.FromMs && time <= range.ToMs;
        });
        var hasWindowEvidence = observed.Count > 0 || ordered.Any(value =>
        {
            var time = Time(value);
            return time >= range.FromMs && time <= range.ToMs;
        });
        var status = capture.WindowEvidence.Truncated || errors > 0
            ? "partial"
            : hasWindowEvidence ? "complete" : "not-observed";
        return new(
            status,
            First(PerfWindowEventTypes.Observed),
            First(PerfWindowEventTypes.Visible),
            First(PerfWindowEventTypes.Responsive),
            delays,
            unrecovered,
            longest,
            errors,
            capture.WindowEvidence.RequestedCadenceMs,
            capture.WindowEvidence.TimeoutMsValue);

        bool AddInterval(double start, double end)
        {
            var overlap = Math.Max(0, Math.Min(end, range.ToMs) - Math.Max(start, range.FromMs));
            if (overlap <= 0)
            {
                return false;
            }
            delays++;
            longest = Math.Max(longest ?? 0, overlap);
            return true;
        }
    }
}
