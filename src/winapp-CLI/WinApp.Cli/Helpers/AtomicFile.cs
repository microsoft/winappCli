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
internal static class AtomicFile
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
    /// <param name="replaceExistingUnderLease">
    /// Preserves open, delete-sharing readers of an existing destination. The caller must hold
    /// a writer lease covering the existence check and replacement: File.Replace does not
    /// support competing publishers. Other callers retain overwrite-by-move publication.
    /// </param>
    public static void WriteAllText(string destinationPath, string content, bool replaceExistingUnderLease = false)
    {
        var tempPath = MakeTempPath(destinationPath);
        try
        {
            File.WriteAllText(tempPath, content);
            if (replaceExistingUnderLease)
            {
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
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
