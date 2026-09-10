// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;

namespace WinApp.Cli.Helpers;

// Filesystem-safety helpers for workspace writes and lockfiles.
internal static class PathSafety
{
    // Rejects paths outside boundary, UNC paths, and reparse points below boundary.
    // Walks downward so symlinks/junctions are detected before they can be followed.
    public static bool HasReparsePointOnPath(string path, string boundary)
    {
        string fullPath;
        string fullBoundary;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullBoundary = Path.GetFullPath(boundary);
        }
        catch
        {
            return true;
        }

        if (IsNetworkPath(fullPath) || IsNetworkPath(fullBoundary))
        {
            return true;
        }

        var normalizedBoundary = NormalizeForContainment(fullBoundary);
        var normalizedPath = NormalizeForContainment(fullPath);

        // String-only containment. Boundary itself is a valid target;
        // otherwise path must live under boundary + a separator.
        bool isBoundaryItself = string.Equals(
            normalizedPath,
            normalizedBoundary,
            StringComparison.OrdinalIgnoreCase);
        var boundaryWithSep = normalizedBoundary.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedBoundary
            : normalizedBoundary + Path.DirectorySeparatorChar;
        bool isUnderBoundary = normalizedPath.StartsWith(
            boundaryWithSep,
            StringComparison.OrdinalIgnoreCase);
        if (!isBoundaryItself && !isUnderBoundary)
        {
            return true;
        }

        // Check boundary itself first — a reparse-point boundary would make
        // every descendant probe silently follow it.
        return WalkForReparsePoint(normalizedBoundary, normalizedPath);
    }

    /// <summary>
    /// True when reaching <paramref name="path"/> from <paramref name="root"/> traverses a
    /// reparse point (symlink or junction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Project files, solutions, and lockfiles all live in the repository, so cloning a
    /// repository is enough to choose the paths this tool resolves. A relative path is
    /// otherwise harmless, but a directory symlink checked into the repository can turn
    /// <c>libs\Lib\Lib.csproj</c> into a location on an SMB share, and merely probing it
    /// authenticates to whoever answers. Reparse points are detected by attribute, before
    /// any call that would follow them.
    /// </para>
    /// <para>
    /// Unlike <see cref="HasReparsePointOnPath"/> this imposes no containment requirement —
    /// a solution in <c>src\</c> legitimately lists <c>..\libs\Lib\Lib.csproj</c> — so the
    /// walk starts at the deepest directory <paramref name="root"/> and
    /// <paramref name="path"/> share.
    /// </para>
    /// <para>
    /// A network <paramref name="root"/> returns <c>false</c>: the caller selected that
    /// location deliberately, everything beneath it is already remote, and there is no
    /// local-to-network transition left to prevent.
    /// </para>
    /// </remarks>
    public static bool CrossesReparsePoint(string path, string root)
    {
        string fullPath;
        string fullRoot;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullRoot = Path.GetFullPath(root);
        }
        catch
        {
            return true;
        }

        if (IsNetworkPath(fullRoot))
        {
            return false;
        }

        // A local root can only reach the network by being redirected, and every
        // redirection below is a reparse point the walk would catch. A path that is
        // already network-shaped got there some other way; refuse it outright.
        if (IsNetworkPath(fullPath))
        {
            return true;
        }

        string? start = DeepestCommonAncestor(
            NormalizeForContainment(fullRoot),
            NormalizeForContainment(fullPath));
        if (start is null)
        {
            // Different volumes: no ancestor inside the caller's tree to start from.
            return true;
        }

        return WalkForReparsePoint(start, NormalizeForContainment(fullPath));
    }

    /// <summary>
    /// True when <paramref name="path"/> itself is a reparse point (junction/symlink), without
    /// following it, or is network-shaped. Unlike <see cref="CrossesReparsePoint"/> this checks
    /// only the single node, so a package folder a developer relocated with a junction can be
    /// trusted while a redirected child beneath it is still rejected.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return IsNetworkPath(full) || IsReparseOrProbeUnknown(NormalizeForContainment(full));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether any component of <paramref name="path"/> is a link that resolves to a
    /// network location. Unlike <see cref="IsNetworkPath"/>, which reads the path as
    /// written, this follows the redirection: a junction at <c>D:\packages</c> pointing at
    /// <c>\\server\share</c> is not network-shaped as a string, but reading through it
    /// still authenticates outward to a host the value's author chose.
    /// </summary>
    /// <remarks>
    /// Only redirections that leave the machine are refused. A junction that relocates a
    /// package cache onto another local volume is a normal developer setup, which is why
    /// this is not simply a reparse-point check — that is
    /// <see cref="CrossesReparsePoint"/>, and applying it to a user-configured location
    /// outside the repository would reject ordinary machines.
    /// </remarks>
    public static bool RedirectsToNetwork(string path)
    {
        string normalized;
        string? root;
        try
        {
            string full = Path.GetFullPath(path);
            if (IsNetworkPath(full))
            {
                return true;
            }
            normalized = NormalizeForContainment(full);
            root = Path.GetPathRoot(normalized);
        }
        catch
        {
            return true;
        }

        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        string current = root;
        string[] segments = normalized[Math.Min(root.Length, normalized.Length)..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (LinkLeavesMachine(current))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a single component is a link whose final target is a network location. A
    /// component that does not exist yet is not a redirection; a link that cannot be
    /// resolved is refused, because an unreadable link is one that cannot be cleared.
    /// </summary>
    private static bool LinkLeavesMachine(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            string? resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.LinkTarget;
            return resolved is not null && IsNetworkPath(resolved);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or lives beneath
    /// it. Pure string containment: this answers "does the repository control this location",
    /// not "is it safe to touch" — pair it with <see cref="CrossesReparsePoint"/> for that.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        string normalizedPath;
        string normalizedRoot;
        try
        {
            normalizedPath = NormalizeForContainment(Path.GetFullPath(path));
            normalizedRoot = NormalizeForContainment(Path.GetFullPath(root));
        }
        catch
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The deepest directory both paths share, or <c>null</c> when they do not share a
    /// volume. Both inputs must already be absolute and normalized.
    /// </summary>
    private static string? DeepestCommonAncestor(string a, string b)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var aParts = a.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var bParts = b.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        int shared = 0;
        while (shared < aParts.Length
            && shared < bParts.Length
            && string.Equals(aParts[shared], bParts[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        if (shared == 0)
        {
            return null;
        }

        // Rebuild from the original string so the volume keeps its trailing separator
        // (`C:` alone is drive-relative and would probe the wrong path).
        int consumed = 0;
        int index = 0;
        while (index < a.Length && consumed < shared)
        {
            if (separators.Contains(a[index]))
            {
                index++;
                continue;
            }
            while (index < a.Length && !separators.Contains(a[index]))
            {
                index++;
            }
            consumed++;
        }

        return NormalizeForContainment(a.Substring(0, index));
    }

    /// <summary>
    /// Walks each path component from <paramref name="start"/> down to
    /// <paramref name="target"/>, returning true at the first reparse point.
    /// <paramref name="target"/> must live under <paramref name="start"/>.
    /// </summary>
    private static bool WalkForReparsePoint(string start, string target)
    {
        if (IsReparseOrProbeUnknown(start))
        {
            return true;
        }

        var remainder = target.Substring(Math.Min(start.Length, target.Length));
        var segments = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = start;
        foreach (var seg in segments)
        {
            current = Path.Combine(current, seg);
            if (IsReparseOrProbeUnknown(current))
            {
                return true;
            }
        }

        return false;
    }

    // True for any path that is not plainly a local drive: UNC (`\\server\share`,
    // `\\?\UNC\…`, `\\.\UNC\…`) and every other DOS device path. Only a drive letter
    // (`\\?\C:\…`) or a volume GUID (`\\?\Volume{…}\…`) after the device prefix names
    // local storage; `\\?\GLOBALROOT\Device\Mup\server\share` reaches the SMB redirector
    // just as a UNC path does, so an allow-list is the only safe reading.
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var p = path.Replace('/', '\\');

        if (p.Length < 3 || p[0] != '\\' || p[1] != '\\')
        {
            return false;
        }

        // Plain UNC: \\server\share…
        if (p[2] != '?' && p[2] != '.')
        {
            return true;
        }

        // Device path: \\?\<device>… or \\.\<device>…
        if (p.Length < 4 || p[3] != '\\')
        {
            return true;
        }
        var device = p.Substring(4);
        bool isDriveLetter = device.Length >= 2 && char.IsAsciiLetter(device[0]) && device[1] == ':';
        bool isVolumeGuid = device.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase);
        return !isDriveLetter && !isVolumeGuid;
    }

    // Preserve `C:\`; `C:` is drive-relative and would probe the wrong path.
    private static string NormalizeForContainment(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + Path.DirectorySeparatorChar;
        }
        return trimmed;
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // True if the path is a reparse point, OR if attributes cannot be probed
    // for an unknown reason (access denied, IO error). FileNotFound /
    // DirectoryNotFound return false — genuinely absent paths have no reparse
    // metadata to follow. Any other failure biases callers to "unsafe".
    private static bool IsReparseOrProbeUnknown(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Access denied / IO error — bias to unsafe so callers refuse to follow.
            return true;
        }
    }

    // Stage to a sibling temp file (same volume so the move stays atomic),
    // flush to disk, rename over the destination. Prevents a crash mid-write
    // from leaving the file truncated.
    public static async Task AtomicWriteAllTextAsync(
        string path,
        string contents,
        System.Text.Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            await using (var sw = new StreamWriter(fs, encoding))
            {
                await sw.WriteAsync(contents.AsMemory(), cancellationToken);
                await sw.FlushAsync(cancellationToken);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup; surface original error.
            }
            throw;
        }
    }

    /// <summary>
    /// Synchronous counterpart to <see cref="AtomicWriteAllTextAsync"/>: stage to a sibling
    /// temp file, flush to disk, rename over the destination. Defaults to UTF-8 without a BOM.
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents, System.Text.Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096))
            using (var sw = new StreamWriter(fs, encoding ?? Utf8NoBom))
            {
                sw.Write(contents);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: a temp file we cannot delete is not worth
                // masking the original write failure, which is rethrown below.
            }
            throw;
        }
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
