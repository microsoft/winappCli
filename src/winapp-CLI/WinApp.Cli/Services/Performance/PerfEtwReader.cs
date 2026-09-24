// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.Etw;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfRawEvent(
    long Qpc, uint ProcessId, uint ThreadId, Guid Provider, ushort EventId, byte Version,
    byte Opcode, Guid ActivityId, int PointerSize, bool TraceLogging, byte[] Payload,
    string? EventName, Dictionary<string, string>? Fields, string? DecodeError);

/// <summary>Streams ETL records through Windows' native decoder without reflection or TraceEvent.</summary>
internal static unsafe class PerfEtwReader
{
    private sealed class Context(Action<PerfRawEvent> receive, HashSet<Guid> providers, uint pid, CancellationToken token)
    {
        public Action<PerfRawEvent> Receive { get; } = receive;
        public HashSet<Guid> Providers { get; } = providers;
        public uint Pid { get; } = pid;
        public CancellationToken Token { get; } = token;
        public Exception? Failure { get; set; }
        public long Records { get; set; }
        public char[] FormattingBuffer { get; } = new char[32768];
    }

    public static (long Frequency, uint EventsLost) Read(
        string path, int processId, IEnumerable<Guid> providers, Action<PerfRawEvent> receive,
        CancellationToken cancellationToken = default)
    {
        var context = new Context(receive, providers.ToHashSet(), checked((uint)processId), cancellationToken);
        var root = GCHandle.Alloc(context);
        try
        {
            fixed (char* fileName = path)
            {
                var file = new EVENT_TRACE_LOGFILEW
                {
                    LogFileName = fileName,
                    Context = (void*)GCHandle.ToIntPtr(root),
                    BufferCallback = &OnBuffer,
                };
                file.Anonymous1.ProcessTraceMode = 0x10000000 | 0x1000;
                file.Anonymous2.EventRecordCallback = &OnEvent;
                var trace = PInvoke.OpenTrace(&file);
                if (trace.Value == ulong.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"OpenTrace failed for '{path}'.");
                }
                try
                {
                    if ((file.LogfileHeader.ReservedFlags & 3) != 1)
                    {
                        throw new InvalidDataException("The ETL does not use the QPC clock required by this capture format.");
                    }
                    var result = PInvoke.ProcessTrace(&trace, 1, null, null);
                    if (context.Failure is { } failure)
                    {
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    PrivateEtwSession.Check(result, "ProcessTrace");
                    return (file.LogfileHeader.PerfFreq, file.LogfileHeader.Anonymous2.Anonymous.EventsLost);
                }
                finally
                {
                    PrivateEtwSession.Check(PInvoke.CloseTrace(trace), "CloseTrace");
                }
            }
        }
        finally
        {
            root.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnBuffer(EVENT_TRACE_LOGFILEW* file)
    {
        var context = (Context)GCHandle.FromIntPtr((nint)file->Context).Target!;
        return context.Failure is null && !context.Token.IsCancellationRequested ? 1u : 0u;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(EVENT_RECORD* record)
    {
        var context = (Context)GCHandle.FromIntPtr((nint)record->UserContext).Target!;
        if (context.Failure is not null || context.Token.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (record->EventHeader.ProcessId != context.Pid ||
                !context.Providers.Contains(record->EventHeader.ProviderId))
            {
                return;
            }
            if (++context.Records > 5_000_000)
            {
                throw new InvalidDataException("Analysis reached the five-million target-record limit.");
            }
            var traceLogging = false;
            for (var i = 0; i < record->ExtendedDataCount; i++)
            {
                traceLogging |= record->ExtendedData[i].ExtType == 11;
            }
            var pointerSize = (record->EventHeader.Flags & 0x20) != 0 ? 4 :
                (record->EventHeader.Flags & 0x40) != 0 ? 8 : 0;
            string? name = null;
            Dictionary<string, string>? fields = null;
            string? error = null;
            if (traceLogging)
            {
                try
                {
                    fields = DecodeTraceLogging(record, pointerSize, context.FormattingBuffer, out name);
                }
                catch (InvalidDataException ex)
                {
                    error = ex.Message;
                }
            }
            var descriptor = record->EventHeader.EventDescriptor;
            context.Receive(new(
                record->EventHeader.TimeStamp, record->EventHeader.ProcessId, record->EventHeader.ThreadId,
                record->EventHeader.ProviderId, descriptor.Id, descriptor.Version, descriptor.Opcode,
                record->EventHeader.ActivityId, pointerSize, traceLogging,
                new ReadOnlySpan<byte>(record->UserData, record->UserDataLength).ToArray(), name, fields, error));
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind across the unmanaged ProcessTrace callback.
            context.Failure = ex;
        }
    }

    private static Dictionary<string, string> DecodeTraceLogging(EVENT_RECORD* record, int pointerSize, char[] output,
        out string? name)
    {
        name = null;
        uint size = 0;
        var status = PInvoke.TdhGetEventInformation(record, 0, null, null, &size);
        if (status != 122 || size < sizeof(TRACE_EVENT_INFO) || size > 1_048_576)
        {
            throw new InvalidDataException($"TDH metadata unavailable or oversized ({status}).");
        }
        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            var info = (TRACE_EVENT_INFO*)buffer;
            var capacity = size;
            status = PInvoke.TdhGetEventInformation(record, 0, null, info, &size);
            if (status != 0 || size > capacity || info->DecodingSource != DECODING_SOURCE.DecodingSourceTlg)
            {
                throw new InvalidDataException($"TDH self-describing schema unavailable ({status}).");
            }
            name = ReadString(buffer, size, info->Anonymous1.EventNameOffset);
            if (info->TopLevelPropertyCount > 128 || info->PropertyCount > 256 ||
                TRACE_EVENT_INFO.SizeOf((int)info->PropertyCount) > size)
            {
                throw new InvalidDataException("TDH property table exceeds its metadata buffer.");
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var properties = info->EventPropertyInfoArray.AsSpan((int)info->PropertyCount);
            var consumed = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < info->TopLevelPropertyCount; i++)
            {
                var propertyName = ReadString(buffer, size, properties[i].NameOffset);
                if (!names.Add(propertyName))
                {
                    duplicates.Add(propertyName);
                }
            }
            for (var i = 0; i < info->TopLevelPropertyCount; i++)
            {
                var property = properties[i];
                // Nested/custom/variable arrays need their own verified decoder, never guessed offsets.
                if (((uint)property.Flags & (1 | 2 | 4 | 128)) != 0 || property.Anonymous2.count > 64)
                {
                    throw new InvalidDataException("Unsupported structured or variable-length TDH property.");
                }
                var values = new List<string>();
                for (var j = 0; j < Math.Max(1, (int)property.Anonymous2.count); j++)
                {
                    var type = property.Anonymous1.nonStructType;
                    if (type.InType == 16 && pointerSize == 0)
                    {
                        throw new InvalidDataException("Event pointer width is unavailable.");
                    }
                    uint outputBytes = (uint)(output.Length * sizeof(char));
                    ushort used;
                    fixed (char* text = output)
                    {
                        status = PInvoke.TdhFormatProperty(info, null, (uint)pointerSize,
                            type.InType, type.OutType, property.Anonymous3.length,
                            checked((ushort)(record->UserDataLength - consumed)),
                            (byte*)record->UserData + consumed, &outputBytes, text, &used);
                    }
                    if (status != 0 || used > record->UserDataLength - consumed)
                    {
                        throw new InvalidDataException($"TDH payload formatting failed ({status}).");
                    }
                    consumed += used;
                    var chars = checked((int)outputBytes / sizeof(char));
                    if (chars > output.Length)
                    {
                        throw new InvalidDataException("TDH formatted value exceeds its output buffer.");
                    }
                    values.Add(new string(output, 0, chars).TrimEnd('\0'));
                }
                var key = ReadString(buffer, size, property.NameOffset);
                if (duplicates.Contains(key))
                {
                    var indexed = $"{key}[property:{i}]";
                    while (names.Contains(indexed) || fields.ContainsKey(indexed))
                    {
                        indexed += "#";
                    }
                    key = indexed;
                }
                fields.Add(key, string.Join(", ", values));
            }
            if (consumed != record->UserDataLength)
            {
                throw new InvalidDataException("TDH schema did not consume the complete event payload.");
            }
            return fields;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static string ReadString(byte* buffer, uint size, uint offset)
    {
        if (offset == 0 || offset >= size || offset % 2 != 0)
        {
            throw new InvalidDataException("Invalid TDH string offset.");
        }
        var text = new ReadOnlySpan<char>(buffer + offset, (int)(size - offset) / sizeof(char));
        var end = text.IndexOf('\0');
        if (end < 0)
        {
            throw new InvalidDataException("Unterminated TDH string.");
        }
        return new string(text[..end]);
    }
}
