// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// File writes that publish their result atomically: content is first written to a uniquely-named
/// temporary file in the destination directory, then moved into place with a single rename. A
/// concurrent reader (e.g. a second <c>winapp run --debug-output</c>, or the same run re-resolving
/// the debugger cache) therefore only ever observes the final path as either absent or fully written
/// — never partially written or not-yet-verified.
/// </summary>
/// <remarks>
/// Windows refuses to rename over a file that any handle still has open, even one opened with
/// <see cref="FileShare.Delete"/>, and reports it as "Access to the path is denied". A concurrent
/// reader therefore makes the publishing rename fail, not just wait. Readers hold the file only for
/// as long as one read takes, so every rename retries that refusal for a short, bounded window
/// before giving up.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>How long a publishing rename keeps retrying while a reader holds the destination open.</summary>
    internal static readonly TimeSpan ReplaceRetryWindow = TimeSpan.FromSeconds(2);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="destinationPath"/> atomically.</summary>
    public static async Task WriteAllBytesAsync(string destinationPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            ReplaceWithRetry(tempPath, destinationPath);
        }
        finally
        {
            TryDeleteLeftoverTemp(tempPath);
        }
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="destinationPath"/> atomically.</summary>
    public static void WriteAllText(string destinationPath, string content)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            File.WriteAllText(tempPath, content);
            ReplaceWithRetry(tempPath, destinationPath);
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
            ReplaceWithRetry(tempPath, destinationPath);
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
        ReplaceWithRetry(stagedPath, destinationPath);

    /// <summary>Deletes a staged temp file that will not be published. Best effort.</summary>
    public static void DiscardStaged(string stagedPath) => TryDeleteLeftoverTemp(stagedPath);

    private static void ReplaceWithRetry(string sourcePath, string destinationPath)
    {
        var deadline = Environment.TickCount64 + (long)ReplaceRetryWindow.TotalMilliseconds;
        var delay = 1;
        while (true)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsHeldOpenByAnotherHandle(ex) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 50);
            }
        }
    }

    // ERROR_ACCESS_DENIED is how a rename over an open destination fails; sharing and lock
    // violations are how it fails while another writer is mid-replace.
    private static bool IsHeldOpenByAnotherHandle(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && (ex.HResult & 0xffff) is 32 or 33);

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
