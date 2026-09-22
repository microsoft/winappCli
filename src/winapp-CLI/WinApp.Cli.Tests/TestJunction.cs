// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

internal static partial class TestJunction
{
    public static unsafe void Create(string linkPath, string targetPath)
    {
        linkPath = Path.GetFullPath(linkPath);
        targetPath = Path.GetFullPath(targetPath);
        if (Path.Exists(linkPath))
        {
            throw new IOException($"Junction path already exists: '{linkPath}'.");
        }
        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException(targetPath);
        }

        var substituteName = targetPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\??\" + targetPath[4..]
            : targetPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\??\UNC\" + targetPath[2..]
                : @"\??\" + targetPath;
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var printName = Encoding.Unicode.GetBytes(targetPath);
        var buffer = new byte[16 + substitute.Length + 2 + printName.Length + 2];

        // Mount-point reparse data uses byte offsets into the two null-terminated UTF-16 names.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printName.Length));
        substitute.CopyTo(buffer, 16);
        printName.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(linkPath);
        using var handle = OpenDirectory(
            LongPathHelper.EnsureExtendedLengthPrefix(linkPath), 0x40000000, 0, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open junction directory '{linkPath}'.");
        }
        fixed (byte* data = buffer)
        {
            if (SetReparsePoint(handle, 0x000900A4, data, (uint)buffer.Length, 0, 0, out _, 0) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot create junction '{linkPath}'.");
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenDirectory(
        string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int SetReparsePoint(
        SafeFileHandle handle, uint code, byte* input, uint inputSize,
        nint output, uint outputSize, out uint bytesReturned, nint overlapped);
}
