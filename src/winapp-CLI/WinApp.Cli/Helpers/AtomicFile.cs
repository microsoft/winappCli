// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinApp.Cli.Helpers;

/// <summary>
/// File writes that publish their result atomically: content is first written to a uniquely-named
/// temporary file in the destination directory, then moved into place with a single rename. A
/// concurrent reader (e.g. a second <c>winapp run --debug-output</c>, or the same run re-resolving
/// the debugger cache) therefore only ever observes the final path as either absent or fully written
/// — never partially written or not-yet-verified.
/// </summary>
internal static partial class AtomicFile
{
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="destinationPath"/> atomically.</summary>
    public static async Task WriteAllBytesAsync(string destinationPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteLeftoverTemp(tempPath);
        }
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="destinationPath"/> atomically.</summary>
    /// <param name="destinationPath">The final file path.</param>
    /// <param name="content">The complete new contents.</param>
    /// <param name="preserveReaders">
    /// Uses a single Windows rename for local state files, preserving open delete-sharing readers
    /// without File.Replace's missing-name and exclusive-handle windows.
    /// </param>
    public static void WriteAllText(string destinationPath, string content, bool preserveReaders = false)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            File.WriteAllText(tempPath, content);
            if (preserveReaders)
            {
                PublishPreservingReaders(tempPath, destinationPath);
            }
            else
            {
                File.Move(tempPath, destinationPath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteLeftoverTemp(tempPath);
        }
    }

    /// <summary>Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/> atomically.</summary>
    public static void Copy(string sourcePath, string destinationPath)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteLeftoverTemp(tempPath);
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temporary file in the destination directory and returns its
    /// path without publishing it, so the caller can validate the content (e.g. verify an Authenticode
    /// signature) before calling <see cref="Publish"/> to move it into place.
    /// </summary>
    public static async Task<string> WriteStagedAsync(string destinationPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var tempPath = MakeTempPath(destinationPath);
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        return tempPath;
    }

    /// <summary>Atomically moves a staged temp file (from <see cref="WriteStagedAsync"/>) into place.</summary>
    public static void Publish(string stagedPath, string destinationPath) =>
        File.Move(stagedPath, destinationPath, overwrite: true);

    /// <summary>Deletes a staged temp file that will not be published. Best effort.</summary>
    public static void DiscardStaged(string stagedPath) => TryDeleteLeftoverTemp(stagedPath);

    private static unsafe void PublishPreservingReaders(string stagedPath, string destinationPath)
    {
        const uint deleteAccess = 0x00010000;
        const uint openReparsePoint = 0x00200000;
        // This handle survives the rename; write access or exclusive sharing would block new readers.
        using var handle = OpenForRename(
            LongPathHelper.EnsureExtendedLengthPrefix(Path.GetFullPath(stagedPath)),
            deleteAccess, (uint)(FileShare.ReadWrite | FileShare.Delete), 0, (uint)FileMode.Open, openReparsePoint, 0);
        if (handle.IsInvalid)
        {
            ThrowRenameError(destinationPath, Marshal.GetLastPInvokeError());
        }

        var destination = LongPathHelper.EnsureExtendedLengthPrefix(Path.GetFullPath(destinationPath));
        var name = MemoryMarshal.AsBytes(destination.AsSpan());
        // FILE_RENAME_INFO contains a DWORD, an aligned HANDLE, a DWORD byte length, then UTF-16.
        var nameOffset = 2 * IntPtr.Size + sizeof(uint);
        var buffer = new byte[checked(nameOffset + name.Length + sizeof(char))];
        const uint replaceIfExists = 0x00000001;
        const uint posixSemantics = 0x00000002;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, replaceIfExists | posixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2 * IntPtr.Size), (uint)name.Length);
        name.CopyTo(buffer.AsSpan(nameOffset));
        fixed (byte* data = buffer)
        {
            const int fileRenameInfoEx = 22;
            if (RenameByHandle(handle, fileRenameInfoEx, data, (uint)buffer.Length) == 0)
            {
                ThrowRenameError(destinationPath, Marshal.GetLastPInvokeError());
            }
        }
    }

    private static void ThrowRenameError(string destinationPath, int error)
    {
        var message = $"Could not atomically publish '{destinationPath}': {new Win32Exception(error).Message}";
        if (error == 5)
        {
            throw new UnauthorizedAccessException(message);
        }
        throw new IOException(message, unchecked((int)(0x80070000u | (uint)error)));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenForRename(
        string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int RenameByHandle(SafeFileHandle handle, int informationClass, byte* buffer, uint length);

    private static string MakeTempPath(string destinationPath) =>
        destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void TryDeleteLeftoverTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best effort — a leftover .tmp is harmless and will be overwritten or ignored.
        }
    }
}
