// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services.Performance;

/// <summary>
/// Identifies one process generation. A PID alone is insufficient because Windows can reuse it after
/// the process exits.
/// </summary>
internal readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks);

internal enum ProcessObservationFailure
{
    None,
    NotFound,
    AccessDenied,
}

internal readonly record struct ProcessObservationResult(
    IObservedProcess? Process,
    ProcessObservationFailure Failure)
{
    public bool Succeeded => Process is not null;
}

/// <summary>
/// A retained process handle and the immutable identity captured from it.
/// </summary>
internal interface IObservedProcess : IDisposable
{
    ProcessIdentity Identity { get; }

    bool HasExited { get; }

    bool TryGetExitCode(out int exitCode);

    ProcessResourceCounters CaptureResourceCounters();
}

/// <summary>
/// Opens a process and captures its reuse-safe identity.
/// </summary>
internal interface IProcessIdentityProbe
{
    ProcessObservationResult Observe(int processId);
}

internal sealed class ProcessIdentityProbe : IProcessIdentityProbe
{
    public ProcessObservationResult Observe(int processId)
    {
        if (processId <= 0)
        {
            return new(null, ProcessObservationFailure.NotFound);
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new(null, ProcessObservationFailure.NotFound);
        }

        try
        {
            // Force Process to open and retain its native handle before capturing StartTime. The handle
            // keeps referring to this generation even after exit and prevents a later PID reuse from
            // changing the identity or exit evidence underneath the recorder.
            _ = process.SafeHandle;
            var identity = new ProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks);
            return new(new ObservedProcess(process, identity), ProcessObservationFailure.None);
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return new(null, ProcessObservationFailure.NotFound);
        }
        catch (Win32Exception)
        {
            process.Dispose();
            return new(null, ProcessObservationFailure.AccessDenied);
        }
    }
}

internal sealed class ObservedProcess(
    Process process,
    ProcessIdentity identity) : IObservedProcess
{
    public ProcessIdentity Identity { get; } = identity;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public bool TryGetExitCode(out int exitCode)
    {
        if (!HasExited)
        {
            exitCode = default;
            return false;
        }

        try
        {
            if (global::Windows.Win32.PInvoke.GetExitCodeProcess(process.SafeHandle, out var nativeExitCode))
            {
                exitCode = unchecked((int)nativeExitCode);
                return true;
            }

            exitCode = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            exitCode = default;
            return false;
        }
    }

    public ProcessResourceCounters CaptureResourceCounters()
    {
        var processorTimes = TryGetProcessorTimes();
        var io = TryGetIoCounters();
        if (HasExited)
        {
            return CreateTerminalCounters(processorTimes, io);
        }

        try
        {
            process.Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return HasExited
                ? CreateTerminalCounters(TryGetProcessorTimes(), TryGetIoCounters())
                : new(Identity);
        }

        var counters = new ProcessResourceCounters(
            Identity,
            TotalProcessorTimeTicks: Add(processorTimes?.UserTicks, processorTimes?.KernelTicks),
            UserProcessorTimeTicks: processorTimes?.UserTicks,
            KernelProcessorTimeTicks: processorTimes?.KernelTicks,
            PrivateBytes: TryRead(() => process.PrivateMemorySize64),
            WorkingSetBytes: TryRead(() => process.WorkingSet64),
            ReadOperationCount: io?.ReadOperationCount,
            WriteOperationCount: io?.WriteOperationCount,
            OtherOperationCount: io?.OtherOperationCount,
            ReadBytes: io?.ReadTransferCount,
            WriteBytes: io?.WriteTransferCount,
            OtherBytes: io?.OtherTransferCount,
            ThreadCount: TryRead(() => process.Threads.Count),
            HandleCount: TryRead(() => process.HandleCount),
            GdiObjectCount: TryGetGuiResources(GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS),
            UserObjectCount: TryGetGuiResources(GET_GUI_RESOURCES_FLAGS.GR_USEROBJECTS));
        return HasExited
            ? CreateTerminalCounters(TryGetProcessorTimes(), TryGetIoCounters())
            : counters;
    }

    private unsafe Windows.Win32.System.Threading.IO_COUNTERS? TryGetIoCounters()
    {
        try
        {
            return global::Windows.Win32.PInvoke.GetProcessIoCounters(
                process.SafeHandle,
                out var counters)
                ? counters
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private unsafe (long KernelTicks, long UserTicks)? TryGetProcessorTimes()
    {
        try
        {
            if (!global::Windows.Win32.PInvoke.GetProcessTimes(
                process.SafeHandle,
                out _,
                out _,
                out var kernel,
                out var user))
            {
                return null;
            }

            return (ToTicks(kernel), ToTicks(user));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private ProcessResourceCounters CreateTerminalCounters(
        (long KernelTicks, long UserTicks)? processorTimes,
        Windows.Win32.System.Threading.IO_COUNTERS? io) => new(
            Identity,
            IsTerminal: true,
            TotalProcessorTimeTicks: Add(processorTimes?.UserTicks, processorTimes?.KernelTicks),
            UserProcessorTimeTicks: processorTimes?.UserTicks,
            KernelProcessorTimeTicks: processorTimes?.KernelTicks,
            ReadOperationCount: io?.ReadOperationCount,
            WriteOperationCount: io?.WriteOperationCount,
            OtherOperationCount: io?.OtherOperationCount,
            ReadBytes: io?.ReadTransferCount,
            WriteBytes: io?.WriteTransferCount,
            OtherBytes: io?.OtherTransferCount);

    private static long ToTicks(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        unchecked((long)(((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime));

    private uint? TryGetGuiResources(GET_GUI_RESOURCES_FLAGS flag)
    {
        try
        {
            Marshal.SetLastPInvokeError(0);
            var count = global::Windows.Win32.PInvoke.GetGuiResources(process.SafeHandle, flag);
            return count != 0 || Marshal.GetLastPInvokeError() == 0 ? count : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static long? Add(long? left, long? right) =>
        left is { } leftValue && right is { } rightValue
            ? leftValue + rightValue
            : null;

    public void Dispose() => process.Dispose();
}
