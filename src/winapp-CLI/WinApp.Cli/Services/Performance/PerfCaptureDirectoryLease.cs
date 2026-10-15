// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinApp.Cli.Services.Performance;

/// <summary>Keeps elevated capture paths from being renamed or redirected while the worker writes.</summary>
internal sealed partial class PerfCaptureDirectoryLease : IDisposable
{
    private const uint ListDirectoryAndReadAttributes = 0x81;
    private const uint OpenExisting = 3;
    private const uint OpenDirectoryWithoutFollowingLinks = 0x02000000 | 0x00200000;
    private readonly List<(string Path, SafeFileHandle Handle)> handles = [];
    private readonly List<FileStream> anchors = [];
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);

    private PerfCaptureDirectoryLease()
    {
    }

    public static PerfCaptureDirectoryLease Open(params string[] directories)
    {
        var lease = new PerfCaptureDirectoryLease();
        try
        {
            foreach (var directory in directories)
            {
                lease.Pin(directory);
            }
            // Atomic child-file replacement needs directory write-sharing. Before allowing it,
            // every directory is kept nonempty by a pinned child directory or a locked anchor:
            // Windows cannot turn a nonempty directory into a junction or symbolic link.
            for (var i = 0; i < lease.handles.Count; i++)
            {
                var (path, handle) = lease.handles[i];
                var shared = OpenDirectory(path, FileShare.ReadWrite);
                lease.handles[i] = (path, shared);
                handle.Dispose();
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void Pin(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)!;
        if (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType == DriveType.Network)
        {
            throw new IOException("Elevated performance recording requires a local drive path without directory links.");
        }

        var ancestors = new Stack<string>();
        for (var current = Path.TrimEndingDirectorySeparator(fullPath); current is not null; current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }
        foreach (var path in ancestors)
        {
            if (!paths.Add(path))
            {
                continue;
            }
            // Pin from the root down. Denying delete prevents replacement; denying write prevents
            // turning an already-open directory into a reparse point. Inspect the handle, not the path.
            handles.Add((path, OpenDirectory(path, FileShare.Read)));
        }
        anchors.Add(new FileStream(Path.Join(fullPath, ".capture-lock-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose));
    }

    private static SafeFileHandle OpenDirectory(string path, FileShare share)
    {
        var handle = CreateFileW(path, ListDirectoryAndReadAttributes, share, IntPtr.Zero,
            OpenExisting, OpenDirectoryWithoutFollowingLinks, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Cannot secure performance capture directory '{path}': {new Win32Exception(error).Message}");
        }
        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Elevated performance recording cannot use directory links: '{path}'. Choose a local directory without symbolic links or junctions.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var anchor in anchors)
        {
            anchor.Dispose();
        }
        anchors.Clear();
        for (var i = handles.Count - 1; i >= 0; i--)
        {
            handles[i].Handle.Dispose();
        }
        handles.Clear();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);
}
