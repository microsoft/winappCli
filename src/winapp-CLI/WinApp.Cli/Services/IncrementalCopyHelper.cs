// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Static helper for incremental file copy operations.
/// Compares source and destination by file size and last-write timestamp
/// to skip unchanged files.
/// </summary>
internal static class IncrementalCopyHelper
{
    /// <summary>
    /// Copies a list of files to a target directory incrementally,
    /// skipping files that are unchanged (same size and timestamp).
    /// </summary>
    internal static (int Copied, int Skipped) CopyFiles(
        List<(FileInfo SourceFile, string RelativePath)> files,
        DirectoryInfo targetDir)
    {
        var resolvedFiles = new List<(FileInfo SourceFile, FileInfo TargetFile)>(files.Count);
        foreach (var (sourceFile, relativePath) in files)
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException($"The file path '{relativePath}' must be relative to the target directory.", nameof(files));
            }

            var destinationPath = Path.GetFullPath(Path.Join(targetDir.FullName, relativePath));
            if (!MsixService.IsPathInsideDirectory(destinationPath, targetDir.FullName))
            {
                throw new ArgumentException($"The file path '{relativePath}' resolves outside the target directory.", nameof(files));
            }
            resolvedFiles.Add((sourceFile, new FileInfo(destinationPath)));
        }

        int copied = 0, skipped = 0;

        foreach (var (sourceFile, targetFile) in resolvedFiles)
        {
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
