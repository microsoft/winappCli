// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Etw;

namespace WinApp.Cli.Services.Performance;

/// <summary>A PID-scoped private file logger. It never changes ETW access permissions.</summary>
internal sealed unsafe class PrivateEtwSession : IDisposable
{
    private const uint PidFilterType = 0x80000004;
    private const int StringBytes = 2048;
    private EVENT_TRACE_PROPERTIES_V2* properties;
    private EVENT_FILTER_DESCRIPTOR* filter;
    private uint* pid;
    private ulong handle;
    // A successful StartTrace can return zero; the handle is not a lifetime sentinel.
    private bool active;
    private readonly IPrivateEtwApi api;

    public string Name { get; }
    public Guid SessionId { get; }
    public uint? EventsLost { get; private set; }
    public uint? BuffersLost { get; private set; }
    public bool Stopped { get; private set; }
    public bool CanEnable => active && handle != 0;

    public PrivateEtwSession(string name, Guid sessionId, int processId, string outputPath, int maximumSizeMiB,
        IPrivateEtwApi? api = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (maximumSizeMiB is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSizeMiB));
        }
        if (!Path.IsPathFullyQualified(outputPath) || outputPath.Length > 990 || name.Length > 990)
        {
            throw new ArgumentException("ETL requires an absolute output path and session name of at most 990 characters.");
        }

        Name = name;
        SessionId = sessionId;
        this.api = api ?? NativePrivateEtwApi.Instance;
        try
        {
            properties = (EVENT_TRACE_PROPERTIES_V2*)NativeMemory.AllocZeroed(
                (nuint)(sizeof(EVENT_TRACE_PROPERTIES_V2) + 2 * StringBytes));
            pid = (uint*)NativeMemory.Alloc((nuint)sizeof(uint));
            *pid = (uint)processId;
            filter = (EVENT_FILTER_DESCRIPTOR*)NativeMemory.AllocZeroed((nuint)sizeof(EVENT_FILTER_DESCRIPTOR));
            *filter = new() { Ptr = (ulong)pid, Size = sizeof(uint), Type = PidFilterType };
            properties->Wnode.BufferSize = (uint)(sizeof(EVENT_TRACE_PROPERTIES_V2) + 2 * StringBytes);
            properties->Wnode.Guid = sessionId;
            properties->Wnode.ClientContext = 1;
            properties->Wnode.Flags = 0x00020000 | 0x00800000;
            properties->BufferSize = 64;
            properties->MinimumBuffers = 32;
            properties->MaximumBuffers = 256;
            properties->MaximumFileSize = (uint)maximumSizeMiB;
            properties->LogFileMode = 0x800 | 1;
            properties->FlushTimer = 1;
            properties->Anonymous2.V2Control = 2;
            properties->FilterDescCount = 1;
            properties->FilterDesc = filter;
            properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES_V2);
            properties->LogFileNameOffset = properties->LoggerNameOffset + StringBytes;
            WriteString(properties->LogFileNameOffset, outputPath);
            fixed (char* sessionName = name)
            {
                ulong started;
                Check(this.api.Start(&started, sessionName, (EVENT_TRACE_PROPERTIES*)properties), "StartTrace");
                handle = started;
                active = true;
            }
        }
        catch
        {
            Free();
            throw;
        }
    }

    public bool Enable(Guid provider, ulong keywords, byte level = 5, ushort[]? eventIds = null)
    {
        ObjectDisposedException.ThrowIf(properties == null || !active, this);
        if (!CanEnable)
        {
            throw new PerfEtwTargetNotReadyException();
        }
        var parameters = new ENABLE_TRACE_PARAMETERS
        {
            Version = 2,
            EnableFilterDesc = filter,
            FilterDescCount = 1,
        };
        if (eventIds is { Length: > 0 })
        {
            if (eventIds.Length > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(eventIds));
            }
            var idFilter = stackalloc byte[4 + 2 * eventIds.Length];
            idFilter[0] = 1;
            idFilter[1] = 0;
            *(ushort*)(idFilter + 2) = (ushort)eventIds.Length;
            eventIds.AsSpan().CopyTo(new Span<ushort>(idFilter + 4, eventIds.Length));
            var descriptors = stackalloc EVENT_FILTER_DESCRIPTOR[2];
            descriptors[0] = *filter;
            descriptors[1] = new()
            {
                Ptr = (ulong)idFilter,
                Size = (uint)(4 + 2 * eventIds.Length),
                Type = 0x80000200,
            };
            parameters.EnableFilterDesc = descriptors;
            parameters.FilterDescCount = 2;
            var filtered = api.Enable(handle, &provider, level, keywords, &parameters);
            if ((uint)filtered != 87)
            {
                Check(filtered, $"EnableTraceEx2({provider}, event IDs)");
                return true;
            }
            parameters.EnableFilterDesc = filter;
            parameters.FilterDescCount = 1;
        }
        Check(api.Enable(handle, &provider, level, keywords, &parameters),
            $"EnableTraceEx2({provider})");
        return false;
    }

    public void Stop()
    {
        if (!active)
        {
            return;
        }
        fixed (char* name = Name)
        {
            var result = api.Stop(handle, name, (EVENT_TRACE_PROPERTIES*)properties);
            if ((uint)result == 4201)
            {
                // The target may have taken its private buffers with it. Unknown loss is not zero.
                active = false;
                return;
            }

            Check(result, "ControlTrace(STOP)");
        }
        active = false;
        Stopped = true;
        EventsLost = properties->EventsLost;
        BuffersLost = properties->LogBuffersLost;
    }

    public static bool StopOwned(string name, Guid sessionId, int processId, string outputPath)
    {
        var size = sizeof(EVENT_TRACE_PROPERTIES_V2) + 2 * StringBytes;
        var query = (EVENT_TRACE_PROPERTIES_V2*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            uint pid = checked((uint)processId);
            var filter = new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)&pid, Size = sizeof(uint), Type = PidFilterType };
            query->Wnode.BufferSize = (uint)size;
            query->Wnode.Guid = sessionId;
            query->Wnode.Flags = 0x00020000 | 0x00800000;
            query->LogFileMode = 0x800 | 1;
            query->Anonymous2.V2Control = 2;
            query->FilterDesc = &filter;
            query->FilterDescCount = 1;
            query->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES_V2);
            query->LogFileNameOffset = query->LoggerNameOffset + StringBytes;
            fixed (char* loggerName = name)
            {
                var status = PInvoke.ControlTrace(0, loggerName, (EVENT_TRACE_PROPERTIES*)query,
                    EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_QUERY);
                if ((uint)status == 4201)
                {
                    return false;
                }
                Check(status, "ControlTrace(QUERY owned)");
                if (query->Wnode.Anonymous1.HistoricalContext == 0)
                {
                    return false;
                }
                if (query->Wnode.Guid != sessionId || query->LogFileNameOffset > size - StringBytes ||
                    query->LogFileNameOffset % 2 != 0)
                {
                    throw new InvalidOperationException("The ETW session identity does not match the capture; it was not stopped.");
                }
                var path = new ReadOnlySpan<char>((byte*)query + query->LogFileNameOffset, StringBytes / 2);
                var end = path.IndexOf('\0');
                if (end < 0 || !path[..end].Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The ETW output path does not match the capture; it was not stopped.");
                }
                Check(PInvoke.ControlTrace(0, loggerName, (EVENT_TRACE_PROPERTIES*)query,
                    EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_STOP), "ControlTrace(STOP owned)");
                return true;
            }
        }
        finally
        {
            NativeMemory.Free(query);
        }
    }

    internal static void Check(WIN32_ERROR result, string operation)
    {
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)result, $"{operation} failed ({(uint)result}). No tracing permissions were changed.");
        }
    }

    private void WriteString(uint offset, string value) =>
        (value + '\0').AsSpan().CopyTo(new Span<char>((byte*)properties + offset, StringBytes / 2));

    private void Free()
    {
        NativeMemory.Free(properties);
        NativeMemory.Free(filter);
        NativeMemory.Free(pid);
        properties = null;
        filter = null;
        pid = null;
    }

    public void Dispose()
    {
        Stop();
        Free();
    }
}

internal unsafe interface IPrivateEtwApi
{
    WIN32_ERROR Start(ulong* handle, char* name, EVENT_TRACE_PROPERTIES* properties);
    WIN32_ERROR Enable(ulong handle, Guid* provider, byte level, ulong keywords, ENABLE_TRACE_PARAMETERS* parameters);
    WIN32_ERROR Stop(ulong handle, char* name, EVENT_TRACE_PROPERTIES* properties);
}

internal sealed unsafe class NativePrivateEtwApi : IPrivateEtwApi
{
    internal static NativePrivateEtwApi Instance { get; } = new();

    public WIN32_ERROR Start(ulong* handle, char* name, EVENT_TRACE_PROPERTIES* properties) =>
        PInvoke.StartTrace(handle, name, properties);

    public WIN32_ERROR Enable(ulong handle, Guid* provider, byte level, ulong keywords, ENABLE_TRACE_PARAMETERS* parameters) =>
        PInvoke.EnableTraceEx2(handle, provider, 1, level, keywords, 0, 5000, parameters);

    public WIN32_ERROR Stop(ulong handle, char* name, EVENT_TRACE_PROPERTIES* properties) =>
        PInvoke.ControlTrace(handle, name, properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_STOP);
}

internal sealed class PerfEtwTargetNotReadyException() : Win32Exception(21,
    "The target process has not initialized a private ETW session. Let the app finish starting, then use perf start with its PID.");
