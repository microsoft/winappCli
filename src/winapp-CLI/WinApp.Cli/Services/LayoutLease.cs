// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// A cross-process exclusive claim on one generated loose-layout directory, held for as long as the
/// layout is being produced <em>and</em> consumed.
/// </summary>
/// <remarks>
/// Two <c>winapp run</c> invocations in the same build output share one generated <c>AppX</c>
/// directory. Without a claim spanning both phases, the second run can rewrite the layout after the
/// first has materialized it but before the first registers or deploys it, so the first would ship
/// the second's files. Covering only materialization would close the smaller race and leave that
/// one open, which is why the lease is taken by the command and released once the layout has been
/// registered or deployed.
/// <para>
/// "Consumed" ends there, not at application exit. Once the app is registered, nothing the run does
/// afterward reads the host directory again, and holding the claim across a long-running app would
/// block every other winapp workflow against that build output. This is the same boundary the guest
/// mutation lease already draws.
/// </para>
/// <para>
/// The lock lives beside the layout, in a reserved directory excluded from app payload. Its location
/// depends only on the layout, never the caller's working directory or cache configuration.
/// </para>
/// </remarks>
internal sealed class LayoutLease : IDisposable
{
    internal const string LockDirectoryName = ".winapp-layout-locks";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly FileStream _stream;

    private LayoutLease(FileStream stream) => _stream = stream;

    /// <summary>
    /// Claims <paramref name="layoutDirectory"/> until the returned lease is disposed.
    /// </summary>
    /// <exception cref="TimeoutException">Another winapp process held the layout for too long.</exception>
    internal static LayoutLease Acquire(
        DirectoryInfo layoutDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Func<string, FileStream>? openLock = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockPath = LongPathHelper.EnsureExtendedLengthPrefix(GetLockPath(layoutDirectory));
        var lockDirectory = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(lockDirectory);

        // A redirected lock directory would no longer be bookkeeping beside this resource.
        RejectLinkedLockPath(lockDirectory);
        RejectLinkedLockPath(lockPath);
        var elapsed = Stopwatch.StartNew();
        var waitLimit = timeout ?? DefaultTimeout;
        openLock ??= path => new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // The kernel releases the handle (and removes the file) even if a process is killed.
                // Never delete the directory on release: another layout or waiter may be using it.
                return new LayoutLease(openLock(lockPath));
            }
            catch (IOException ex) when (IsContention(ex))
            {
                if (elapsed.Elapsed >= waitLimit)
                {
                    throw new TimeoutException(
                        $"Another winapp process is using the app layout at '{layoutDirectory.FullName}'. Wait for it to finish, " +
                        "or use --output-appx-directory to give this run a layout of its own.", ex);
                }

                var remaining = waitLimit - elapsed.Elapsed;
                cancellationToken.WaitHandle.WaitOne(
                    TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 0, 100)));
            }
        }
    }

    internal static string GetLockPath(DirectoryInfo layoutDirectory)
    {
        var canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(LongPathHelper.StripExtendedPrefix(layoutDirectory.FullName)));
        ThrowIfArtifactPath(canonical);
        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException("A drive or share root cannot be used as an app layout. Choose a subdirectory.");
        }

        // Fixed-length names and extended-length I/O keep bookkeeping usable even when a layout
        // near MAX_PATH leaves no room for an appended suffix.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        return Path.Combine(parent, LockDirectoryName, key + ".lock");
    }

    internal static bool IsContention(IOException exception) =>
        exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    internal static bool IsArtifactPath(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, LockDirectoryName, StringComparison.OrdinalIgnoreCase));

    internal static void ThrowIfArtifactPath(string path)
    {
        if (IsArtifactPath(path))
        {
            throw new InvalidOperationException(
                $"'{path}' uses '{LockDirectoryName}', which is reserved for layout coordination and cannot be app payload. " +
                "Choose a different layout or payload path.");
        }
    }

    /// <summary>
    /// Existing lock state in a destination may belong to another layout. Never prune it, copy over
    /// it, or register it as payload; refuse the ambiguous layout without deleting anything.
    /// </summary>
    internal static void EnsureNoArtifactsInLayout(DirectoryInfo layoutDirectory)
    {
        ThrowIfArtifactPath(layoutDirectory.FullName);
        layoutDirectory.Refresh();
        if (!layoutDirectory.Exists)
        {
            return;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(layoutDirectory);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                ThrowIfArtifactPath(entry.FullName);
                if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void RejectLinkedLockPath(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"Layout lock path '{path}' is a symbolic link or junction. Use a real layout lock directory.");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The lock file is normally absent until it is acquired.
        }
    }

    public void Dispose() => _stream.Dispose();
}
