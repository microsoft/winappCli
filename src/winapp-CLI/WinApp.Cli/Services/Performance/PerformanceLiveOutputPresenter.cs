// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.Services.Performance;

internal sealed class PerformanceLiveOutputPresenter(bool verbose)
{
    private readonly Dictionary<ResponseTarget, double> _activeFailures = [];
    private readonly Dictionary<ResponseTarget, int> _visibleWindows = [];
    private ProcessIdentity? _targetProcess;

    public static void WriteStart(TextWriter output, DateTimeOffset startedUtc)
    {
        var local = startedUtc.ToLocalTime();
        output.WriteLine(
            $"Recording started: {local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        output.WriteLine("Press Ctrl+C to stop.");
    }

    public void Write(
        TextWriter output,
        IReadOnlyList<PerformanceTimelineEntry> events,
        ProcessIdentity? targetProcess = null)
    {
        _targetProcess = targetProcess ?? _targetProcess;
        foreach (var entry in events)
        {
            var line = Format(entry);
            if (line is not null)
            {
                output.WriteLine(line);
            }
        }
    }

    internal string? Format(PerformanceTimelineEntry entry) =>
        verbose ? FormatVerbose(entry) : FormatDefault(entry);

    private string? FormatDefault(PerformanceTimelineEntry entry)
    {
        var prefix = FormatTimelinePosition(entry.ElapsedMs);
        return entry.Type switch
        {
            nameof(StartupEventType.ActivationRequested) =>
                $"{prefix} App activation requested",
            nameof(StartupEventType.ProcessObserved) =>
                !IsTargetProcess(entry)
                    ? null
                    : entry.WasPresentBeforeActivation == true
                    ? $"{prefix} Attached to an existing app process"
                    : $"{prefix} App process started",
            nameof(StartupEventType.WindowObserved) => null,
            nameof(StartupEventType.WindowVisible) =>
                $"{prefix} {GetWindowLabel(entry, registerVisible: true)} visible",
            nameof(StartupEventType.WindowResponsive) =>
                entry.WasPresentBeforeActivation == true
                    ? $"{prefix} {GetWindowLabel(entry)} responding"
                    : $"{prefix} {GetWindowLabel(entry)} responding - " +
                      $"{FormatDuration(entry.ElapsedMs)} after activation",
            nameof(StartupEventType.WindowResponseFailed) =>
                RecordFailure(entry, $"{prefix} {GetWindowLabel(entry)} stopped responding"),
            nameof(StartupEventType.WindowResponseProbeFailed) =>
                $"{prefix} Could not check {GetWindowLabel(entry).ToLowerInvariant()} responsiveness" +
                " - run with --verbose for details",
            nameof(StartupEventType.WindowResponseRecovered) =>
                FormatRecovery(entry, prefix),
            nameof(StartupEventType.ProcessExited) =>
                IsTargetProcess(entry) ? FormatProcessExit(entry, prefix) : null,
            _ => null,
        };
    }

    private static string FormatVerbose(PerformanceTimelineEntry entry)
    {
        var details = new List<string>();
        if (entry.ProcessId is { } processId)
        {
            details.Add($"PID {processId}");
        }
        if (entry.WindowHandle is { } windowHandle)
        {
            details.Add($"HWND 0x{windowHandle:X}");
        }
        if (entry.WindowThreadId is { } threadId)
        {
            details.Add($"thread {threadId}");
        }
        if (entry.ResponseProbeOutcome is { } outcome)
        {
            details.Add($"probe {outcome}");
        }
        if (entry.Win32ErrorCode is { } error)
        {
            details.Add($"Win32 {error}");
        }
        if (entry.ExitCode is { } exitCode)
        {
            details.Add($"exit code {exitCode}");
        }
        if (entry.WasPresentBeforeActivation == true)
        {
            details.Add("present before activation");
        }

        return details.Count == 0
            ? $"{FormatTimelinePosition(entry.ElapsedMs)} {FormatEventName(entry.Type)}"
            : $"{FormatTimelinePosition(entry.ElapsedMs)} {FormatEventName(entry.Type)}; " +
              string.Join("; ", details);
    }

    private string RecordFailure(PerformanceTimelineEntry entry, string line)
    {
        if (TryGetTarget(entry, out var target))
        {
            _activeFailures.TryAdd(target, entry.ElapsedMs);
        }
        return line;
    }

    private string FormatRecovery(PerformanceTimelineEntry entry, string prefix)
    {
        var line = $"{prefix} {GetWindowLabel(entry)} responding again";
        if (TryGetTarget(entry, out var target)
            && _activeFailures.Remove(target, out var failureStartMs))
        {
            line += $" - observed unresponsive for {FormatDuration(entry.ElapsedMs - failureStartMs)}";
        }
        return line;
    }

    private static string FormatProcessExit(PerformanceTimelineEntry entry, string prefix) =>
        entry.ExitCode switch
        {
            0 => $"{prefix} App exited normally",
            { } exitCode => $"{prefix} App exited with code {exitCode}",
            null => $"{prefix} App exited; exit code unavailable",
        };

    private string GetWindowLabel(
        PerformanceTimelineEntry entry,
        bool registerVisible = false)
    {
        if (!TryGetTarget(entry, out var target))
        {
            return "App window";
        }

        if (registerVisible && !_visibleWindows.ContainsKey(target))
        {
            _visibleWindows[target] = _visibleWindows.Count + 1;
        }

        return _visibleWindows.TryGetValue(target, out var ordinal) && ordinal > 1
            ? $"App window {ordinal}"
            : "App window";
    }

    private static bool TryGetTarget(
        PerformanceTimelineEntry entry,
        out ResponseTarget target)
    {
        if (entry.WindowHandle is not { } windowHandle)
        {
            target = default;
            return false;
        }

        target = new(
            entry.ProcessId,
            entry.ProcessStartTimeUtcTicks,
            windowHandle);
        return true;
    }

    private bool IsTargetProcess(PerformanceTimelineEntry entry) =>
        _targetProcess is not { } target
        || (entry.ProcessId == target.ProcessId
            && entry.ProcessStartTimeUtcTicks == target.StartTimeUtcTicks);

    private static string FormatTimelinePosition(double elapsedMs)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
        var totalHours = (long)value.TotalHours;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[T+{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}]");
    }

    private static string FormatDuration(double durationMs) =>
        durationMs >= 1_000
            ? $"{durationMs / 1_000:0.00} s"
            : $"{durationMs:0} ms";

    private static string FormatEventName(string type) => type switch
    {
        nameof(StartupEventType.ActivationRequested) => "Activation requested",
        nameof(StartupEventType.ProcessObserved) => "Process observed",
        nameof(StartupEventType.WindowObserved) => "Window observed",
        nameof(StartupEventType.WindowVisible) => "Window visible",
        nameof(StartupEventType.WindowResponsive) => "Window responsive",
        nameof(StartupEventType.WindowResponseFailed) => "Window response failed",
        nameof(StartupEventType.WindowResponseProbeFailed) => "Window response probe failed",
        nameof(StartupEventType.WindowResponseRecovered) => "Window response recovered",
        nameof(StartupEventType.ProcessExited) => "Process exited",
        _ => type,
    };

    private readonly record struct ResponseTarget(
        int? ProcessId,
        long? ProcessStartTimeUtcTicks,
        long WindowHandle);
}
