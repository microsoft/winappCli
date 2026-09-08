// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Path helpers shared between the cache builder (write side) and the query
/// engine (read side) so the two agree on file names, and so untrusted metadata
/// (namespaces from a <c>.winmd</c>, package Id/Version from
/// <c>project.assets.json</c>) can never be used to escape the cache directory.
/// </summary>
internal static class ApiCachePaths
{
    /// <summary>
    /// Version of the on-disk cache layout. Bump this whenever the *shape* or
    /// *naming* of the files under a package cache directory changes, and also
    /// whenever the *meaning* of what is recorded in them changes: a cache
    /// written by an older layout is otherwise reused and reads as an empty
    /// index rather than an error, and one written with older naming keeps
    /// answering with names this build no longer produces. <see cref="PackageMeta.Format"/>
    /// records the version a cache was written with, and the builder refuses to
    /// reuse a package whose recorded version is not this one.
    /// </summary>
    internal const int CacheFormatVersion = 7;

    /// <summary>
    /// File name of the machine-wide "SDK scope" manifest, written as a sibling of
    /// the <c>projects</c> directory (never inside it) so it can never collide with
    /// a real project manifest or show up in <c>find-api projects</c>.
    /// </summary>
    internal const string SdkManifestFileName = "sdk.json";

    /// <summary>Display name used for the SDK scope wherever a project name is shown.</summary>
    internal const string SdkScopeName = "Windows SDK";

    /// <summary>Full path of the SDK-scope manifest under a find-api cache directory.</summary>
    internal static string SdkManifestPath(string cacheDir) => Path.Combine(cacheDir, SdkManifestFileName);

    /// <summary>
    /// Directory holding one package's exported metadata under a find-api cache
    /// directory. The asset-path fingerprint is part of the path rather than only
    /// recorded inside it, because a package id and version do not identify what was
    /// cached: two projects can resolve the same id and version to different files — a
    /// different target framework selecting different compile assets, for instance — and
    /// with one shared directory whichever indexed last silently answers for both.
    /// Rebuilding a project reference rewrites the same paths, so it reuses this
    /// directory instead of orphaning it. Returns <see langword="false"/> when the
    /// untrusted id, version, or key would escape the cache directory.
    /// </summary>
    internal static bool TryPackageCacheDir(string cacheDir, string id, string version, string assetPathKey, out string dir) =>
        TryCombineContained(Path.Combine(cacheDir, "packages"), new[] { id, version, assetPathKey }, out dir);

    /// <summary>
    /// The package cache directory a manifest entry points at.
    /// See <see cref="TryPackageCacheDir(string, string, string, string, out string)"/>.
    /// </summary>
    internal static bool TryPackageCacheDir(string cacheDir, ProjectPackageRef package, out string dir) =>
        TryPackageCacheDir(cacheDir, package.Id, package.Version, package.AssetPathKey, out dir);

    /// <summary>Character count of a <see cref="ShortHash"/>.</summary>
    internal const int ShortHashLength = 8;

    /// <summary>
    /// First 8 hex characters of the SHA-256 of <paramref name="value"/>, used to
    /// make otherwise-lossy cache file and directory names injective.
    /// </summary>
    internal static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..ShortHashLength].ToLowerInvariant();

    /// <summary>
    /// Maps a namespace (or the <c>_GlobalNamespace</c> sentinel) to its
    /// per-namespace types file name. Dots become underscores; any remaining
    /// path-significant character is neutralized so a crafted namespace such as
    /// <c>..\..\evil</c> can't traverse out of the <c>types</c> directory.
    /// <para>
    /// That sanitizing is lossy — <c>A.B</c> and <c>A_B</c> both reduce to
    /// <c>A_B</c>, and Windows file names are case-insensitive on top of that —
    /// so a hash of the exact namespace is appended to keep the mapping
    /// injective. Without it, two real namespaces share one file, the parallel
    /// export overwrites one with the other, and later queries silently omit a
    /// namespace or answer with the wrong one's types.
    /// </para>
    /// </summary>
    internal static string NamespaceFileName(string ns)
    {
        string name = ns.Replace('.', '_');
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        // The hash covers the full namespace, so truncating the readable part
        // (which only exists to make the cache browsable) stays collision-safe
        // while keeping the path within the file-name length limit.
        if (name.Length > 100)
        {
            name = name[..100];
        }
        return name + "_" + ShortHash(ns) + ".json";
    }

    /// <summary>
    /// Combines <paramref name="segments"/> under <paramref name="root"/> and
    /// verifies the fully-resolved result stays within <paramref name="root"/>.
    /// Returns <see langword="false"/> (and an empty path) when the segments
    /// would escape the root, guarding against traversal via untrusted package
    /// Id/Version values used as directory names.
    /// </summary>
    internal static bool TryCombineContained(string root, string[] segments, out string combined)
    {
        string rootFull = Path.GetFullPath(root);
        var all = new string[segments.Length + 1];
        all[0] = rootFull;
        Array.Copy(segments, 0, all, 1, segments.Length);
        combined = Path.GetFullPath(Path.Combine(all));

        string prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase) ||
            combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        combined = string.Empty;
        return false;
    }
}
