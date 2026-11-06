// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinApp.Cli.Helpers;

/// <summary>Whether another process runs as this process's user at this process's integrity level.</summary>
internal static partial class ProcessTrust
{
    /// <summary>
    /// True when <paramref name="process"/> has this process's user and integrity level. False, with the reason,
    /// for another user, an elevated (or lower) process, or a process whose token cannot be read.
    /// </summary>
    internal static bool IsSameUserAndIntegrity(Process process, out string? reason)
    {
        reason = null;
        try
        {
            if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token))
            {
                reason = $"its access token can't be read (error {Marshal.GetLastWin32Error()})";
                return false;
            }
            using var held = token;
            using var owner = WindowsIdentity.GetCurrent();
            using var peer = new WindowsIdentity(token.DangerousGetHandle());
            if (owner.User != peer.User)
            {
                reason = "it runs as a different user";
                return false;
            }
            if (Integrity(token) != Integrity(owner.AccessToken))
            {
                reason = "it runs at a different integrity level (for example elevated)";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            reason = "its identity can't be verified";
            return false;
        }
    }

    /// <summary>The mandatory integrity level RID of <paramref name="token"/>.</summary>
    internal static unsafe uint Integrity(SafeAccessTokenHandle token)
    {
        const int TokenIntegrityLevel = 25;
        _ = GetTokenInformation(token, TokenIntegrityLevel, null, 0, out var length);
        if (length is < 16 or > 4096)
        {
            throw new UnauthorizedAccessException("Cannot establish the process integrity level.");
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
                throw new UnauthorizedAccessException("Invalid integrity label.");
            }
            return *(uint*)(sid + 8 + (sid[1] - 1) * sizeof(uint));
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, void* information, uint length, out uint returned);
}
