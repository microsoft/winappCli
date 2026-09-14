// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

internal static class MigrationPathResolver
{
    internal static bool TryCanonicalizeRoot(
        string? value,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("@(", StringComparison.Ordinal)
            || value.IndexOfAny(['*', '?']) >= 0)
        {
            error = "The root must be a non-empty literal path.";
            return false;
        }

        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(value));
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static bool TryResolveContainedRelativePath(
        string root,
        string? value,
        out string fullPath,
        out string normalizedRelativePath,
        out string error)
    {
        fullPath = string.Empty;
        normalizedRelativePath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The path must be a non-empty literal relative path.";
            return false;
        }
        if (Path.IsPathRooted(value)
            || value.Contains(':')
            || value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("@(", StringComparison.Ordinal)
            || value.IndexOfAny(['*', '?']) >= 0)
        {
            error = "The path must be a literal relative path.";
            return false;
        }

        var normalizedInput = value.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalizedInput.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
            segment.TrimEnd(' ', '.') == ".."))
        {
            error = "The path cannot contain parent-directory traversal.";
            return false;
        }

        string canonicalRoot;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
            fullPath = Path.GetFullPath(
                Path.Combine(canonicalRoot, normalizedInput));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            fullPath = string.Empty;
            error = exception.Message;
            return false;
        }

        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            fullPath = string.Empty;
            error = "The path must remain contained by its declared root.";
            return false;
        }

        var relativePath = Path.GetRelativePath(canonicalRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            fullPath = string.Empty;
            error = "The path must remain contained by its declared root.";
            return false;
        }

        normalizedRelativePath = relativePath.Replace('\\', '/');
        var current = canonicalRoot;
        try
        {
            foreach (var segment in relativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current))
                    && File.GetAttributes(current).HasFlag(
                        FileAttributes.ReparsePoint))
                {
                    fullPath = string.Empty;
                    normalizedRelativePath = string.Empty;
                    error =
                        "The path cannot traverse a reparse point outside the declared migration tree.";
                    return false;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            normalizedRelativePath = string.Empty;
            error = $"The path could not be inspected safely: {exception.Message}";
            return false;
        }
        return true;
    }
}
