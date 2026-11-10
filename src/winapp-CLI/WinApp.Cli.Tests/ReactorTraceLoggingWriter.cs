// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

/// <summary>
/// Writes Microsoft-UI-Reactor-Perf TraceLogging events through native ETW with the event IDs, opcodes,
/// levels, keywords and field metadata of Reactor's published contract (microsoft-ui-reactor
/// docs/reference/perf-trace-events.md), so tests can record and analyze a real ETL without a Reactor app.
/// </summary>
internal sealed unsafe partial class ReactorTraceLoggingWriter : IDisposable
{
    private const byte TraceLoggingChannel = 11;
    private const byte InUnicodeString = 1;
    private const byte InUInt32 = 8;
    private const byte InUInt64 = 10;
    private const byte InBool32 = 13;
    private const ulong Pass = 0x1;
    private const ulong Component = 0x2;
    private const ulong Children = 0x4;

    private static readonly byte[] ProviderMetadata = Metadata(w => w.String8("Microsoft-UI-Reactor-Perf"));
    private static readonly byte[] RenderPassMetadata = Event("RenderPass", ("hostId", InUInt64));
    private static readonly byte[] ComponentRenderStartMetadata = Event("ComponentRender",
        ("componentId", InUInt64), ("name", InUnicodeString), ("reason", InUnicodeString));
    private static readonly byte[] ComponentRenderStopMetadata = Event("ComponentRender", ("componentId", InUInt64));
    private static readonly byte[] ReconcileStartMetadata = Event("Reconcile", ("entry", InUnicodeString), ("element", InUnicodeString));
    private static readonly byte[] ReconcileStopMetadata = Event("Reconcile");
    private static readonly byte[] EffectsMetadata = Event("Effects", ("componentId", InUInt64));
    private static readonly byte[] ChildReconcileStartMetadata = Event("ChildReconcile", ("oldCount", InUInt32), ("newCount", InUInt32));
    private static readonly byte[] KeyedListDiagnosticMetadata = Event("KeyedListDiagnostic", ("kind", InUnicodeString), ("bailedOut", InBool32));

    private ulong handle;

    public ReactorTraceLoggingWriter()
    {
        var provider = PerfProviders.Reactor;
        Check(EventRegister(&provider, 0, 0, out handle));
    }

    public void RenderPassStart(ulong hostId) => Write(1, 4, 1, Pass, RenderPassMetadata, new Payload().UInt64(hostId));
    public void RenderPassStop(ulong hostId) => Write(2, 4, 2, Pass, RenderPassMetadata, new Payload().UInt64(hostId));

    public void ComponentRenderStart(ulong componentId, string name, string reason) =>
        Write(3, 4, 1, Component, ComponentRenderStartMetadata, new Payload().UInt64(componentId).String16(name).String16(reason));

    public void ComponentRenderStop(ulong componentId) =>
        Write(4, 4, 2, Component, ComponentRenderStopMetadata, new Payload().UInt64(componentId));

    public void ReconcileStart(string entry, string element) =>
        Write(5, 4, 1, Pass, ReconcileStartMetadata, new Payload().String16(entry).String16(element));

    public void ReconcileStop() => Write(6, 4, 2, Pass, ReconcileStopMetadata, new Payload());
    public void EffectsStart(ulong componentId) => Write(7, 4, 1, Pass, EffectsMetadata, new Payload().UInt64(componentId));
    public void EffectsStop(ulong componentId) => Write(8, 4, 2, Pass, EffectsMetadata, new Payload().UInt64(componentId));

    /// <summary>Verbose (level 5, Children keyword): not recorded by winapp's default keywords.</summary>
    public void ChildReconcileStart(uint oldCount, uint newCount) =>
        Write(9, 5, 1, Children, ChildReconcileStartMetadata, new Payload().UInt32(oldCount).UInt32(newCount));

    public void KeyedListDiagnostic(string kind, bool bailedOut) =>
        Write(12, 5, 0, Children, KeyedListDiagnosticMetadata, new Payload().String16(kind).UInt32(bailedOut ? 1u : 0u));

    public void Dispose()
    {
        if (handle != 0)
        {
            _ = EventUnregister(handle);
            handle = 0;
        }
    }

    private void Write(ushort id, byte level, byte opcode, ulong keyword, byte[] eventMetadata, Payload payload)
    {
        var data = payload.ToArray();
        var descriptor = new EventDescriptor { Id = id, Channel = TraceLoggingChannel, Level = level, Opcode = opcode, Keyword = keyword };
        fixed (byte* provider = ProviderMetadata, metadata = eventMetadata, values = data)
        {
            var descriptors = stackalloc DataDescriptor[3];
            descriptors[0] = new() { Ptr = (ulong)provider, Size = (uint)ProviderMetadata.Length, Type = 2 };
            descriptors[1] = new() { Ptr = (ulong)metadata, Size = (uint)eventMetadata.Length, Type = 1 };
            descriptors[2] = new() { Ptr = (ulong)values, Size = (uint)data.Length };
            Check(EventWrite(handle, &descriptor, data.Length == 0 ? 2u : 3u, descriptors));
        }
    }

    private static byte[] Event(string name, params (string Name, byte InType)[] fields) => Metadata(w =>
    {
        w.Byte(0).String8(name);
        foreach (var (fieldName, inType) in fields)
        {
            w.String8(fieldName).Byte(inType);
        }
        return w;
    });

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

        public Payload Byte(byte value) { bytes.Add(value); return this; }
        public Payload UInt16(ushort value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload UInt32(uint value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload UInt64(ulong value) { bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        public Payload String8(string value) { bytes.AddRange(Encoding.UTF8.GetBytes(value)); return Byte(0); }
        public Payload String16(string value) { bytes.AddRange(Encoding.Unicode.GetBytes(value)); return UInt16(0); }
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
