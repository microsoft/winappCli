// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

internal static partial class GuestCommentPeer
{
    internal static void Verify(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The comment relay could not identify its client.");
        }
        VerifyProcess(pid);
    }

    internal static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The host comment relay owner is unavailable.");
        }
        VerifyProcess(pid);
    }

    private static void VerifyProcess(uint pid)
    {
        using var process = Process.GetProcessById(checked((int)pid));
        if (!ProcessTrust.OpenProcessToken(process.SafeHandle, 0x0008, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot authenticate the comment peer.");
        }
        using var heldToken = token;
        if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Only the matching guest winapp can use the comment relay.");
        }
        using var owner = WindowsIdentity.GetCurrent();
        var integrity = ProcessTrust.Integrity(owner.AccessToken);
        using var peer = new WindowsIdentity(token.DangerousGetHandle());
        if (owner.User != peer.User || ProcessTrust.Integrity(token) != integrity)
        {
            throw new UnauthorizedAccessException("The comment peer must have the relay owner's identity and integrity.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
