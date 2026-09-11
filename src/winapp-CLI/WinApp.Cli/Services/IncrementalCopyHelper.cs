// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Static helper for incremental file copy operations.
/// Compares source and destination by file size and last-write timestamp
/// to skip unchanged files and remove stale files.
/// </summary>
internal static class IncrementalCopyHelper
{
    internal record SyncResult(int Copied, int Skipped, int Deleted);

    /// <summary>
    /// Synchronizes files from <paramref name="sourceDir"/> to <paramref name="destDir"/> incrementally.
    /// Only copies files that are new or changed (by size or timestamp).
    /// Removes stale files from <paramref name="destDir"/> that no longer exist in source,
    /// except for files in <paramref name="protectedFileNames"/>.
    /// </summary>
    internal static SyncResult SyncDirectory(
        DirectoryInfo sourceDir,
        DirectoryInfo destDir,
        HashSet<string>? protectedFileNames = null)
    {
        if (PathSafety.HasReparsePointOnExistingPath(sourceDir.FullName))
        {
            throw new InvalidOperationException(
                $"The source directory '{sourceDir.FullName}' contains a symbolic link or junction and cannot be synchronized safely.");
        }
        if (DirectoryRelationship.IsSameOrAncestor(destDir, sourceDir))
        {
            throw new InvalidOperationException(
                $"The destination directory '{destDir.FullName}' cannot be the source directory '{sourceDir.FullName}' or one of its ancestors.");
        }
        if (PathSafety.HasReparsePointOnExistingPath(destDir.FullName))
        {
            throw new InvalidOperationException(
                $"The destination directory '{destDir.FullName}' contains a symbolic link or junction and cannot be synchronized safely.");
        }

        if (!destDir.Exists)
        {
            destDir.Create();
        }

        var destFullPath = destDir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sourceRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, skipped = 0;

        foreach (var file in sourceDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // Skip files that are inside the dest folder (if dest is nested inside source)
            if (file.FullName.StartsWith(destFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDir.FullName, file.FullName);
            sourceRelativePaths.Add(relativePath);
            var destFile = new FileInfo(Path.Combine(destDir.FullName, relativePath));

            // Skip copy if destination exists with same size and timestamp
            if (destFile.Exists && destFile.Length == file.Length && destFile.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                skipped++;
                continue;
            }

            destFile.Directory?.Create();
            file.CopyTo(destFile.FullName, overwrite: true);
            copied++;
        }

        // Remove stale files in dest that no longer exist in source
        int deleted = 0;
        foreach (var destFile in destDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(destDir.FullName, destFile.FullName);

            if (protectedFileNames != null && protectedFileNames.Contains(relativePath))
            {
                continue;
            }

            if (!sourceRelativePaths.Contains(relativePath))
            {
                destFile.Delete();
                deleted++;
            }
        }

        return new SyncResult(copied, skipped, deleted);
    }

    /// <summary>
    /// Copies a list of files to a target directory incrementally,
    /// skipping files that are unchanged (same size and timestamp).
    /// </summary>
    internal static (int Copied, int Skipped) CopyFiles(
        List<(FileInfo SourceFile, string RelativePath)> files,
        DirectoryInfo targetDir)
    {
        int copied = 0, skipped = 0;

        foreach (var (sourceFile, relativePath) in files)
        {
            var targetFile = new FileInfo(Path.Combine(targetDir.FullName, relativePath));

            if (targetFile.Exists && targetFile.Length == sourceFile.Length && targetFile.LastWriteTimeUtc == sourceFile.LastWriteTimeUtc)
            {
                skipped++;
                continue;
            }

            targetFile.Directory?.Create();
            sourceFile.CopyTo(targetFile.FullName, overwrite: true);
            copied++;
        }

        return (copied, skipped);
    }
}
