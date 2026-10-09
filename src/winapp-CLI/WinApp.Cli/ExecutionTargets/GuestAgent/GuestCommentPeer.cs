// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot authenticate the comment peer.");
        }
        using var heldToken = token;
        if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Only the matching guest winapp can use the comment relay.");
        }
        using var owner = WindowsIdentity.GetCurrent();
        var integrity = Integrity(owner.AccessToken);
        using var peer = new WindowsIdentity(token.DangerousGetHandle());
        if (owner.User != peer.User || Integrity(token) != integrity)
        {
            throw new UnauthorizedAccessException("The comment peer must have the relay owner's identity and integrity.");
        }
    }

    private static unsafe uint Integrity(SafeAccessTokenHandle token)
    {
        const int TokenIntegrityLevel = 25;
        _ = GetTokenInformation(token, TokenIntegrityLevel, null, 0, out var length);
        if (length is < 16 or > 4096)
        {
            throw new UnauthorizedAccessException("Cannot establish comment client integrity.");
        }
        var buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, pointer, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var sid = *(byte**)pointer;
            if (sid < pointer || sid + 8 > pointer + length || sid[1] == 0 ||
                sid + 8 + sid[1] * sizeof(uint) > pointer + length)
            {
                throw new UnauthorizedAccessException("Invalid comment client integrity label.");
            }
            return *(uint*)(sid + 8 + (sid[1] - 1) * sizeof(uint));
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, void* information, uint length, out uint returned);
}
