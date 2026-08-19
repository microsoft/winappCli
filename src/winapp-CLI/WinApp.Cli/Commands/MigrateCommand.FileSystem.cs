// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static string GetProjectName(string sourceRoot, string? sourceProject)
        {
            var candidate = sourceProject is null
                ? new DirectoryInfo(sourceRoot).Name
                : Path.GetFileNameWithoutExtension(sourceProject);
            return SanitizeProjectName(candidate + "App");
        }

        private static string SanitizeProjectName(string candidate)
        {
            var sanitized = InvalidProjectNameCharacter().Replace(candidate, "_").Trim('_', '.');
            if (string.IsNullOrEmpty(sanitized))
            {
                return "MigratedApp";
            }
            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "_" + sanitized;
            }
            return sanitized;
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        private static List<string> GetUnsupportedOutputEntries(string targetRoot)
        {
            if (File.Exists(targetRoot))
            {
                return ["<output path is a file>"];
            }

            if (!Directory.Exists(targetRoot))
            {
                return [];
            }

            var unsupported = Directory.EnumerateFileSystemEntries(targetRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !AllowedExistingOutputEntries.Contains(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (IsReparsePoint(new DirectoryInfo(targetRoot)))
            {
                unsupported.Add("<output directory is a reparse point>");
                return unsupported;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(targetRoot))
            {
                if (TryFindReparsePoint(entry, targetRoot, out var reparsePoint))
                {
                    unsupported.Add($"{NormalizePath(reparsePoint!)} (reparse point)");
                }
            }

            return unsupported;
        }

        private static bool TryMergeScaffold(string stagingRoot, string targetRoot, out string? error)
        {
            var stagedDirectories = Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stagingRoot, path))
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .ToList();
            var stagedFiles = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stagingRoot, path))
                .ToList();

            foreach (var relativePath in stagedDirectories)
            {
                if (File.Exists(Path.Combine(targetRoot, relativePath)))
                {
                    error = $"'{NormalizePath(relativePath)}' is a file in the output but a directory in the template.";
                    return false;
                }
            }

            foreach (var relativePath in stagedFiles)
            {
                var sourcePath = Path.Combine(stagingRoot, relativePath);
                var targetPath = Path.Combine(targetRoot, relativePath);
                if (Directory.Exists(targetPath))
                {
                    error = $"'{NormalizePath(relativePath)}' is a directory in the output but a file in the template.";
                    return false;
                }
                if (File.Exists(targetPath) && !FilesEqual(sourcePath, targetPath))
                {
                    error = $"'{NormalizePath(relativePath)}' conflicts with an existing metadata file.";
                    return false;
                }
            }

            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            try
            {
                if (!Directory.Exists(targetRoot))
                {
                    Directory.CreateDirectory(targetRoot);
                    createdDirectories.Add(targetRoot);
                }

                foreach (var relativePath in stagedDirectories)
                {
                    var targetDirectory = Path.Combine(targetRoot, relativePath);
                    if (!Directory.Exists(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                        createdDirectories.Add(targetDirectory);
                    }
                }
                foreach (var relativePath in stagedFiles)
                {
                    var targetPath = Path.Combine(targetRoot, relativePath);
                    if (File.Exists(targetPath))
                    {
                        continue;
                    }

                    var targetDirectory = Path.GetDirectoryName(targetPath)!;
                    var temporaryPath = Path.Combine(
                        targetDirectory,
                        $".{Path.GetFileName(targetPath)}.winapp-migrate-{Guid.NewGuid():N}");
                    try
                    {
                        File.Copy(Path.Combine(stagingRoot, relativePath), temporaryPath);
                        File.Move(temporaryPath, targetPath);
                        createdFiles.Add(targetPath);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var rollbackFailures = RollbackCreatedPaths(createdFiles, createdDirectories);
                error = $"I/O failure while creating the scaffold: {exception.Message}";
                if (rollbackFailures.Count > 0)
                {
                    error += $" Rollback could not remove: {string.Join(", ", rollbackFailures.Select(NormalizePath))}.";
                }
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryFindReparsePoint(string path, string targetRoot, out string? relativePath)
        {
            var pending = new Stack<FileSystemInfo>();
            pending.Push(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path));

            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                if (IsReparsePoint(entry))
                {
                    relativePath = Path.GetRelativePath(targetRoot, entry.FullName);
                    return true;
                }

                if (entry is DirectoryInfo directory)
                {
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        pending.Push(child);
                    }
                }
            }

            relativePath = null;
            return false;
        }

        private static bool IsReparsePoint(FileSystemInfo entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        private static List<string> RollbackCreatedPaths(
            IReadOnlyList<string> createdFiles,
            IReadOnlyList<string> createdDirectories)
        {
            var failures = new List<string>();
            foreach (var file in createdFiles.Reverse())
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{file} ({exception.Message})");
                }
            }

            foreach (var directory in createdDirectories.Reverse())
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{directory} ({exception.Message})");
                }
            }

            return failures;
        }

        private static bool FilesEqual(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (first.Length != second.Length)
            {
                return false;
            }

            return File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(secondPath));
        }

        // ───────────────────────── helpers ─────────────────────────────────────
        private static IEnumerable<string> EnumerateFiles(string root)
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (IsExcluded(f, root))
                {
                    continue;
                }
                yield return f;
            }
        }

        private static bool IsExcluded(string file, string root)
        {
            var rel = Path.GetRelativePath(root, file);
            foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                foreach (var ex in ExcludeDirSegments)
                {
                    if (string.Equals(seg, ex, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ShouldCopy(string file) => !IsBuildOrProjectFile(file);

        private static bool IsBuildOrProjectFile(string file)
        {
            var name = file.ToLowerInvariant();
            foreach (var ext in DoNotCopyExtensions)
            {
                if (name.EndsWith(ext, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CopyInto(string src, string dst)
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.Copy(src, dst, overwrite: true);
        }

        private static string? FindFile(string root, string fileName)
        {
            return EnumerateFiles(root).FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }

        // App.xaml / App.xaml.cs define the WinUI 3 startup bootstrap in the scaffold and must not
        // be clobbered by the UWP originals (which launch via Window.Current, not MainWindow).
        private static bool IsScaffoldStartupFile(string rel)
        {
            var name = Path.GetFileName(rel);
            return string.Equals(name, "App.xaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "App.xaml.cs", StringComparison.OrdinalIgnoreCase);
        }

        // True when source == target, or one directory contains the other.
        private static bool PathsOverlap(string sourceRoot, string targetRoot)
        {
            var a = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var aSlash = a + Path.DirectorySeparatorChar;
            var bSlash = b + Path.DirectorySeparatorChar;
            return b.StartsWith(aSlash, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(bSlash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
