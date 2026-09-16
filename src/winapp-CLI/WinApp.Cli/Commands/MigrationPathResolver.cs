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
            if (!TryEnsureNoReparsePoints(
                    fullPath,
                    out error))
            {
                fullPath = string.Empty;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException)
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

        var normalizedInput = value
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalizedInput.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
            segment == ".."
            || (segment != "."
                && (segment.EndsWith(' ')
                    || segment.EndsWith('.')))))
        {
            error =
                "The path cannot contain parent traversal or Windows trailing-dot/space aliases.";
            return false;
        }

        if (!TryCanonicalizeRoot(root, out var canonicalRoot, out error))
        {
            return false;
        }
        try
        {
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

    internal static bool TryResolveContainedAbsolutePath(
        string root,
        string? value,
        out string fullPath,
        out string normalizedRelativePath,
        out string error)
    {
        fullPath = string.Empty;
        normalizedRelativePath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Path.IsPathFullyQualified(value)
            || value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("@(", StringComparison.Ordinal)
            || value.IndexOfAny(['*', '?']) >= 0)
        {
            error = "The path must be a fully qualified literal path.";
            return false;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(value));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = exception.Message;
            return false;
        }
        return TryResolveContainedRelativePath(
            root,
            relativePath,
            out fullPath,
            out normalizedRelativePath,
            out error);
    }

    private static bool TryEnsureNoReparsePoints(
        string path,
        out string error)
    {
        error = string.Empty;
        var pathRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            error = "The migration root has no filesystem root.";
            return false;
        }

        var current = pathRoot;
        var relative = Path.GetRelativePath(pathRoot, path);
        foreach (var segment in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(
                    FileAttributes.ReparsePoint))
            {
                error =
                    $"The migration root cannot traverse reparse point '{current}'.";
                return false;
            }
        }
        return true;
    }
}
