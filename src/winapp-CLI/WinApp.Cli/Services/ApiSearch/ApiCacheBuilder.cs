// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Builds and refreshes the on-disk API metadata cache: discovers a project's
/// referenced <c>.winmd</c>/<c>.dll</c> metadata, parses it (plus XML docs),
/// and writes structured JSON per package under the cache directory. Ported
/// from the standalone <c>winmd update</c> flow; progress is reported through a
/// callback so it never touches stdout/stderr directly.
/// </summary>
internal static class ApiCacheBuilder
{
    public static ApiRefreshOutput BuildCache(
        string projectDir,
        string cacheDir,
        bool scan,
        string? winAppSdkRuntimePath,
        Action<string>? onProgress = null,
        bool force = false)
    {
        string fullDir = Path.GetFullPath(projectDir);
        winAppSdkRuntimePath ??= DetectWinAppSdkRuntime();

        List<string> projectFiles = DiscoverProjectFiles(fullDir, scan);
        int parsed = 0, reused = 0, processed = 0;
        var projectNames = new List<string>();

        // Progress is reported from worker threads during the parallel export
        // phase, so serialize it — the console sink behind it is not thread-safe.
        object progressLock = new();
        Action<string>? report = onProgress is null
            ? null
            : msg =>
            {
                lock (progressLock)
                {
                    onProgress(msg);
                }
            };

        // Packages are commonly shared between projects (Windows SDK, WinAppSDK),
        // so resolve every project first and collect the distinct packages that
        // still need parsing. They are then exported in parallel below, and each
        // one is parsed only once per run no matter how many projects use it.
        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingManifests = new List<(string Name, ProjectManifest Manifest)>();

        foreach (string projectFile in projectFiles)
        {
            string dir = Path.GetDirectoryName(projectFile)!;
            string projectName = ProjectNameFor(projectFile);
            report?.Invoke($"Indexing {projectName}…");

            // Everything the resolver sends through this callback is a caveat about what
            // the index can answer for, so it is both reported now and kept for the
            // queries that will be served from the cache later.
            var caveats = new List<string>();
            void collect(string message)
            {
                caveats.Add(message);
                report?.Invoke(message);
            }

            List<PackageWithWinMd> packages = NuGetResolver.FindPackagesWithWinMd(dir, projectFile, winAppSdkRuntimePath, collect);
            if (packages.Count == 0)
            {
                continue;
            }
            processed++;
            projectNames.Add(projectName);

            List<ProjectPackageRef> packageRefs = ResolvePackageExports(
                packages, cacheDir, force, pendingExports, seenPackageDirs, ref reused, report);

            var manifest = new ProjectManifest
            {
                ProjectName = projectName,
                ProjectDir = Path.GetFullPath(dir),
                ProjectFile = Path.GetFileName(projectFile),
                Packages = packageRefs,
                Caveats = caveats.Count > 0 ? caveats : null,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            pendingManifests.Add((ManifestName(projectFile), manifest));
        }

        if (pendingExports.Count > 0)
        {
            report?.Invoke($"Parsing {pendingExports.Count} package(s)…");
            var failures = new ConcurrentBag<(string Key, string Message)>();
            var unrecorded = new ConcurrentBag<string>();
            Parallel.ForEach(pendingExports, entry =>
            {
                if (TryExportPackageCache(entry.Value, entry.Key, failures, unrecorded))
                {
                    Interlocked.Increment(ref parsed);
                }
            });
            ReportFailures(failures, report);
            DropUnrecordedPackages(pendingManifests, unrecorded);
        }

        // Manifests are written last so a project is only ever advertised as indexed
        // once the package caches it points at are actually on disk.
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (pendingManifests.Count > 0)
        {
            Directory.CreateDirectory(projectsDir);
            foreach ((string manifestName, ProjectManifest manifest) in pendingManifests)
            {
                WriteFileAtomic(
                    Path.Combine(projectsDir, manifestName + ".json"),
                    JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
            }
        }

        return new ApiRefreshOutput
        {
            ProjectsProcessed = processed,
            PackagesParsed = parsed,
            PackagesReused = reused,
            ProjectNames = projectNames,
        };
    }

    /// <summary>
    /// Decides, for each package, whether its cache can be reused or must be
    /// exported, accumulating the work into <paramref name="pendingExports"/> so
    /// every distinct package is parsed at most once per run. Returns the package
    /// references to record in the owning manifest.
    /// </summary>
    internal static List<ProjectPackageRef> ResolvePackageExports(
        List<PackageWithWinMd> packages,
        string cacheDir,
        bool force,
        Dictionary<string, PackageWithWinMd> pendingExports,
        HashSet<string> seenPackageDirs,
        ref int reused,
        Action<string>? report)
    {
        var packageRefs = new List<ProjectPackageRef>();
        foreach (PackageWithWinMd package in packages)
        {
            string sourceStamp = ComputeSourceStamp(package);
            string assetPathKey = ComputeAssetPathKey(package);
            if (!ApiCachePaths.TryPackageCacheDir(cacheDir, package.Id, package.Version, assetPathKey, out string packageCacheDir))
            {
                // Untrusted Id/Version would escape the cache dir — skip it.
                report?.Invoke($"Skipping package with unsafe path: {package.Id} {package.Version}");
                continue;
            }
            // A project reference is exported with version "local" and can change
            // without a version bump, so its cache is never safe to reuse. An
            // explicit refresh (force) rebuilds every package.
            bool mustRebuild = force || string.Equals(package.Version, "local", StringComparison.OrdinalIgnoreCase);
            if (!seenPackageDirs.Add(packageCacheDir))
            {
                // Already resolved for an earlier project in this run.
                reused++;
            }
            else if (!mustRebuild && IsReusableCache(packageCacheDir, sourceStamp))
            {
                reused++;
            }
            else
            {
                pendingExports[packageCacheDir] = package;
            }
            packageRefs.Add(new ProjectPackageRef
            {
                Id = package.Id,
                Version = package.Version,
                SourceStamp = sourceStamp,
                AssetPathKey = assetPathKey,
            });
            PruneStalePackageCaches(packageCacheDir);
        }
        return packageRefs;
    }

    /// <summary>
    /// Deletes cache directories left beside <paramref name="keptPackageCacheDir"/> by an
    /// older cache layout, so upgrading does not strand exports nothing will ever read
    /// again.
    /// <para>
    /// A sibling whose <c>meta.json</c> records the current
    /// <see cref="ApiCachePaths.CacheFormatVersion"/> is left alone: it is another
    /// project's genuinely different asset selection for the same package version, and
    /// deleting it would make two projects evict each other on every refresh.
    /// </para>
    /// <para>
    /// A key-shaped sibling with no readable <c>meta.json</c> is also left alone. Only a
    /// finished export writes that file, so the directory belongs either to an export
    /// another process is running right now — the explicit refresh path does not hold the
    /// cache lock — or to a run that crashed. Deleting it would pull a live export out
    /// from under a concurrent process, whereas keeping it costs disk space only: the
    /// reuse check rejects a cache with no <c>meta.json</c>, so the next run rebuilds it
    /// in place.
    /// </para>
    /// </summary>
    private static void PruneStalePackageCaches(string keptPackageCacheDir)
    {
        string? versionDir = Path.GetDirectoryName(keptPackageCacheDir);
        if (versionDir is null)
        {
            return;
        }
        try
        {
            foreach (string sibling in Directory.EnumerateDirectories(versionDir))
            {
                if (string.Equals(sibling, keptPackageCacheDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (IsPackageKeyDirectoryName(Path.GetFileName(sibling)))
                {
                    int? format = CacheFormatOf(sibling);
                    if (format is null || format == ApiCachePaths.CacheFormatVersion)
                    {
                        continue;
                    }
                }
                Directory.Delete(sibling, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is opportunistic: a locked or unreadable leftover directory costs
            // disk space, not correctness, so it must never fail the refresh.
        }
    }

    /// <summary>
    /// Whether a directory name is one this layout mints — an
    /// <see cref="ApiCachePaths.ShortHash"/> of the asset paths. Anything else beside a
    /// package cache directory was left by an earlier layout that wrote a package's
    /// contents straight into the version directory.
    /// </summary>
    private static bool IsPackageKeyDirectoryName(string name) =>
        name.Length == ApiCachePaths.ShortHashLength && name.All(Uri.IsHexDigit);

    /// <summary>
    /// The <see cref="ApiCachePaths.CacheFormatVersion"/> recorded in a package cache
    /// directory, or <see langword="null"/> when it has no readable <c>meta.json</c>.
    /// </summary>
    private static int? CacheFormatOf(string packageCacheDir)
    {
        string metaPath = Path.Combine(packageCacheDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(metaPath), ApiSearchJsonContext.Default.PackageMeta)?.Format;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an existing package cache can be reused as-is. A cache written by an
    /// older layout is *not* reusable: the file naming it used no longer matches what
    /// the query side looks for, so reusing it would read as an empty index — a silent
    /// wrong answer — rather than an error. It is also not reusable when the previous
    /// run failed to parse one of its metadata files, so a transient read failure heals
    /// on the next refresh instead of being cached forever, or when the metadata files
    /// it was built from have changed (see <see cref="ComputeSourceStamp"/>).
    /// </summary>
    private static bool IsReusableCache(string packageCacheDir, string expectedSourceStamp)
    {
        string metaPath = Path.Combine(packageCacheDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }
        try
        {
            PackageMeta? meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), ApiSearchJsonContext.Default.PackageMeta);
            return meta is { Incomplete: false }
                && meta.Format == ApiCachePaths.CacheFormatVersion
                && string.Equals(meta.SourceStamp, expectedSourceStamp, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable or malformed meta.json reads as "no usable cache", so the
            // package is simply re-exported.
            return false;
        }
    }

    /// <summary>
    /// A short fingerprint of *which* metadata files a package resolved to — their paths
    /// only, hashed. This names the cache directory.
    /// <para>
    /// Paths and not write times, deliberately. A package id and version alone do not
    /// identify what is cached: two projects on different target frameworks select
    /// different assets from the same package version, and with one shared directory
    /// whichever indexed last silently answers for both. Including write times would
    /// separate those correctly too, but it would also mint a fresh directory every time
    /// a referenced project is rebuilt, orphaning the previous one forever. Asset paths
    /// are stable across rebuilds, so the common case reuses one directory and only a
    /// genuinely different asset selection gets its own.
    /// </para>
    /// <para>
    /// Content changes at the same paths are caught by <see cref="ComputeSourceStamp"/>,
    /// which re-exports into this same directory rather than beside it.
    /// </para>
    /// </summary>
    private static string ComputeAssetPathKey(PackageWithWinMd package)
    {
        var builder = new StringBuilder();
        foreach (string file in SortedAssetFiles(package))
        {
            builder.Append(file).Append(';');
        }
        return ApiCachePaths.ShortHash(builder.ToString());
    }

    /// <summary>
    /// A short fingerprint of the metadata files a package actually resolved to — their
    /// paths, sizes, and write times, hashed to roughly twenty characters so the cache
    /// does not grow a per-file record. Recorded in <c>meta.json</c> and compared on
    /// reuse: rebuilding a referenced project rewrites its <c>.winmd</c> in place at the
    /// same path, and comparing the fingerprint re-exports it instead of answering from
    /// stale metadata.
    /// </summary>
    private static string ComputeSourceStamp(PackageWithWinMd package)
    {
        var builder = new StringBuilder();
        foreach (string file in SortedAssetFiles(package))
        {
            builder.Append(file);
            try
            {
                var info = new FileInfo(file);
                builder.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable input cannot be fingerprinted, so it never compares equal
                // and the package is re-exported rather than trusted.
                builder.Append("|unreadable|").Append(Guid.NewGuid().ToString("N"));
            }
            builder.Append(';');
        }
        return ApiCachePaths.ShortHash(builder.ToString());
    }

    /// <summary>Every metadata file a package resolved to, in a stable order.</summary>
    private static IEnumerable<string> SortedAssetFiles(PackageWithWinMd package) =>
        package.WinMdFiles.Concat(package.XmlDocFiles).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the machine-wide SDK scope from <paramref name="sdkPackages"/> (the
    /// Windows SDK UnionMetadata and the installed WinAppSDK runtime), which need
    /// no project on disk. The manifest is written to <c>sdk.json</c> alongside —
    /// never inside — the <c>projects</c> directory, and, as with project
    /// manifests, only after the package caches it points at are on disk.
    /// </summary>
    public static ApiRefreshOutput BuildSdkCache(
        string cacheDir,
        List<PackageWithWinMd> sdkPackages,
        Action<string>? onProgress = null,
        bool force = false)
    {
        if (sdkPackages.Count == 0)
        {
            return new ApiRefreshOutput
            {
                ProjectsProcessed = 0,
                PackagesParsed = 0,
                PackagesReused = 0,
                ProjectNames = [],
            };
        }

        int parsed = 0, reused = 0;
        object progressLock = new();
        Action<string>? report = onProgress is null
            ? null
            : msg =>
            {
                lock (progressLock)
                {
                    onProgress(msg);
                }
            };

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        report?.Invoke($"Indexing {ApiCachePaths.SdkScopeName}…");
        List<ProjectPackageRef> packageRefs = ResolvePackageExports(
            sdkPackages, cacheDir, force, pendingExports, seenPackageDirs, ref reused, report);

        if (pendingExports.Count > 0)
        {
            report?.Invoke($"Parsing {pendingExports.Count} package(s)…");
            var failures = new ConcurrentBag<(string Key, string Message)>();
            var unrecorded = new ConcurrentBag<string>();
            Parallel.ForEach(pendingExports, entry =>
            {
                if (TryExportPackageCache(entry.Value, entry.Key, failures, unrecorded))
                {
                    Interlocked.Increment(ref parsed);
                }
            });
            ReportFailures(failures, report);
            var unrecordedKeys = new HashSet<string>(unrecorded, StringComparer.OrdinalIgnoreCase);
            packageRefs.RemoveAll(p => unrecordedKeys.Contains(PackageKey(p.Id, p.Version)));
        }

        var manifest = new ProjectManifest
        {
            ProjectName = ApiCachePaths.SdkScopeName,
            ProjectDir = string.Empty,
            ProjectFile = string.Empty,
            Packages = packageRefs,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        Directory.CreateDirectory(cacheDir);
        WriteFileAtomic(
            ApiCachePaths.SdkManifestPath(cacheDir),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

        return new ApiRefreshOutput
        {
            ProjectsProcessed = 1,
            PackagesParsed = parsed,
            PackagesReused = reused,
            ProjectNames = [ApiCachePaths.SdkScopeName],
        };
    }

    /// <summary>
    /// Exports one package, turning a metadata read failure into a reported skip.
    /// Files can become unreadable while a run is in progress — deleted, locked by a
    /// build, or denied — and the export work is parallel, so without this the failure
    /// surfaces as an unhandled <see cref="AggregateException"/> that ends a query the
    /// user did not know was indexing. The package is recorded as incomplete rather
    /// than left absent, so a later query can say the index is partial instead of
    /// answering "no such type" from metadata it never read.
    /// </summary>
    private static bool TryExportPackageCache(
        PackageWithWinMd package,
        string cacheDir,
        ConcurrentBag<(string Key, string Message)> failures,
        ConcurrentBag<string> unrecorded)
    {
        try
        {
            ExportPackageCache(package, cacheDir);
            return true;
        }
        catch (Exception ex) when (IsPackageReadFailure(ex))
        {
            string reason = Unwrap(ex).Message;
            failures.Add((PackageKey(package.Id, package.Version), $"Skipped {package.Id} {package.Version}: {reason}"));
            if (!TryMarkPackageIncomplete(package, cacheDir, reason))
            {
                unrecorded.Add(PackageKey(package.Id, package.Version));
            }
            return false;
        }
    }

    /// <summary>
    /// Records a failed export as an incomplete package cache. A query qualifies every
    /// negative answer with the packages it could not fully read, so a package left with
    /// no cache at all turns "that package was never read" into a confident "no such
    /// type". The marker also keeps the index from looking half-written: it names a
    /// current-format cache, so the read path does not decide the whole project is stale
    /// and re-index on every query, while <see cref="IsReusableCache"/> still refuses to
    /// reuse an incomplete cache, so the next index pass retries the package. Returns
    /// false when even the marker cannot be written, leaving the caller to drop the
    /// reference instead.
    /// </summary>
    private static bool TryMarkPackageIncomplete(PackageWithWinMd package, string cacheDir, string reason)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            var meta = new PackageMeta
            {
                Format = ApiCachePaths.CacheFormatVersion,
                PackageId = package.Id,
                Version = package.Version,
                Incomplete = true,
                ParseErrors = [reason],
                WinMdFiles = package.WinMdFiles.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                TotalTypes = 0,
                TotalMembers = 0,
                TotalNamespaces = 0,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            WriteFileAtomic(
                Path.Combine(cacheDir, "meta.json"),
                JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PackageKey(string id, string version) => id + "/" + version;

    /// <summary>
    /// Drops the packages whose failure could not even be recorded from the manifests
    /// about to be written. A manifest names the package caches a query may read, so
    /// advertising one that has no <c>meta.json</c> makes every later query see a
    /// missing cache, decide the index is stale, and re-index — permanently, if the
    /// failure is permanent.
    /// </summary>
    private static void DropUnrecordedPackages(
        List<(string Name, ProjectManifest Manifest)> manifests,
        ConcurrentBag<string> unrecorded)
    {
        if (unrecorded.IsEmpty)
        {
            return;
        }
        var failed = new HashSet<string>(unrecorded, StringComparer.OrdinalIgnoreCase);
        foreach ((_, ProjectManifest manifest) in manifests)
        {
            manifest.Packages.RemoveAll(p => failed.Contains(PackageKey(p.Id, p.Version)));
        }
    }

    private static bool IsPackageReadFailure(Exception ex) => ex switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsPackageReadFailure),
        IOException or UnauthorizedAccessException or JsonException => true,
        _ => false,
    };

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: > 0 } aggregate ? Unwrap(aggregate.InnerExceptions[0]) : ex;

    private static void ReportFailures(ConcurrentBag<(string Key, string Message)> failures, Action<string>? report)
    {
        if (report == null)
        {
            return;
        }
        foreach (string message in failures.Select(f => f.Message).OrderBy(m => m, StringComparer.Ordinal))
        {
            report(message);
        }
    }

    private static void ExportPackageCache(PackageWithWinMd package, string cacheDir)
    {
        string typesDir = Path.Combine(cacheDir, "types");
        Directory.CreateDirectory(typesDir);

        var types = new List<WinMdTypeInfo>();
        var parseErrors = new List<string>();
        if (package.WinMdFiles.Count == 1)
        {
            WinMdParser.WinMdParseResult single = WinMdParser.ParseFile(package.WinMdFiles[0]);
            types.AddRange(single.Types);
            if (single.Error is not null)
            {
                parseErrors.Add($"{Path.GetFileName(package.WinMdFiles[0])}: {single.Error}");
            }
        }
        else if (package.WinMdFiles.Count > 1)
        {
            // Parsing is CPU-bound and each metadata file is independent, so fan
            // out and reassemble in file order to keep the output deterministic.
            var perFile = new WinMdParser.WinMdParseResult[package.WinMdFiles.Count];
            Parallel.For(0, package.WinMdFiles.Count, i => perFile[i] = WinMdParser.ParseFile(package.WinMdFiles[i]));
            for (int i = 0; i < perFile.Length; i++)
            {
                types.AddRange(perFile[i].Types);
                if (perFile[i].Error is not null)
                {
                    parseErrors.Add($"{Path.GetFileName(package.WinMdFiles[i])}: {perFile[i].Error}");
                }
            }
        }

        if (package.XmlDocFiles.Count > 0)
        {
            var perFileDocs = new Dictionary<string, string>[package.XmlDocFiles.Count];
            Parallel.For(0, package.XmlDocFiles.Count, i => perFileDocs[i] = XmlDocParser.ParseFile(package.XmlDocFiles[i]));

            var docs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Dictionary<string, string> fileDocs in perFileDocs)
            {
                foreach (var kvp in fileDocs)
                {
                    docs.TryAdd(kvp.Key, kvp.Value);
                }
            }
            if (docs.Count > 0)
            {
                XmlDocParser.MergeDescriptions(types, docs);
            }
        }

        var byNamespace = types
            .GroupBy(t => t.Namespace)
            .ToDictionary(g => g.Key, g => g.ToList());
        var namespaceNames = byNamespace.Keys
            .Where(ns => !string.IsNullOrEmpty(ns))
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();
        if (byNamespace.TryGetValue(string.Empty, out var globalTypes) && globalTypes.Count > 0)
        {
            namespaceNames.Insert(0, "_GlobalNamespace");
        }

        var meta = new PackageMeta
        {
            Format = ApiCachePaths.CacheFormatVersion,
            PackageId = package.Id,
            Version = package.Version,
            SourceStamp = ComputeSourceStamp(package),
            WinMdFiles = package.WinMdFiles.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TotalTypes = types.Count,
            TotalMembers = types.Sum(t => t.Members.Count),
            TotalNamespaces = namespaceNames.Count,
            // Recorded so the query side can qualify a "not found" answer: a file that
            // failed to parse contributed no types, and without this marker a missing
            // API is indistinguishable from one that was simply never indexed.
            Incomplete = parseErrors.Count > 0,
            ParseErrors = parseErrors.Count > 0 ? parseErrors : null,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        WriteFileAtomic(Path.Combine(cacheDir, "namespaces.json"), JsonSerializer.Serialize(namespaceNames, ApiSearchJsonContext.Default.ListString));

        Parallel.ForEach(namespaceNames, ns =>
        {
            string key = ns == "_GlobalNamespace" ? string.Empty : ns;
            List<WinMdTypeInfo> namespaceTypes = byNamespace[key];
            string fileName = ApiCachePaths.NamespaceFileName(ns);
            WriteFileAtomic(Path.Combine(typesDir, fileName), JsonSerializer.Serialize(namespaceTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        });

        // meta.json is the cache-reuse sentinel, so it is written last — its presence
        // then means the namespace and type payloads it describes are already on disk.
        WriteFileAtomic(Path.Combine(cacheDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
    }

    /// <summary>
    /// Writes content atomically (temp file + rename) so readers never see partial writes.
    /// There is deliberately no direct-write fallback: <see cref="File.WriteAllText(string, string)"/>
    /// follows a symlink planted at the destination, whereas the staged rename replaces it,
    /// and cache writes are already serialized by the cache lock — so a fallback would only
    /// ever run in the case where it is unsafe.
    /// </summary>
    private static void WriteFileAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        PathSafety.AtomicWriteAllText(path, content);
    }

    /// <summary>
    /// The file that stands in for an MSBuild project in a project that has none —
    /// an Electron or other non-.NET app whose Windows metadata is declared in
    /// <c>winapp.yaml</c> and resolved into <c>.winapp/winmds.lock.json</c>.
    /// </summary>
    internal const string WinappConfigFileName = "winapp.yaml";

    /// <summary>Whether a discovered project file is a <c>winapp.yaml</c> stand-in.</summary>
    internal static bool IsWinappConfigProject(string projectFile) =>
        Path.GetFileName(projectFile).Equals(WinappConfigFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>winapp.yaml</c> project has no project name of its own, so it takes the
    /// name of the directory it sits in — which is the app's name for an Electron or
    /// similar project. <c>winapp</c> (the file's own stem) would name every such
    /// project identically.
    /// </summary>
    internal static string ProjectNameFor(string projectFile) =>
        IsWinappConfigProject(projectFile)
            ? new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectFile))!).Name
            : Path.GetFileNameWithoutExtension(projectFile);

    internal static List<string> DiscoverProjectFiles(string inputPath, bool scan)
    {
        var results = new List<string>();
        if (scan)
        {
            if (!Directory.Exists(inputPath))
            {
                return results;
            }
            // Prune the excluded trees before descending into them rather than
            // filtering their files out afterwards. In an Electron repository
            // 'node_modules' holds the overwhelming majority of the directories on
            // disk and contains no project this indexes, so walking it — once per
            // pattern — made 'refresh --scan' scale with installed dependencies
            // instead of with project sources.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
            };
            var enumerable = new FileSystemEnumerable<string>(
                inputPath,
                // Matches what Directory.EnumerateFiles returns: the path rooted at the
                // string the caller passed. ToFullPath would canonicalize the root
                // instead, so results would stop comparing equal to paths built from
                // the caller's own input.
                (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
                options)
            {
                // Reparse points are never followed. A symlink or junction checked into a
                // repository can redirect an ordinary-looking subdirectory onto an SMB
                // share, and reading a project file from there authenticates to whatever
                // host answers. A legitimately linked source tree is still indexable by
                // pointing --project-dir at its real location.
                ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                    !entry.IsDirectory
                    && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && IsDiscoverableProjectFile(entry.FileName),
                ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                    !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && !IsExcludedScanDirectory(entry.FileName),
            };
            return enumerable
                .Where(f => !IsWinappConfigProject(f) || !DirectoryHasMsBuildProject(Path.GetDirectoryName(f)!))
                .ToList();
        }
        if (File.Exists(inputPath) && (inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || inputPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(inputPath);
        }
        else if (Directory.Exists(inputPath))
        {
            results.AddRange(Directory.GetFiles(inputPath, "*.csproj"));
            results.AddRange(Directory.GetFiles(inputPath, "*.vcxproj"));
            if (results.Count == 0 && FindSolutionFileInDir(inputPath) is { } solutionFile)
            {
                // A solution directory holds no project of its own, so indexing it as
                // given records nothing and every query typed there falls back to the
                // SDK scope. The projects the solution builds live below it — and the
                // solution, not the directory tree, is what says which those are. A
                // sibling directory can hold a project the solution deliberately
                // excludes; indexing it makes the caller disambiguate against a project
                // their solution does not build.
                return DiscoverSolutionProjectFiles(solutionFile, inputPath);
            }
            // Only when the directory builds nothing MSBuild understands: a project
            // that has both is a .NET project that happens to use winapp.yaml for its
            // SDK packages, and its .csproj is the more precise description of what it
            // compiles against.
            if (results.Count == 0)
            {
                string winappConfig = Path.Combine(inputPath, WinappConfigFileName);
                if (File.Exists(winappConfig))
                {
                    results.Add(winappConfig);
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Directory names a recursive scan never descends into: they hold build output
    /// and installed dependencies, never a project this should index.
    /// </summary>
    private static bool IsExcludedScanDirectory(ReadOnlySpan<char> name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    /// <summary>File names a recursive scan treats as a project to index.</summary>
    private static bool IsDiscoverableProjectFile(ReadOnlySpan<char> name) =>
        name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)
        || name.Equals(WinappConfigFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The projects a solution lists that this indexer can read, as absolute paths.
    /// Falls back to a recursive scan when the solution lists nothing readable — an
    /// unparseable or empty solution means "membership unknown", and scanning is a
    /// better answer there than indexing nothing at all.
    /// </summary>
    private static List<string> DiscoverSolutionProjectFiles(string solutionFile, string solutionDir)
    {
        List<string> listed = SolutionProjectReader.ReadProjectPaths(solutionFile)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToList();

        return listed.Count > 0 ? listed : DiscoverProjectFiles(solutionDir, scan: true);
    }

    /// <summary>Whether a directory contains a <c>.csproj</c> or <c>.vcxproj</c>.</summary>
    private static bool DirectoryHasMsBuildProject(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.csproj").Any()
                || Directory.EnumerateFiles(dir, "*.vcxproj").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns the first solution file (<c>.sln</c>/<c>.slnx</c>) in a directory, or null.</summary>
    internal static string? FindSolutionFileInDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        try
        {
            return Directory.EnumerateFiles(dir, "*.sln*")
                .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cache file name for a project's manifest. The project's full path is hashed
    /// into the name so that identically-named projects in different directories
    /// (a monorepo, or the same template scaffolded repeatedly) each get their own
    /// manifest instead of silently overwriting one another.
    /// </summary>
    internal static string ManifestName(string projectFile)
    {
        string fullPath = Path.GetFullPath(projectFile);
        return ProjectNameFor(fullPath) + "_" + ApiCachePaths.ShortHash(fullPath);
    }

    /// <summary>
    /// Returns the name of the project in a directory (<c>.csproj</c>/<c>.vcxproj</c>,
    /// or a <c>winapp.yaml</c> project's directory name), or null.
    /// </summary>
    internal static string? FindProjectNameInDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        string[] projectFiles = Directory.GetFiles(dir, "*.csproj").Concat(Directory.GetFiles(dir, "*.vcxproj")).ToArray();
        if (projectFiles.Length > 0)
        {
            return Path.GetFileNameWithoutExtension(projectFiles[0]);
        }
        string winappConfig = Path.Combine(dir, WinappConfigFileName);
        return File.Exists(winappConfig) ? ProjectNameFor(winappConfig) : null;
    }

    /// <summary>
    /// Best-effort discovery of the installed WinAppSDK runtime path via
    /// <c>Get-AppxPackage</c>, used to index framework-dependent apps whose
    /// <c>.winmd</c> files live in the runtime rather than a NuGet package.
    /// Failures are non-fatal — NuGet and Windows SDK metadata still index.
    /// </summary>
    /// <summary>
    /// Selects the newest installed Windows App Runtime for the current architecture.
    /// </summary>
    /// <remarks>
    /// Sorting on the raw Appx <c>Version</c> picks the wrong runtime, because the two
    /// release lines number their packages differently: <c>Microsoft.WindowsAppRuntime.1.8</c>
    /// reports <c>8000.946.1701.0</c> while the newer <c>Microsoft.WindowsAppRuntime.2</c>
    /// reports <c>2.4.0.0</c>, so a descending Version sort ranks 1.8 far above 2.4.
    /// The release identity therefore comes from the package name: a
    /// <c>major.minor</c> suffix is the release outright, while a major-only suffix
    /// takes its minor from the package version. Stable beats experimental within a
    /// release, and experimental is still used when it is all that is installed.
    /// </remarks>
    private const string NewestRuntimeScript =
        "Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' | " +
        "Where-Object { $_.Name -notmatch 'CBS' -and $_.Architecture -eq '{ARCH}' } | " +
        "ForEach-Object { " +
            "$suffix = $_.Name -replace '^Microsoft\\.WindowsAppRuntime\\.',''; " +
            "$core = ($suffix -split '-')[0]; " +
            "$v = [version]$_.Version; " +
            "if ($core -match '^\\d+\\.\\d+$') { $rel = [version]$core } " +
            "elseif ($core -match '^\\d+$') { $rel = [version]('{0}.{1}' -f $v.Major, $v.Minor) } " +
            "else { $rel = [version]'0.0' }; " +
            "[pscustomobject]@{ Rel = $rel; Stable = [int]($suffix -notmatch '-'); Ver = $v; Path = $_.InstallLocation } " +
        "} | " +
        "Sort-Object Rel, Stable, Ver -Descending | " +
        "Select-Object -First 1 -ExpandProperty Path";

    internal static string? DetectWinAppSdkRuntime()
    {
        try
        {
            string arch = RuntimeInformation.OSArchitecture.ToString();
            // Use the absolute, well-known Windows PowerShell path so a
            // "powershell.exe" planted in the project directory (or anywhere on
            // PATH) can't be executed instead.
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string powershellPath = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershellPath))
            {
                return null;
            }
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = "-NoProfile -Command \"" + NewestRuntimeScript.Replace("{ARCH}", arch, StringComparison.Ordinal) + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                return null;
            }

            // Both streams are drained asynchronously *before* waiting. Reading
            // stdout synchronously would block until the process exits — making the
            // timeout below unreachable — and leaving stderr undrained lets a chatty
            // or hung query fill its pipe and deadlock the very first index build.
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (stdout)
                    {
                        stdout.AppendLine(e.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(15000))
            {
                KillProcessTree(process);
                return null;
            }
            // Second, untimed wait: it is what flushes the async output handlers, so
            // the buffer below is complete rather than racing the last callback.
            process.WaitForExit();

            string output;
            lock (stdout)
            {
                output = stdout.ToString().Trim();
            }
            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                return output;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Probing for an installed WindowsAppRuntime is best-effort: if PowerShell
            // cannot be launched or the query fails, report "not found" and let the
            // caller fall back to the packages it already resolved.
        }
        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or AggregateException)
        {
            // The process already exited, or the OS refused the kill. Either way the
            // caller only needs "no runtime detected".
        }
    }
}
