// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices.ComTypes;

// The CLR requires this type and entry point in the global namespace.
internal sealed class StartupHook
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    public static void Initialize() => _ = Task.Run(ServeAsync);

    private static async Task ServeAsync()
    {
        string pipeName = $"winapp-devtools-binding-{Environment.ProcessId}";
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                var connected = pipe;
                pipe = null;
                _ = Task.Run(async () =>
                {
                    using (connected)
                    {
                        try { await HandleAsync(connected); }
                        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Binding connection failed: {0}", ex); }
                    }
                });
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                System.Diagnostics.Trace.TraceError("Binding listener failed: {0}", ex);
                await Task.Delay(200);
            }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream stream)
    {
        if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint processId))
        {
            System.Diagnostics.Trace.TraceError("Binding client identity query failed: {0}", Marshal.GetLastWin32Error());
            return;
        }
        if (processId != (uint)Environment.ProcessId)
        {
            System.Diagnostics.Trace.TraceWarning("Binding connection refused: client is not in this process.");
            return;
        }
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        while (ReadLine(stream) is string command)
        {
            if (command != "BINDING2")
            {
                await writer.WriteLineAsync("BINDING2 {\"state\":\"unavailable\",\"reason\":\"incompatible binding protocol\"}");
                return;
            }
            string result;
            try
            {
                byte[] packet = ReadField(reader, allowEmpty: false);
                string op = ReadString(reader);
                string property = ReadString(reader);
                string authored = ReadString(reader);
                if (op is not ("diagnose" or "capture" or "restore" or "restoreconfirmed" or "clear" or "writesource"))
                    throw new InvalidDataException("unsupported binding operation");
                using var apartment = new WinApp.DevTools.Managed.BindingTarget.Apartment();
                result = WinApp.DevTools.Managed.BindingDiagnosis.Run(WinApp.DevTools.Managed.BindingTarget.Consume(packet), op, property, authored);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or DecoderFallbackException)
            {
                System.Diagnostics.Trace.TraceWarning("Invalid binding request: {0}", ex);
                await writer.WriteLineAsync("BINDING2 {\"state\":\"unavailable\",\"reason\":\"invalid binding frame\"}");
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Binding request failed: {0}", ex);
                result = "{\"state\":\"unavailable\",\"reason\":\"" + ex.GetType().Name + "\"}";
            }
            await writer.WriteLineAsync("BINDING2 " + result.Replace("\r", "").Replace("\n", " "));
        }
    }

    private static string? ReadLine(Stream stream)
    {
        var result = new StringBuilder();
        int next;
        while ((next = stream.ReadByte()) != -1)
        {
            if (next == '\n') return result.ToString();
            if (result.Length >= 16) throw new InvalidDataException("binding command too long");
            result.Append((char)next);
        }
        if (result.Length != 0) throw new EndOfStreamException("truncated binding command");
        return null;
    }

    private static string ReadString(BinaryReader reader)
    {
        return new UTF8Encoding(false, true).GetString(ReadField(reader, allowEmpty: true));
    }

    private static byte[] ReadField(BinaryReader reader, bool allowEmpty)
    {
        int length = reader.ReadInt32();
        if (length < (allowEmpty ? 0 : 1) || length > 65536) throw new InvalidDataException("binding field length");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("truncated binding field");
        return bytes;
    }
}

namespace WinApp.DevTools.Managed
{
    // Owns only a platform agile reference; resolved ABI pointers never escape their calling apartment.
    internal sealed class BindingTarget : SafeHandleZeroOrMinusOneIsInvalid
    {
        private static readonly Guid Inspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, bool deleteOnRelease, out IStream stream);
        [DllImport("ole32.dll")] private static extern int CoUnmarshalInterface(IStream stream, in Guid iid, out IntPtr value);
        [DllImport("combase.dll")] private static extern int RoGetAgileReference(uint options, in Guid iid, IntPtr value, out BindingTarget reference);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResolveReference(IntPtr self, in Guid iid, out IntPtr value);

        internal sealed class Apartment : IDisposable
        {
            internal Apartment() => Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            public void Dispose() => CoUninitialize();
        }

        public BindingTarget() : base(true) { }
        protected override bool ReleaseHandle() { Marshal.Release(handle); return true; }

        internal static BindingTarget Consume(byte[] packet)
        {
            if (packet.Length is < 1 or > 65536) throw new InvalidDataException("binding packet length");
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
            try
            {
                stream.Write(packet, packet.Length, IntPtr.Zero);
                stream.Seek(0, 0, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(CoUnmarshalInterface(stream, in Inspectable, out var value));
                try
                {
                    Marshal.ThrowExceptionForHR(RoGetAgileReference(0, in Inspectable, value, out var owner));
                    return owner;
                }
                finally { if (value != IntPtr.Zero) Marshal.Release(value); }
            }
            finally { Marshal.FinalReleaseComObject(stream); }
        }

        internal object? Project()
        {
            bool held = false;
            try
            {
                DangerousAddRef(ref held);
                var resolve = Marshal.GetDelegateForFunctionPointer<ResolveReference>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(handle), 3 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(resolve(handle, in Inspectable, out var value));
                try { return WinRT.MarshalInspectable<object>.FromAbi(value); }
                finally { if (value != IntPtr.Zero) Marshal.Release(value); }
            }
            finally { if (held) DangerousRelease(); }
        }
    }
}
