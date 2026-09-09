// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Services;

/// <summary>
/// A caller-supplied scope for an API metadata query: an optional project
/// directory (defaults to the current directory) and an optional project name
/// used to disambiguate when several projects are indexed.
/// </summary>
internal readonly record struct ApiRequestScope(string? ProjectDir, string? Project);

/// <summary>
/// The winapp-native façade over the ported API-metadata engine that backs
/// <c>winapp find-api</c>. It owns cache-directory resolution (under the managed
/// global <c>.winapp</c> directory), lazy auto-indexing when a project's
/// <c>project.assets.json</c> is newer than the cached manifest, project-manifest
/// resolution (honoring <c>--project</c>/<c>--project-dir</c>), and forwards to
/// <see cref="ApiQueryEngine"/>. Query methods never write to stdout — they
/// return <see cref="ApiQueryResult{T}"/> so the command renders text or JSON.
/// </summary>
internal interface IApiMetadataService
{
    ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope);

    ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null, bool includeAll = false);

    /// <summary>Lists members for several types, reading the index once for the whole batch.</summary>
    List<(string Type, ApiQueryResult<ApiMembersOutput> Result)> MembersBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null, bool includeAll = false);

    ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope);

    /// <summary>
    /// Checks several properties of one type, reading the index once for the whole batch.
    /// </summary>
    List<(string Property, ApiQueryResult<ApiCheckPropertyOutput> Result)> CheckProperties(
        string typeName, IReadOnlyList<string> propertyNames, ApiRequestScope scope);

    ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope);

    ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null);

    /// <summary>Lists values for several enums, reading the index once for the whole batch.</summary>
    List<(string Type, ApiQueryResult<ApiEnumsOutput> Result)> EnumsBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null);

    ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope);

    ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope);

    ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope);

    ApiProjectsOutput Projects();

    ApiQueryResult<ApiRefreshOutput> Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false);
}

internal sealed class ApiMetadataService(
    IWinappDirectoryService directoryService,
    ICurrentDirectoryProvider currentDirectory,
    ISdkPackageSource sdkPackages,
    ILogger<ApiMetadataService> logger) : IApiMetadataService
{
    private string GetCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "find-api");

    public ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Search(query, maxResults, cacheDir, manifest));

    public ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null, bool includeAll = false) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Members(fullName, filter, cacheDir, manifest, includeAll));

    public List<(string Type, ApiQueryResult<ApiMembersOutput> Result)> MembersBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null, bool includeAll = false) =>
        WithManifestBatch(scope, fullNames, (cacheDir, manifest) => ApiQueryEngine.MembersBatch(fullNames, filter, cacheDir, manifest, includeAll));

    public ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.CheckProperty(typeName, propertyName, cacheDir, manifest));

    public List<(string Property, ApiQueryResult<ApiCheckPropertyOutput> Result)> CheckProperties(
        string typeName, IReadOnlyList<string> propertyNames, ApiRequestScope scope) =>
        WithManifestBatch(scope, propertyNames, (cacheDir, manifest) => ApiQueryEngine.CheckProperties(typeName, propertyNames, cacheDir, manifest));

    public ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Types(ns, cacheDir, manifest));

    public ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Enums(fullName, filter, cacheDir, manifest));

    public List<(string Type, ApiQueryResult<ApiEnumsOutput> Result)> EnumsBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null) =>
        WithManifestBatch(scope, fullNames, (cacheDir, manifest) => ApiQueryEngine.EnumsBatch(fullNames, filter, cacheDir, manifest));

    public ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Namespaces(filter, cacheDir, manifest));

    public ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Packages(cacheDir, manifest));

    public ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Stats(cacheDir, manifest));

    public ApiProjectsOutput Projects() => ApiQueryEngine.Projects(GetCacheDir());

    public ApiQueryResult<ApiRefreshOutput> Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false)
    {
        string cacheDir = GetCacheDir();
        string? runtimePath = ApiCacheBuilder.DetectWinAppSdkRuntime();

        // 'refresh --project sdk' rebuilds the machine-wide scope explicitly (the
        // only way to pick up a newly installed Windows SDK / WinAppSDK runtime).
        if (scope.Project is not null && IsSdkScopeName(scope.Project))
        {
            return ApiQueryResult<ApiRefreshOutput>.Ok(
                ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force: true));
        }

        string projectDir;
        if (scope.Project is not null)
        {
            // A named project that resolves to nothing must fail: refreshing the
            // current directory instead would report success for a project the
            // caller never asked about.
            string? named = TryResolveNamedProjectDir(scope.Project, cacheDir);
            if (named is null)
            {
                return ApiQueryResult<ApiRefreshOutput>.InvalidInput(UnknownProjectMessage(scope.Project, cacheDir));
            }
            projectDir = named;
        }
        else
        {
            projectDir = ResolveProjectDir(scope);

            // Symmetric with the named-project branch above: a --project-dir that does
            // not exist is a typo, and indexing the SDK instead reports success for a
            // scope the caller never asked about — after which every query answers from
            // the SDK and reports the project's own packages as not found.
            if (scope.ProjectDir is not null && !Directory.Exists(projectDir))
            {
                return ApiQueryResult<ApiRefreshOutput>.InvalidInput(
                    $"Project directory not found: '{projectDir}'. Check the path passed to '--project-dir', " +
                    "or use '--project sdk' to refresh the machine-wide Windows SDK scope.");
            }
        }

        ApiRefreshOutput output = ApiCacheBuilder.BuildCache(projectDir, cacheDir, scan, runtimePath, onProgress, force);

        // Nothing to index here means there is no project in this directory, which is
        // exactly when queries fall back to the SDK scope — so build that instead of
        // leaving the user with an index they can't query.
        if (output.ProjectsProcessed == 0)
        {
            return ApiQueryResult<ApiRefreshOutput>.Ok(
                ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force));
        }
        return ApiQueryResult<ApiRefreshOutput>.Ok(output);
    }

    /// <summary>
    /// Error text for a <c>--project</c> name that matches no indexed project, listing
    /// what is actually indexed so the caller can correct the name.
    /// </summary>
    private static string UnknownProjectMessage(string projectName, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        string[] files = Directory.Exists(projectsDir) ? Directory.GetFiles(projectsDir, "*.json") : [];
        string names = AvailableProjects(files);
        string known = names.Length == 0
            ? "No projects are indexed yet."
            : $"Indexed projects: {names}.";
        return $"No single indexed project matches '{projectName}'. {known} " +
            "Run 'winapp find-api refresh' in the project directory, or pass --project-dir.";
    }

    /// <summary>
    /// Resolve an explicit <c>--project</c> name to the recorded directory of the one
    /// indexed project it names. Returns <c>null</c> when the name matches no indexed
    /// project or several in different directories, so callers fail loudly instead of
    /// silently acting on the current directory.
    /// </summary>
    private static string? TryResolveNamedProjectDir(string projectName, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return null;
        }

        var dirs = new List<string>();
        foreach (string path in Directory.GetFiles(projectsDir, "*.json"))
        {
            if (!IsManifestForProject(path, projectName))
            {
                continue;
            }
            ProjectManifest? manifest = DeserializeManifest(path);
            if (manifest is not null &&
                manifest.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(manifest.ProjectDir))
            {
                dirs.Add(Path.GetFullPath(manifest.ProjectDir));
            }
        }

        var distinct = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// Cheap pre-filter on the manifest file name, which is <c>ProjectName_hash</c>. It is a
    /// superset test only — <c>App</c> also prefixes <c>App_Tests_hash</c> — so every caller
    /// must still compare the deserialized <see cref="ProjectManifest.ProjectName"/>.
    /// </summary>
    private static bool IsManifestForProject(string manifestPath, string projectName)
    {
        string name = Path.GetFileNameWithoutExtension(manifestPath);
        return name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(projectName + "_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shared read path for every query verb: ensure the cache is fresh, resolve
    /// the project manifest, and either forward to the engine or return a
    /// <see cref="ApiQueryOutcome.NoProject"/> result with actionable guidance.
    /// </summary>
    private ApiQueryResult<T> WithManifest<T>(ApiRequestScope scope, Func<string, ProjectManifest, ApiQueryResult<T>> query)
        where T : class
    {
        string cacheDir = GetCacheDir();
        string? indexError = AutoIndexIfStale(scope, cacheDir);
        if (indexError is not null)
        {
            // Answering from a cache we know is stale or absent would look
            // authoritative while being wrong, so surface the failure instead.
            return ApiQueryResult<T>.NoProject(indexError);
        }
        ResolvedScope resolved = ResolveManifest(scope, cacheDir);
        if (resolved.Manifest is null)
        {
            return ApiQueryResult<T>.NoProject(resolved.Error ?? NoProjectMessage);
        }

        ApiQueryResult<T> result = query(cacheDir, resolved.Manifest);

        // Stamp scope and project identity centrally so every verb reports them
        // identically and no payload can silently omit which index answered.
        if (result.Data is IApiScopedOutput scoped)
        {
            StampScope(scoped, resolved);
        }

        // A failure has no payload to stamp, so its caveats are attached to the message
        // instead — otherwise the one answer a caller acts on hardest ("not found") is the
        // one that never says the index was partial.
        return result.WithCaveats(resolved.Manifest.Caveats);
    }

    /// <summary>
    /// Resolves the manifest once and runs a batch query against it, stamping scope onto
    /// every result. A failure before the query applies to the whole batch, so each key
    /// reports the same error rather than the batch collapsing to a single message.
    /// </summary>
    private List<(string Key, ApiQueryResult<T> Result)> WithManifestBatch<T>(
        ApiRequestScope scope,
        IReadOnlyList<string> keys,
        Func<string, ProjectManifest, List<(string Key, ApiQueryResult<T> Result)>> query)
        where T : class
    {
        static List<(string, ApiQueryResult<T>)> FailAll(IReadOnlyList<string> keys, string message)
        {
            var failed = new List<(string, ApiQueryResult<T>)>(keys.Count);
            foreach (string key in keys)
            {
                failed.Add((key, ApiQueryResult<T>.NoProject(message)));
            }
            return failed;
        }

        string cacheDir = GetCacheDir();
        string? indexError = AutoIndexIfStale(scope, cacheDir);
        if (indexError is not null)
        {
            return FailAll(keys, indexError);
        }
        ResolvedScope resolved = ResolveManifest(scope, cacheDir);
        if (resolved.Manifest is null)
        {
            return FailAll(keys, resolved.Error ?? NoProjectMessage);
        }

        var results = query(cacheDir, resolved.Manifest);
        for (int i = 0; i < results.Count; i++)
        {
            (string key, ApiQueryResult<T> result) = results[i];
            if (result.Data is IApiScopedOutput scoped)
            {
                StampScope(scoped, resolved);
                continue;
            }
            results[i] = (key, result.WithCaveats(resolved.Manifest.Caveats));
        }
        return results;
    }

    /// <summary>
    /// Records which index answered. Project names are not unique across directories, so
    /// the directory is the only reliable identity — a caller auditing which project
    /// served a query must not have to infer it from cache timestamps.
    /// </summary>
    private static void StampScope(IApiScopedOutput scoped, ResolvedScope resolved)
    {
        scoped.Scope = resolved.IsSdk ? ApiScopeNames.Sdk : ApiScopeNames.Project;
        scoped.ProjectName = resolved.Manifest!.ProjectName;
        scoped.ProjectDir = string.IsNullOrEmpty(resolved.Manifest.ProjectDir)
            ? null
            : resolved.Manifest.ProjectDir;
        scoped.Caveats = resolved.Manifest.Caveats;
    }

    private string ResolveProjectDir(ApiRequestScope scope) =>
        Path.GetFullPath(scope.ProjectDir ?? currentDirectory.GetCurrentDirectory());

    /// <summary>
    /// Re-index the project when its restore output (<c>project.assets.json</c>)
    /// is newer than the cached manifest, or no manifest exists yet. A file lock
    /// under the cache dir serializes concurrent winapp invocations so two
    /// processes don't index into the same tree at once. Returns an error message
    /// when indexing was required but failed, so the caller can refuse to answer
    /// from a cache it knows is stale.
    /// </summary>
    private string? AutoIndexIfStale(ApiRequestScope scope, string cacheDir)
    {
        // The machine-wide SDK scope is rebuilt only by an explicit refresh; it has
        // no project.assets.json to stale-check against.
        if (scope.Project is not null && IsSdkScopeName(scope.Project))
        {
            return null;
        }

        // A named project is stale-checked against its own recorded directory. Using
        // the current directory here would refresh an unrelated project and leave the
        // requested one stale. An unresolvable name is left to ResolveManifest, which
        // reports the ambiguity or the unknown name properly.
        string projectDir;
        if (scope.Project is not null)
        {
            string? named = TryResolveNamedProjectDir(scope.Project, cacheDir);
            if (named is null)
            {
                return null;
            }
            projectDir = named;
        }
        else
        {
            projectDir = ResolveProjectDir(scope);
        }

        string[] manifestFiles = ManifestFiles(cacheDir);

        // A solution directory has no project and no restore output of its own; the
        // projects it builds live below it. Those are what must be indexed and
        // stale-checked, or a query typed at the solution root either answers from the
        // SDK scope with none of their packages or, once indexed, from an index that
        // nothing ever refreshes.
        if (ApiCacheBuilder.FindProjectNameInDir(projectDir) is null
            && ApiCacheBuilder.FindSolutionFileInDir(projectDir) is not null)
        {
            List<ProjectManifest> indexed = FindManifestsUnderDir(manifestFiles, projectDir);
            if (indexed.Count == 0
                || indexed.Any(m => IsProjectDirStale(manifestFiles, Path.GetFullPath(m.ProjectDir), cacheDir))
                || HasRestoredProjectMissingFromIndex(projectDir, indexed))
            {
                return RunIndexWithLock(projectDir, cacheDir);
            }
            return null;
        }

        if (NuGetResolver.FindRestoreOutput(projectDir) is null)
        {
            return null;
        }

        // Only a directory that actually holds a project is indexed from here.
        bool needsUpdate = manifestFiles.Length == 0
            || (ApiCacheBuilder.FindProjectNameInDir(projectDir) is not null
                && IsProjectDirStale(manifestFiles, projectDir, cacheDir));

        if (needsUpdate)
        {
            return RunIndexWithLock(projectDir, cacheDir);
        }
        return null;
    }

    /// <summary>
    /// Whether the solution tree holds a project that was restored after it was last
    /// indexed but has no manifest — a project added to the solution since. Without this,
    /// stale-checking only the projects already indexed leaves a newly added project
    /// invisible, and a query at the solution root answers from the one project it
    /// happens to know instead of asking which of the two was meant.
    ///
    /// Restore time is compared against the newest manifest under the tree rather than
    /// simply treating "no manifest" as stale: a project that legitimately produces no
    /// manifest — one that resolves no metadata packages at all — would otherwise be
    /// missing forever and re-index the whole solution on every query.
    ///
    /// The solution file's own timestamp is the second trigger, because adding an
    /// already-restored project changes membership without touching that project's
    /// restore output — its <c>project.assets.json</c> can be older than the manifest
    /// written for a sibling, leaving the new project invisible to every query.
    /// </summary>
    private static bool HasRestoredProjectMissingFromIndex(string solutionDir, List<ProjectManifest> indexed)
    {
        var indexedDirs = new HashSet<string>(
            indexed.Select(m => Path.GetFullPath(m.ProjectDir).TrimEnd(Path.DirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase);

        DateTime lastIndexed = indexed
            .Select(manifest => DateTime.TryParse(
                    manifest.GeneratedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime generated)
                ? generated
                : DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        bool membershipChanged = ApiCacheBuilder.FindSolutionFileInDir(solutionDir) is { } solutionFile
            && File.GetLastWriteTimeUtc(solutionFile) > lastIndexed;

        foreach (string projectFile in ApiCacheBuilder.DiscoverProjectFiles(solutionDir, scan: false))
        {
            string dir = Path.GetFullPath(Path.GetDirectoryName(projectFile)!).TrimEnd(Path.DirectorySeparatorChar);
            if (indexedDirs.Contains(dir))
            {
                continue;
            }
            string? restoreOutput = NuGetResolver.FindRestoreOutput(dir);
            if (restoreOutput is null)
            {
                continue;
            }
            if (membershipChanged || File.GetLastWriteTimeUtc(restoreOutput) > lastIndexed)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the cached index for one project directory no longer reflects it: never
    /// indexed, indexed before the project's last restore, or invalidated by
    /// <see cref="IsCachedIndexStale"/>. Manifests are matched on their recorded
    /// <c>ProjectDir</c>, not on file name, so a same-named project in another directory
    /// cannot be mistaken for this one and suppress indexing of the project being queried.
    /// A directory holding several projects is stale until every one of them is indexed,
    /// so a second project added beside the first does not stay invisible.
    /// </summary>
    private static bool IsProjectDirStale(string[] manifestFiles, string projectDir, string cacheDir)
    {
        List<string> manifestPaths = FindManifestPathsForDir(manifestFiles, projectDir);
        if (manifestPaths.Count == 0)
        {
            return true;
        }
        if (manifestPaths.Count < CountProjectsInDir(projectDir))
        {
            return true;
        }
        string? restoreOutput = NuGetResolver.FindRestoreOutput(projectDir);
        foreach (string manifestPath in manifestPaths)
        {
            if (restoreOutput is not null
                && File.GetLastWriteTimeUtc(restoreOutput) > File.GetLastWriteTimeUtc(manifestPath))
            {
                return true;
            }
            if (IsCachedIndexStale(manifestPath, projectDir, cacheDir))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>How many indexable project files a directory declares, ignoring failures.</summary>
    private static int CountProjectsInDir(string projectDir)
    {
        try
        {
            return ApiCacheBuilder.DiscoverProjectFiles(projectDir, scan: false).Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether an existing manifest can still be answered from. Restore output is not the
    /// only thing that invalidates an index: rebuilding a referenced project rewrites its
    /// <c>.winmd</c> without touching <c>project.assets.json</c>, and a package cache written
    /// by an older cache format no longer matches what the query side reads. Both are checked
    /// here, on the read path, because the builder is never reached unless something is
    /// already known to be stale — so a query would otherwise answer confidently from
    /// metadata that no longer describes the project.
    /// </summary>
    private static bool IsCachedIndexStale(string manifestPath, string projectDir, string cacheDir)
    {
        DateTime manifestWriteTime = File.GetLastWriteTimeUtc(manifestPath);

        bool referencedWinmdIsNewer = ApiCacheBuilder.DiscoverProjectFiles(projectDir, scan: false)
            .SelectMany(NuGetResolver.FindWinMdFromProjectReferences)
            .SelectMany(reference => reference.WinMdFiles)
            .Any(winmd => File.GetLastWriteTimeUtc(winmd) > manifestWriteTime);
        if (referencedWinmdIsNewer)
        {
            return true;
        }

        try
        {
            ProjectManifest? manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), ApiSearchJsonContext.Default.ProjectManifest);
            if (manifest is null)
            {
                return true;
            }
            foreach (ProjectPackageRef package in manifest.Packages)
            {
                if (!ApiCachePaths.TryPackageCacheDir(cacheDir, package, out string packageDir))
                {
                    return true;
                }
                string metaPath = Path.Combine(packageDir, "meta.json");
                if (!File.Exists(metaPath))
                {
                    return true;
                }
                PackageMeta? meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), ApiSearchJsonContext.Default.PackageMeta);
                if (meta is null || meta.Format != ApiCachePaths.CacheFormatVersion)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A manifest or meta.json that cannot be read is treated as stale so the
            // next query rebuilds it rather than answering from an unknown state.
            return true;
        }

        return false;
    }

    /// <summary>
    /// What to report when the cache lock is still held after the wait. This point is
    /// only reached because the index was already known to be stale, so simply carrying
    /// on would answer confidently from metadata that no longer describes the project.
    /// The other process may however have just indexed it, so the staleness is
    /// re-checked rather than either outcome being assumed. Returns <c>null</c> when the
    /// index is now usable, or a retryable error message when it is not.
    /// </summary>
    internal string? LockTimedOutResult(string projectDir, string cacheDir)
    {
        if (!IsProjectDirStale(ManifestFiles(cacheDir), projectDir, cacheDir))
        {
            return null;
        }
        logger.LogWarning("Timed out waiting for the API metadata cache lock.");
        return "The API metadata index for this project is out of date, and another winapp process " +
            "has held the cache lock for 30 seconds. Answering now would use metadata that no longer " +
            "describes the project. Retry once the other process finishes, or run " +
            "'winapp find-api refresh' to rebuild the index.";
    }

    /// <summary>
    /// Runs an index pass under the cache lock. Returns an error message when the
    /// index failed or could not be run, or <c>null</c> when the index is usable.
    /// </summary>
    internal string? RunIndexWithLock(string projectDir, string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        string lockPath = Path.Combine(cacheDir, ".lock");

        FileStream? lockFile = TryAcquireIndexLock(lockPath);
        if (lockFile is null)
        {
            // Another winapp process holds the lock and is indexing the same cache.
            // Wait for it to finish and then take the lock ourselves: the cache is
            // shared by every project, so the other process is very likely indexing
            // a different one and returning here would leave this caller's project
            // unindexed.
            logger.LogInformation("API metadata cache is being indexed by another process, waiting…");
            for (int i = 0; i < 30 && lockFile is null; i++)
            {
                Thread.Sleep(1000);
                lockFile = TryAcquireIndexLock(lockPath);
            }
            if (lockFile is null)
            {
                return LockTimedOutResult(projectDir, cacheDir);
            }
        }

        using (lockFile)
        {
            try
            {
                logger.LogInformation("Indexing API metadata for this project…");
                // Index the directory already resolved for this scope so a named
                // project is not re-resolved (or resolved differently) here.
                var indexScope = new ApiRequestScope(projectDir, Project: null);
                var result = Refresh(indexScope, scan: false, onProgress: msg => logger.LogInformation("{Message}", msg));
                if (result.Outcome != ApiQueryOutcome.Ok)
                {
                    logger.LogWarning("Failed to index API metadata: {Message}", result.Message);
                    return result.Message ?? "Failed to index API metadata for this project.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cancellation is deliberately excluded so Ctrl+C is not reported
                // as an indexing failure.
                logger.LogWarning(ex, "Failed to index API metadata.");
                return $"Failed to index API metadata for this project: {ex.Message}. " +
                    "Run 'winapp find-api refresh' to see the full error.";
            }
        }
        return null;
    }

    /// <summary>
    /// Takes the cache lock, or returns <see langword="null"/> when another process
    /// holds it. Only the acquisition is guarded, so a genuine I/O failure during the
    /// indexing work itself is never misreported as lock contention.
    /// </summary>
    private static FileStream? TryAcquireIndexLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the scope for the request: an explicit <c>--project</c> name (or the
    /// reserved SDK scope name), then a <c>--project-dir</c> match (by recorded dir,
    /// then by discovered project name), then the project in the current directory.
    /// When the current directory holds no project at all the machine-wide SDK scope
    /// answers instead — deliberately regardless of how many projects are cached, so
    /// the result never depends on unrelated global state. Returns a human-readable
    /// error when resolution is impossible.
    /// </summary>
    /// <summary>
    /// Finds the cached manifest that actually belongs to <paramref name="projectDir"/>.
    /// Manifests are matched on their recorded <c>ProjectDir</c> rather than on file
    /// name: identically-named projects in different directories must never resolve
    /// to each other, and legacy caches may still hold unhashed manifest names.
    /// </summary>
    private static string? FindManifestPathForDir(string[] files, string projectDir) =>
        FindManifestPathsForDir(files, projectDir).FirstOrDefault();

    /// <summary>
    /// Every cached manifest recorded for one project directory. A directory normally
    /// holds one project, but it may legally hold several (<c>App.csproj</c> and
    /// <c>Other.csproj</c> side by side), and those reference different packages — so
    /// callers that answer a query must distinguish "one project" from "several" rather
    /// than letting manifest enumeration order pick the API surface.
    /// </summary>
    private static List<string> FindManifestPathsForDir(string[] files, string projectDir)
    {
        string fullPath = Path.GetFullPath(projectDir);
        var matches = new List<string>();
        foreach (string path in files)
        {
            ProjectManifest? manifest = DeserializeManifest(path);
            if (manifest is not null && manifest.ProjectDir.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(path);
            }
        }
        return matches;
    }

    /// <summary>
    /// Resolves the scope for a directory that holds indexed projects: the project when
    /// there is exactly one, an ambiguity failure when there are several, or
    /// <see langword="null"/> when none are indexed so the caller's own fallback runs.
    /// </summary>
    private static ResolvedScope? ResolveDirScope(string[] files, string projectDir)
    {
        List<ProjectManifest> manifests = FindManifestPathsForDir(files, projectDir)
            .Select(DeserializeManifest)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        List<IGrouping<string, ProjectManifest>> distinct = manifests
            .GroupBy(m => m.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 1)
        {
            return ResolvedScope.Project(distinct[0].First());
        }
        if (distinct.Count > 1)
        {
            // Two projects in one directory reference different packages, so choosing
            // one would make the answer depend on manifest enumeration order — and the
            // caller would have no sign that the other project exists.
            string names = string.Join(", ", distinct
                .Select(g => $"'{g.Key}'")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return ResolvedScope.Failed(
                $"This directory has {distinct.Count} indexed projects: {names}. " +
                "Pick one with '--project <name>', or use '--project sdk' for the Windows SDK scope.");
        }
        return null;
    }

    private ResolvedScope ResolveManifest(ApiRequestScope scope, string cacheDir)
    {
        string[] files = ManifestFiles(cacheDir);

        if (scope.Project is not null)
        {
            if (IsSdkScopeName(scope.Project))
            {
                return ResolveSdkScope(cacheDir);
            }
            var matches = new List<ProjectManifest>();
            foreach (string path in files)
            {
                if (!IsManifestForProject(path, scope.Project))
                {
                    continue;
                }
                ProjectManifest? manifest = DeserializeManifest(path);
                if (manifest is not null &&
                    manifest.ProjectName.Equals(scope.Project, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(manifest);
                }
            }

            List<IGrouping<string, ProjectManifest>> distinct = matches
                .GroupBy(m => Path.GetFullPath(m.ProjectDir), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 1)
            {
                return ResolvedScope.Project(distinct[0].First());
            }
            if (distinct.Count > 1)
            {
                // Several indexed projects share this name. Answering from whichever
                // was enumerated first would make the result depend on directory
                // ordering, so make the caller disambiguate explicitly.
                string dirs = string.Join(", ", distinct
                    .Select(g => $"'{g.Key}'")
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
                return ResolvedScope.Failed(
                    $"Project '{scope.Project}' is ambiguous — {distinct.Count} indexed projects share that name: {dirs}. " +
                    "Use '--project-dir <path>' to pick one.");
            }
            string available = AvailableProjects(files);
            return ResolvedScope.Failed(available.Length == 0
                ? $"Project '{scope.Project}' is not indexed — no projects are indexed yet. Run 'winapp find-api refresh' in the project directory, or use '--project sdk' for the Windows SDK scope."
                : $"Project '{scope.Project}' is not indexed. Run 'winapp find-api refresh' in the project directory, or pick from: {available}.");
        }

        if (scope.ProjectDir is not null)
        {
            string fullPath = Path.GetFullPath(scope.ProjectDir);

            // A path that does not exist is a mistake — almost always a typo. Answering
            // it from the SDK scope returns a confident, plausible result for a project
            // the caller never named, which is worse than no answer at all.
            if (!Directory.Exists(fullPath))
            {
                return ResolvedScope.Failed(
                    $"Project directory not found: '{fullPath}'. Check the path passed to '--project-dir', " +
                    "or use '--project sdk' to query the machine-wide Windows SDK scope.");
            }

            if (ResolveDirScope(files, fullPath) is { } dirScope)
            {
                return dirScope;
            }
            string? dirProjectName = ApiCacheBuilder.FindProjectNameInDir(fullPath);
            if (dirProjectName is not null)
            {
                // An explicit --project-dir named a real project that isn't indexed.
                // Do NOT quietly widen to the SDK scope — the caller asked about that
                // project specifically, and its NuGet packages would be missing — and
                // do NOT fall back to a same-named manifest from another directory.
                string available = AvailableProjects(files);
                return ResolvedScope.Failed($"No indexed API metadata was found for '{fullPath}'. Restore it first " +
                    "('winapp restore', or 'dotnet restore' for a .NET project without winapp.yaml), " +
                    $"then run 'winapp find-api refresh' in that directory.{(available.Length == 0 ? "" : $" Indexed projects: {available}.")}");
            }

            // --project-dir pointed at a directory with no project in it. A solution
            // directory resolves the same way here as it does as the current directory,
            // so naming it explicitly does not silently drop the solution's packages.
            if (ResolveSolutionScope(fullPath, files) is { } namedSolution)
            {
                return namedSolution;
            }
            return ResolveSdkScope(cacheDir);
        }

        string cwd = currentDirectory.GetCurrentDirectory();
        string? currentName = ApiCacheBuilder.FindProjectNameInDir(cwd);
        if (currentName is not null)
        {
            // Resolve strictly by directory. Matching a same-named manifest from a
            // different directory would answer from an unrelated project's packages.
            if (ResolveDirScope(files, cwd) is { } cwdScope)
            {
                return cwdScope;
            }
            return ResolvedScope.Failed(NoProjectMessage);
        }

        // A solution directory contains no project of its own, but the projects it builds
        // are indexed under it. Answering from the SDK scope there silently drops every
        // NuGet package those projects reference.
        if (ResolveSolutionScope(cwd, files) is { } solutionScope)
        {
            return solutionScope;
        }

        // No project here. Answer from the machine-wide SDK scope. Note this does not
        // consult the cached project list at all: a query from a projectless directory
        // must not change meaning just because some unrelated project was indexed.
        return ResolveSdkScope(cacheDir);
    }

    /// <summary>
    /// Resolves a directory that holds a solution but no project of its own to the single
    /// project the solution builds, or asks which one when it builds several. Returns
    /// <see langword="null"/> when the directory holds no solution or nothing under it is
    /// indexed, leaving the caller's own fallback in charge.
    /// </summary>
    private static ResolvedScope? ResolveSolutionScope(string dir, string[] files)
    {
        if (ApiCacheBuilder.FindSolutionFileInDir(dir) is not { } solutionFile)
        {
            return null;
        }

        // The solution says which projects it builds. A project indexed earlier from a
        // sibling directory the solution excludes must not make the caller disambiguate
        // against something their solution does not contain. An unreadable or empty
        // solution leaves membership unknown, so everything indexed under the directory
        // stays eligible.
        var solutionProjectDirs = SolutionProjectReader.ReadProjectPaths(solutionFile)
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.GetFullPath(d!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<ProjectManifest> candidates = FindManifestsUnderDir(files, dir);
        if (solutionProjectDirs.Count > 0)
        {
            candidates = candidates.Where(m => solutionProjectDirs.Contains(Path.GetFullPath(m.ProjectDir)));
        }

        List<IGrouping<string, ProjectManifest>> underSolution = candidates
            .GroupBy(m => Path.GetFullPath(m.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (underSolution.Count == 1)
        {
            return ResolvedScope.Project(underSolution[0].First());
        }
        if (underSolution.Count > 1)
        {
            // Choosing one would make the answer depend on directory ordering, and the
            // projects in a solution reference different packages.
            string names = string.Join(", ", underSolution
                .Select(g => $"'{g.First().ProjectName}'")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return ResolvedScope.Failed(
                $"This solution has {underSolution.Count} indexed projects: {names}. " +
                "Pick one with '--project <name>', or use '--project sdk' for the Windows SDK scope.");
        }
        return null;
    }

    private static string[] ManifestFiles(string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        return Directory.Exists(projectsDir) ? Directory.GetFiles(projectsDir, "*.json") : [];
    }

    /// <summary>
    /// The indexed projects whose recorded directory lies under <paramref name="root"/>.
    /// A solution directory contains no project of its own, so this is how a query typed
    /// at the solution root reaches the projects the solution builds.
    /// </summary>
    private static List<ProjectManifest> FindManifestsUnderDir(string[] files, string root)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return files
            .Select(DeserializeManifest)
            .OfType<ProjectManifest>()
            .Where(manifest => Path.GetFullPath(manifest.ProjectDir).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsSdkScopeName(string name) =>
        name.Equals(ApiScopeNames.Sdk, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(ApiCachePaths.SdkScopeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the SDK-scope manifest, building it on first use. Indexing the Windows
    /// SDK is the same work a project index does for its SDK packages, so a warm
    /// project cache usually makes this a no-op reuse.
    /// </summary>
    private ResolvedScope ResolveSdkScope(string cacheDir)
    {
        string manifestPath = ApiCachePaths.SdkManifestPath(cacheDir);
        ProjectManifest? manifest = File.Exists(manifestPath) ? DeserializeManifest(manifestPath) : null;
        if (manifest is not null)
        {
            return ResolvedScope.Sdk(manifest);
        }

        try
        {
            List<PackageWithWinMd> packages = sdkPackages.GetSdkPackages();
            if (packages.Count == 0)
            {
                return ResolvedScope.Failed(NoSdkMessage);
            }
            logger.LogInformation("No project here — indexing {Scope} metadata…", ApiCachePaths.SdkScopeName);
            ApiCacheBuilder.BuildSdkCache(cacheDir, packages, onProgress: msg => logger.LogInformation("{Message}", msg));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Indexing the machine-wide SDK is best-effort; cancellation still
            // propagates so Ctrl+C is not reported as an SDK failure.
            logger.LogWarning(ex, "Failed to index Windows SDK metadata.");
            return ResolvedScope.Failed(NoSdkMessage);
        }

        manifest = File.Exists(manifestPath) ? DeserializeManifest(manifestPath) : null;
        return manifest is not null ? ResolvedScope.Sdk(manifest) : ResolvedScope.Failed(NoSdkMessage);
    }

    /// <summary>
    /// Human-readable list of indexed project names for an error message. Manifest
    /// *file* names carry a path hash (<c>App_ab12cd34.json</c>) to keep same-named
    /// projects apart, but <c>--project</c> matches <see cref="ProjectManifest.ProjectName"/>,
    /// so the file names must never be surfaced — a caller copying one back would get
    /// "not indexed". Returns an empty string when nothing readable is indexed.
    /// </summary>
    private static string AvailableProjects(IEnumerable<string> files) =>
        string.Join(", ", files
            .Select(DeserializeManifest)
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ProjectName))
            .Select(m => m!.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private static ProjectManifest? DeserializeManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ApiSearchJsonContext.Default.ProjectManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt manifest reads as "no manifest" so the caller can
            // report an unindexed project instead of crashing.
            return null;
        }
    }

    private const string NoProjectMessage =
        "No indexed API metadata was found for this project. Restore it first " +
        "('winapp restore', or 'dotnet restore' for a .NET project without winapp.yaml), " +
        "then run 'winapp find-api refresh' in the project directory to build the API index.";

    private const string NoSdkMessage =
        "No project was found here and no Windows SDK metadata is available on this machine. " +
        "Run 'winapp find-api' from a project directory, or install the Windows SDK / Windows App SDK.";

    /// <summary>
    /// A resolved query scope: the manifest to read plus whether it came from the
    /// machine-wide SDK scope (which excludes project NuGet packages) so callers
    /// can label the answer.
    /// </summary>
    private readonly record struct ResolvedScope(ProjectManifest? Manifest, string? Error, bool IsSdk)
    {
        public static ResolvedScope Project(ProjectManifest? manifest) => manifest is null
            ? Failed(NoProjectMessage)
            : new ResolvedScope(manifest, null, false);

        public static ResolvedScope Sdk(ProjectManifest manifest) => new(manifest, null, true);

        public static ResolvedScope Failed(string error) => new(null, error, false);
    }
}
