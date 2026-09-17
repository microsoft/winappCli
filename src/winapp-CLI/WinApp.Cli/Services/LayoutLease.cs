// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

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
/// The lock file lives in winapp's state directory rather than in the layout: anything inside the
/// layout is app payload, and would be packaged and registered along with it.
/// </para>
/// </remarks>
internal sealed class LayoutLease : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly FileStream _stream;

    private LayoutLease(FileStream stream) => _stream = stream;

    /// <summary>
    /// Claims <paramref name="layoutDirectory"/> until the returned lease is disposed.
    /// </summary>
    /// <exception cref="TimeoutException">Another winapp process held the layout for too long.</exception>
    internal static LayoutLease Acquire(
        DirectoryInfo winappStateRoot,
        DirectoryInfo layoutDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var stateDirectory = Path.Combine(winappStateRoot.FullName, "layout-locks");
        Directory.CreateDirectory(stateDirectory);

        // Hashed so the name is a fixed length no matter how deep the layout is, and
        // case-insensitively, so two spellings of one Windows path do not become two locks.
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layoutDirectory.FullName));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        var lockPath = Path.Combine(stateDirectory, key + ".lock");

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // DeleteOnClose keeps the state directory from growing a file per layout ever built.
                return new LayoutLease(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Another winapp process is using the app layout at '{canonical}'. Wait for it to finish, " +
                        $"or use --output-appx-directory to give this run a layout of its own.");
                }

                Thread.Sleep(100);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
