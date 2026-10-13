// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

/// <summary>
/// Writes self-describing TraceLogging events as the Microsoft-Windows-XAML provider through native ETW.
/// A managed EventSource is not used because .NET only enables it for sessions that EnumerateTraceGuidsEx
/// reports, and a process-private session is not always reported.
/// </summary>
internal sealed unsafe partial class XamlTraceLoggingWriter : IDisposable
{
    private const byte TraceLoggingChannel = 11;
    private const byte InUnicodeString = 1;
    private const byte InInt32 = 7;
    private const byte InUInt64 = 10;
    private const byte InStruct = 24;
    private const byte Chain = 0x80;
    private const uint ProviderMetadataType = 2;
    private const uint EventMetadataType = 1;

    private static readonly byte[] ProviderMetadata = Metadata(w => w.String8("Microsoft-Windows-XAML"));
    private static readonly byte[] PublicApiCallMetadata = Metadata(w => w
        .Tags().String8("PublicApiCall")
        .Field("IsStart", InInt32).Field("MethodName", InUnicodeString).Field("ObjectPointer", InUInt64));
    private static readonly byte[] FallbackErrorMetadata = Metadata(w => w
        .Tags().String8("FallbackError")
        .Field("Failure", InStruct, fieldCount: 2).Field("Code", InInt32).Field("Message", InUnicodeString));

    private ulong handle;

    public XamlTraceLoggingWriter()
    {
        var provider = PerfProviders.Xaml;
        Check(EventRegister(&provider, 0, 0, out handle));
    }

    public void PublicApiCall(int isStart, string methodName, ulong objectPointer) =>
        Write(4, PublicApiCallMetadata, new Payload().Int32(isStart).String16(methodName).UInt64(objectPointer));

    public void FallbackError(int code, string message) =>
        Write(2, FallbackErrorMetadata, new Payload().Int32(code).String16(message));

    public void Dispose()
    {
        if (handle != 0)
        {
            _ = EventUnregister(handle);
            handle = 0;
        }
    }

    private void Write(byte level, byte[] eventMetadata, Payload payload)
    {
        var data = payload.ToArray();
        var descriptor = new EventDescriptor { Channel = TraceLoggingChannel, Level = level };
        fixed (byte* provider = ProviderMetadata, metadata = eventMetadata, values = data)
        {
            var descriptors = stackalloc DataDescriptor[3];
            descriptors[0] = new() { Ptr = (ulong)provider, Size = (uint)ProviderMetadata.Length, Type = ProviderMetadataType };
            descriptors[1] = new() { Ptr = (ulong)metadata, Size = (uint)eventMetadata.Length, Type = EventMetadataType };
            descriptors[2] = new() { Ptr = (ulong)values, Size = (uint)data.Length };
            Check(EventWrite(handle, &descriptor, 3, descriptors));
        }
    }

    private static byte[] Metadata(Func<Payload, Payload> build)
    {
        var bytes = build(new Payload().UInt16(0)).ToArray();
        // TraceLogging metadata starts with its total size, including the size field.
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), checked((ushort)bytes.Length));
        return bytes;
    }

    private static void Check(uint status)
    {
        if (status != 0)
        {
            throw new Win32Exception((int)status);
        }
    }

    private sealed class Payload
    {
        private readonly List<byte> bytes = [];

        public Payload Tags() => Byte(0);
        public Payload Byte(byte value) { bytes.Add(value); return this; }
        public Payload UInt16(ushort value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload Int32(int value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload UInt64(ulong value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload String8(string value) { bytes.AddRange(Encoding.UTF8.GetBytes(value)); return Byte(0); }
        public Payload String16(string value) { bytes.AddRange(Encoding.Unicode.GetBytes(value)); return UInt16(0); }

        // A struct field chains its out-type byte, which carries the number of fields that follow.
        public Payload Field(string name, byte inType, byte fieldCount = 0) =>
            fieldCount == 0 ? String8(name).Byte(inType) : String8(name).Byte((byte)(inType | Chain)).Byte(fieldCount);

        public byte[] ToArray() => [.. bytes];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint EventRegister(Guid* providerId, nint callback, nint context, out ulong handle);

    [LibraryImport("advapi32.dll")]
    private static partial uint EventUnregister(ulong handle);

    [LibraryImport("advapi32.dll")]
    private static partial uint EventWrite(ulong handle, EventDescriptor* descriptor, uint count, DataDescriptor* data);
}
