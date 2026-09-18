// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>Retries cache storage operations in the invocation directory, never configuration or integrity failures.</summary>
internal sealed class CacheStorage(
    IWinappDirectoryService directories,
    string globalRelativePath,
    string localRelativePath,
    IStorageDiagnostics? diagnostics = null)
{
    private readonly string _globalRelativePath = RequireRelativePath(globalRelativePath, nameof(globalRelativePath));
    private readonly string _localRelativePath = RequireRelativePath(localRelativePath, nameof(localRelativePath));
    private bool _local;

    // A read-only probe: readable warm caches do not need writable directories.
    internal Action<string> InspectDirectory { get; set; } = InspectAncestors;

    public string DirectoryPath => ResolvePath();
    public bool IsExplicit => directories.IsGlobalCacheOverridden;
    internal bool IsLocalFallback => _local;

    public void Clear(Action<string> clear)
    {
        var global = Path.Combine(directories.GetGlobalWinappDirectory().FullName, _globalRelativePath);
        InspectDirectory(global);
        clear(global);
        if (!IsExplicit)
        {
            var root = directories.GetLocalCacheDirectory().FullName;
            var local = Path.Combine(root, _localRelativePath);
            ValidateLocalPath(local, root);
            ValidateLocalTree(local);
            if (!local.Equals(global, StringComparison.OrdinalIgnoreCase))
            {
                clear(local);
            }
        }
    }

    public T Run<T>(Func<string, T> operation)
    {
        try
        {
            var path = ResolvePath();
            var result = operation(path);
            ReportFallback(path);
            return result;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            SwitchToLocal(ex);
            try
            {
                var path = ResolvePath();
                var result = operation(path);
                ReportFallback(path);
                return result;
            }
            catch (Exception localError) when (IsStorageFailure(localError))
            {
                throw Unavailable(localError);
            }
        }
    }

    public async Task<T> RunAsync<T>(Func<string, Task<T>> operation)
    {
        try
        {
            var path = ResolvePath();
            var result = await operation(path).ConfigureAwait(false);
            ReportFallback(path);
            return result;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            SwitchToLocal(ex);
            try
            {
                var path = ResolvePath();
                var result = await operation(path).ConfigureAwait(false);
                ReportFallback(path);
                return result;
            }
            catch (Exception localError) when (IsStorageFailure(localError))
            {
                throw Unavailable(localError);
            }
        }
    }

    public void WarnUnavailable(Exception error) =>
        diagnostics?.Warning("cache-unavailable", $"Cache storage is unavailable; continuing without caching. {error.Message}");

    internal static bool IsStorageFailure(Exception error) =>
        error is UnauthorizedAccessException or IOException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions.All(IsStorageFailure);

    private string ResolvePath()
    {
        string path;
        if (_local)
        {
            var root = directories.GetLocalCacheDirectory().FullName;
            path = Path.GetFullPath(Path.Combine(root, _localRelativePath));
            ValidateLocalPath(path, root);
            ValidateLocalTree(path);
        }
        else
        {
            path = Path.Combine(directories.GetGlobalWinappDirectory().FullName, _globalRelativePath);
        }
        InspectDirectory(path);
        return path;
    }

    private static string RequireRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathRooted(path)
            || path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."
                    || segment.EndsWith(' ') || segment.EndsWith('.')
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("A cache subdirectory must be relative and cannot contain parent traversal or invalid path segments.", parameterName);
        }
        return path;
    }

    private void SwitchToLocal(Exception error)
    {
        if (directories.IsGlobalCacheOverridden)
        {
            throw new IOException(
                $"The configured WINAPP_CLI_CACHE_DIRECTORY cannot be used. Correct the configured path or its permissions. {error.Message}", error);
        }
        if (_local)
        {
            throw Unavailable(error);
        }
        _local = true;
    }

    private void ReportFallback(string path)
    {
        if (_local)
        {
            diagnostics?.Warning("cache-fallback",
                $"The default winapp cache is inaccessible. Using cache '{path}'.");
        }
    }

    private static IOException Unavailable(Exception error) =>
        new($"Neither the default winapp cache nor the invocation directory's .winapp\\cache can be used. " +
            $"Grant access to one of these locations or set WINAPP_CLI_CACHE_DIRECTORY to an accessible directory. {error.Message}", error);

    internal static void InspectAncestors(string path) => InspectAncestors(path, File.GetAttributes);

    internal static void InspectAncestors(string path, Func<string, FileAttributes> attributesForPath)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            try
            {
                var attributes = attributesForPath(dir.FullName);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Cache directory '{dir.FullName}' is a file.");
                }
                return;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void ValidateLocalPath(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The local cache must stay inside the invocation directory.");
        }
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Local cache path '{current}' is a link or reparse point.");
                }
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }

    internal static void ValidateLocalTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Local cache entry '{entry}' is a link or reparse point.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                ValidateLocalTree(entry);
            }
        }
    }
}

internal sealed class CacheWriteException(string path, Exception inner)
    : IOException($"Could not write cache '{path}': {inner.Message}", inner)
{
}
