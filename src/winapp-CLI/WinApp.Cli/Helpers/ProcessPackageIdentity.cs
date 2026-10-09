// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Helpers;

internal static partial class ProcessPackageIdentity
{
    internal static string? TryGetPackageFamilyName(IntPtr processHandle)
        => ReadIdentity(processHandle, GetPackageFamilyName);

    internal static string? TryGetApplicationUserModelId(IntPtr processHandle)
        => ReadIdentity(processHandle, GetApplicationUserModelId);

    private delegate int IdentityReader(IntPtr process, ref uint length, char[]? value);

    private static string? ReadIdentity(IntPtr processHandle, IdentityReader read)
    {
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        uint length = 0;
        if (read(processHandle, ref length, null) != 122 || length is 0 or > 1024)
        {
            return null;
        }

        var buffer = new char[length];
        if (read(processHandle, ref length, buffer) != 0
            || length <= 1 || length > buffer.Length || buffer[length - 1] != '\0')
        {
            return null;
        }

        return new string(buffer, 0, (int)length - 1);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackageFamilyName(
        IntPtr process, ref uint length, [Out] char[]? packageFamilyName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetApplicationUserModelId(
        IntPtr process, ref uint length, [Out] char[]? applicationUserModelId);
}
