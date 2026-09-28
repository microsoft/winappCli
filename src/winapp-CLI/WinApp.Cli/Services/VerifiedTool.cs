// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinApp.Cli.Services;

/// <summary>
/// A downloaded build tool that has passed the Authenticode gate and is being kept in place until
/// the caller has finished running it. Dispose it once the tool has exited.
/// </summary>
/// <remarks>
/// Verifying a tool and launching it are two separate moments, and both name the tool by path, so on
/// their own they say nothing about whether the same bytes were involved in each: whoever can write
/// to the NuGet package cache could swap the file in between and have the replacement run. Holding
/// the file open closes that gap — while the handle is held, no one can write to, rename or delete
/// the file, nor rename or delete any directory above it.
///
/// A handle alone is not quite enough, because it pins the file rather than the path. If a directory
/// along the way is a junction, deleting and re-creating it re-points the original path at another
/// file while the handle stays valid, and the path would launch that one instead. <see cref="Path"/>
/// is therefore resolved from the handle itself, with every junction already followed, and is what
/// callers must launch.
/// </remarks>
internal sealed partial class VerifiedTool : IDisposable
{
    private readonly FileStream _held;

    private VerifiedTool(FileStream held, string path)
    {
        _held = held;
        Path = path;
    }

    /// <summary>
    /// Where to launch the tool from. This is the verified file's own location, not necessarily the
    /// path it was found at, so that a re-pointed junction cannot substitute a different file.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Opens <paramref name="toolPath"/> so it cannot be replaced, then checks it with
    /// <paramref name="signatureVerifier"/>.
    /// </summary>
    /// <exception cref="BuildToolSignatureException">
    /// The file could not be held open, or it is not validly signed by Microsoft. Either way the
    /// caller never gets a handle, so the tool is not run.
    /// </exception>
    internal static VerifiedTool Open(FileInfo toolPath, Func<string, ILogger, bool> signatureVerifier, ILogger logger)
    {
        var held = Hold(toolPath);

        try
        {
            // Resolved before the check, not after, so that the signature and the launch are known to
            // describe the same file. Checking the path the tool was found at and launching the
            // resolved one would let a junction re-pointed in between offer a signed file to the
            // check while a different file stays held and runs.
            var path = ResolveHeldPath(held, toolPath);

            if (!signatureVerifier(path, logger))
            {
                throw new BuildToolSignatureException(UnsignedMessage(toolPath));
            }

            return new VerifiedTool(held, path);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    public void Dispose() => _held.Dispose();

    /// <summary>
    /// What to tell the user when a downloaded tool fails the Authenticode gate. Shared so every
    /// caller reports the same problem the same way.
    /// </summary>
    internal static string UnsignedMessage(FileInfo toolPath) =>
        $"'{toolPath.Name}' is not validly signed by Microsoft, so it was not run ({toolPath.FullName}). " +
        "The file on disk is not what Microsoft published, which usually means a corrupt or partial " +
        "download. Delete the package from the NuGet cache and run the command again to re-download it.";

    private static FileStream Hold(FileInfo toolPath)
    {
        try
        {
            // FileShare.Read still admits other readers — including the loader, which is how the
            // tool can be launched while this handle is open — but denies write, rename and delete
            // to everyone.
            return new FileStream(toolPath.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BuildToolSignatureException(
                $"'{toolPath.Name}' could not be held open for verification, so it was not run ({toolPath.FullName}). " +
                "Something else has the file open for writing, or it was removed. Close whatever is using it, or " +
                "delete the package from the NuGet cache and run the command again to re-download it.");
        }
    }

    private static string ResolveHeldPath(FileStream held, FileInfo toolPath)
    {
        try
        {
            return FinalPathOf(held.SafeFileHandle);
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            // Falling back to the path the tool was found at would quietly reopen the substitution
            // window this class exists to close, and nothing downstream could tell the difference.
            // Refusing to run is the honest outcome: the guarantee either holds or the tool does
            // not run.
            throw new BuildToolSignatureException(
                $"'{toolPath.Name}' could not be pinned to a stable location, so it was not run ({toolPath.FullName}). " +
                "winapp could not confirm that the file it checked is the file Windows would load. Delete the " +
                "package from the NuGet cache and run the command again to re-download it.");
        }
    }

    private static unsafe string FinalPathOf(SafeFileHandle handle)
    {
        // Length is returned in characters and excludes the terminating null, except when the buffer
        // is too small, where it is the size required to hold it.
        var length = 4096;
        while (true)
        {
            var buffer = new char[length];
            uint written;
            fixed (char* p = buffer)
            {
                written = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
            }

            if (written == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (written < buffer.Length)
            {
                return Normalize(new string(buffer, 0, (int)written));
            }

            length = (int)written + 1;
        }
    }

    /// <summary>
    /// Turns the extended-length form the OS returns back into the ordinary path users and tools
    /// expect to see.
    /// </summary>
    private static string Normalize(string finalPath)
    {
        const string UncPrefix = @"\\?\UNC\";
        const string DevicePrefix = @"\\?\";

        if (finalPath.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + finalPath[UncPrefix.Length..];
        }

        return finalPath.StartsWith(DevicePrefix, StringComparison.Ordinal)
            ? finalPath[DevicePrefix.Length..]
            : finalPath;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
}
