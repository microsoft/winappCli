// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinApp.Cli.Services.DevTools;

internal static partial class DevToolsPipeClient
{
    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint SecurityIdentification = 0x00110000;
    private const int ErrorAccessDenied = 5;

    public static NamedPipeClientStream Connect(
        uint pipePid,
        uint expectedServerPid,
        int timeoutMs,
        bool asynchronous,
        CancellationToken cancellationToken = default)
    {
        var path = $@"\\.\pipe\winapp-devtools-{pipePid}";
        var handle = DevToolsConnectRetry.Run<SafePipeHandle>(
            (int remainingMs, out int error) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TryConnectOnce(path, Math.Clamp(remainingMs, 1, 50), asynchronous, out error);
            },
            timeoutMs,
            error => ConnectError(path, error),
            sleep: delay =>
            {
                if (cancellationToken.WaitHandle.WaitOne(delay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });

        if (!GetNamedPipeServerProcessId(handle, out var serverPid))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"Could not verify the DevTools pipe server for process {expectedServerPid}.",
                new Win32Exception(error));
        }

        if (serverPid != expectedServerPid)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException(
                $"Refused DevTools pipe '{path}': its server is process {serverPid}, not target process {expectedServerPid}.");
        }

        return new NamedPipeClientStream(
            PipeDirection.InOut,
            asynchronous,
            isConnected: true,
            handle);
    }

    private static SafePipeHandle? TryConnectOnce(string path, int waitMs, bool asynchronous, out int error)
    {
        if (!WaitNamedPipeW(path, checked((uint)waitMs)))
        {
            error = Marshal.GetLastPInvokeError();
            if (error == 121)
            {
                // A short WaitNamedPipe slice timed out while an existing instance was busy.
                error = DevToolsConnectRetry.ErrorPipeBusy;
            }
            return null;
        }

        var handle = CreateFileW(
            path,
            FileReadData | FileWriteData | FileReadAttributes | Synchronize,
            0,
            0,
            OpenExisting,
            SecurityIdentification | (asynchronous ? FileFlagOverlapped : 0),
            0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = 0;
        return handle;
    }

    private static Exception ConnectError(string path, int error) => error == ErrorAccessDenied
        ? new UnauthorizedAccessException(
            $"Access to DevTools pipe '{path}' was denied. The client must run as the target owner " +
            "at the same or a higher Windows integrity level.")
        : new IOException($"Could not connect to DevTools pipe '{path}'.", new Win32Exception(error));

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipeW(string name, uint timeout);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
