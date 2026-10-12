// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Etw;

namespace WinApp.Cli.Services.Performance;

/// <summary>
/// The processes a capture records. A PID-scoped private logger attaches to a running process without
/// administrator rights. An executable- or package-scoped system logger requires elevation and is started
/// before launch, so it records the app from its first event.
/// </summary>
internal sealed record PerfEtwScope(int? ProcessId = null, string? ExecutableName = null, string? PackageFullName = null)
{
    public bool IsPrivate => ProcessId is not null;
    public bool IsEmpty => ProcessId is null && string.IsNullOrWhiteSpace(PackageFullName ?? ExecutableName);
}

/// <summary>An ETW file logger for one capture. It never changes ETW access permissions.</summary>
internal sealed unsafe class PrivateEtwSession : IDisposable
{
    private const uint PidFilterType = 0x80000004;
    private const uint ExecutableNameFilterType = 0x80000008;
    private const uint PackageIdFilterType = 0x80000010;
    private const uint PrivateLoggerMode = 0x800;
    private const uint SequentialFileMode = 1;
    private const int StringBytes = 2048;
    private EVENT_TRACE_PROPERTIES_V2* properties;
    private EVENT_FILTER_DESCRIPTOR* filter;
    private void* filterData;
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
        : this(name, sessionId, new PerfEtwScope(ProcessId: processId), outputPath, maximumSizeMiB, api)
    {
    }

    public PrivateEtwSession(string name, Guid sessionId, PerfEtwScope scope, string outputPath, int maximumSizeMiB,
        IPrivateEtwApi? api = null)
    {
        if (scope.IsEmpty)
        {
            throw new ArgumentException("An ETW capture requires a process ID, executable name, or package full name.", nameof(scope));
        }
        if (scope.ProcessId is { } processId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        }
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
            filter = CreateScopeFilter(scope, out filterData);
            properties->Wnode.BufferSize = (uint)(sizeof(EVENT_TRACE_PROPERTIES_V2) + 2 * StringBytes);
            properties->Wnode.Guid = sessionId;
            properties->Wnode.ClientContext = 1;
            properties->Wnode.Flags = 0x00020000 | 0x00800000;
            properties->BufferSize = 64;
            properties->MinimumBuffers = 32;
            properties->MaximumBuffers = 256;
            properties->MaximumFileSize = (uint)maximumSizeMiB;
            properties->LogFileMode = scope.IsPrivate ? PrivateLoggerMode | SequentialFileMode : SequentialFileMode;
            properties->FlushTimer = 1;
            properties->Anonymous2.V2Control = 2;
            if (scope.IsPrivate)
            {
                // A private logger runs inside the target process, so its scope is part of the session.
                properties->FilterDescCount = 1;
                properties->FilterDesc = filter;
            }
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

    private static EVENT_FILTER_DESCRIPTOR* CreateScopeFilter(PerfEtwScope scope, out void* data)
    {
        data = null;
        uint size;
        uint type;
        if (scope.ProcessId is { } processId)
        {
            data = NativeMemory.Alloc(sizeof(uint));
            *(uint*)data = (uint)processId;
            size = sizeof(uint);
            type = PidFilterType;
        }
        else if ((scope.PackageFullName ?? scope.ExecutableName) is { Length: > 0 } value)
        {
            // Scope filter strings are null-terminated UTF-16 and limited to 1024 bytes.
            size = checked((uint)(2 * (value.Length + 1)));
            if (size > 1024)
            {
                throw new ArgumentException("The executable or package name is too long for an ETW scope filter.");
            }
            data = NativeMemory.AllocZeroed(size);
            value.AsSpan().CopyTo(new Span<char>(data, value.Length));
            type = scope.PackageFullName is not null ? PackageIdFilterType : ExecutableNameFilterType;
        }
        else
        {
            throw new ArgumentException("An ETW capture requires a process ID, executable name, or package full name.", nameof(scope));
        }
        var descriptor = (EVENT_FILTER_DESCRIPTOR*)NativeMemory.AllocZeroed((nuint)sizeof(EVENT_FILTER_DESCRIPTOR));
        *descriptor = new() { Ptr = (ulong)data, Size = size, Type = type };
        return descriptor;
    }

    public bool Enable(Guid provider, ulong keywords, byte level = 5, ushort[]? eventIds = null)
    {
        ObjectDisposedException.ThrowIf(properties == null || !active, this);
        if (!CanEnable)
        {
            throw new PerfEtwTargetNotReadyException();
        }
        var scopeCount = filter is null ? 0u : 1u;
        var descriptors = stackalloc EVENT_FILTER_DESCRIPTOR[2];
        if (filter is not null)
        {
            descriptors[0] = *filter;
        }
        var parameters = new ENABLE_TRACE_PARAMETERS
        {
            Version = 2,
            EnableFilterDesc = scopeCount == 0 ? null : descriptors,
            FilterDescCount = scopeCount,
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
            descriptors[scopeCount] = new()
            {
                Ptr = (ulong)idFilter,
                Size = (uint)(4 + 2 * eventIds.Length),
                Type = 0x80000200,
            };
            parameters.EnableFilterDesc = descriptors;
            parameters.FilterDescCount = scopeCount + 1;
            var filtered = api.Enable(handle, &provider, level, keywords, &parameters);
            if ((uint)filtered != 87)
            {
                Check(filtered, $"EnableTraceEx2({provider}, event IDs)");
                return true;
            }
            parameters.EnableFilterDesc = scopeCount == 0 ? null : descriptors;
            parameters.FilterDescCount = scopeCount;
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

    /// <summary>
    /// Stops a capture's session after its worker is gone. Pass the target PID for a private session,
    /// or null for an elevated system session (which only an elevated caller can stop).
    /// </summary>
    public static bool StopOwned(string name, Guid sessionId, int? processId, string outputPath)
    {
        var size = sizeof(EVENT_TRACE_PROPERTIES_V2) + 2 * StringBytes;
        var query = (EVENT_TRACE_PROPERTIES_V2*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            uint pid = checked((uint)(processId ?? 0));
            var filter = new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)&pid, Size = sizeof(uint), Type = PidFilterType };
            query->Wnode.BufferSize = (uint)size;
            query->Wnode.Guid = sessionId;
            query->Wnode.Flags = 0x00020000 | 0x00800000;
            query->LogFileMode = processId is null ? SequentialFileMode : PrivateLoggerMode | SequentialFileMode;
            query->Anonymous2.V2Control = 2;
            if (processId is not null)
            {
                query->FilterDesc = &filter;
                query->FilterDescCount = 1;
            }
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
        NativeMemory.Free(filterData);
        properties = null;
        filter = null;
        filterData = null;
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (Win32Exception)
        {
            // The failure was already reported by the explicit Stop; disposal must still release memory.
        }
        finally
        {
            Free();
        }
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
