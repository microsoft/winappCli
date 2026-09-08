// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Xml;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Wraps the official NuGet client libraries (NuGet.Protocol / NuGet.Packaging /
/// NuGet.Configuration). Package sources, credentials and the global packages folder are resolved
/// from the user's <c>nuget.config</c> hierarchy (rooted at the current working directory), so
/// private/custom feeds and mirrors are honored when restoring SDK packages. Source resolution /
/// credentials / eligibility are delegated to <see cref="NugetSourceProvider"/> and package
/// download/extraction to <see cref="NugetPackageDownloader"/>; this service owns version selection,
/// dependency traversal and the on-disk cache layout.
/// </summary>
internal partial class NugetService : INugetService
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> DependencyCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IWinappDirectoryService _winappDirectoryService;
    private readonly NugetSourceProvider _sourceProvider;
    private readonly NugetPackageDownloader _downloader;

    public NugetService(
        IWinappDirectoryService winappDirectoryService,
        NugetSourceProvider sourceProvider,
        NugetPackageDownloader downloader)
    {
        _winappDirectoryService = winappDirectoryService;
        _sourceProvider = sourceProvider;
        _downloader = downloader;
    }

    private static readonly string[] IgnoredDependencyPrefixes =
    [
        "NETStandard.",
        "runtime.",
        "System.",
        "Microsoft.Bcl.",
        "Microsoft.NETCore.",
    ];

    public static readonly string[] SDK_PACKAGES =
    [
        "Microsoft.Windows.CppWinRT",
        BuildToolsService.WINAPP_SDK_PACKAGE,
        "Microsoft.Windows.ImplementationLibrary",
        BuildToolsService.CPP_SDK_PACKAGE,
        $"{BuildToolsService.CPP_SDK_PACKAGE}.x64",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.arm64"
    ];

    public DirectoryInfo GetNuGetGlobalPackagesDir()
    {
        // In test mode (cache override set), use a "packages" subdir of the override directory
        var globalDir = _winappDirectoryService.GetGlobalWinappDirectory();
        if (IsTestOverride(globalDir))
        {
            var overrideDir = new DirectoryInfo(Path.Join(globalDir.FullName, "packages"));
            if (!overrideDir.Exists)
            {
                overrideDir.Create();
            }
            return overrideDir;
        }

        // Resolve the global packages folder from the user's NuGet configuration. This honors the
        // NUGET_PACKAGES environment variable and the `globalPackagesFolder` setting in nuget.config,
        // falling back to %USERPROFILE%/.nuget/packages.
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(_sourceProvider.Settings);
        var nugetDir = new DirectoryInfo(globalPackagesFolder);
        if (!nugetDir.Exists)
        {
            nugetDir.Create();
        }
        return nugetDir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        // Validate the identity BEFORE it is ever turned into a filesystem path. A malformed id or a value
        // that is not a real NuGet version (e.g. "..") must never be concatenated into the cache path: it
        // could otherwise resolve to a directory outside the package folder (path traversal) that a later
        // DirectoryInfo.Exists() check would treat as an already-installed package. There is deliberately no
        // raw string fallback — an unparseable version is an error, not a literal folder name.
        if (string.IsNullOrWhiteSpace(packageName) || !PackageIdValidator.IsValidPackageId(packageName))
        {
            throw new InvalidOperationException(
                $"'{packageName}' is not a valid NuGet package id.");
        }

        var parsed = ParseVersion(packageName, version);
        var cache = GetNuGetGlobalPackagesDir();
        // Resolve the on-disk folder the same way the global-packages writer does, so the path matches
        // regardless of how the version string is expressed (NuGet stores e.g. "1.0" under "1.0.0").
        var resolver = new VersionFolderPathResolver(cache.FullName);
        return new DirectoryInfo(resolver.GetInstallPath(packageName, parsed));
    }

    /// <summary>
    /// Reports whether <paramref name="package"/> <paramref name="version"/> is FULLY installed in the NuGet
    /// global-packages cache. The version directory merely existing is not sufficient: NuGet writes the
    /// ".nupkg.metadata" completion marker only after extraction finishes, so an interrupted download can
    /// leave a partial folder (missing nuspec / lib files) with no marker. Both this service and higher-level
    /// callers must gate "already installed" decisions on this single predicate so a partial cache entry is
    /// never mistaken for a complete install (which would let restore report a truncated dependency graph as
    /// success).
    /// </summary>
    public bool IsPackageInstalled(string package, string version) =>
        HasCompletionMarker(GetNuGetPackageDir(package, version));

    /// <summary>
    /// True when <paramref name="packageDir"/> exists and contains NuGet's ".nupkg.metadata" completion
    /// marker — the same signal NuGet itself uses to treat a global-packages entry as fully extracted.
    /// </summary>
    private static bool HasCompletionMarker(DirectoryInfo packageDir) =>
        packageDir.Exists && File.Exists(Path.Join(packageDir.FullName, ".nupkg.metadata"));

    /// <summary>
    /// Normalizes a version string to NuGet's canonical form (e.g. "1.0" -> "1.0.0") so the value stored
    /// and returned by the installer matches the on-disk global-packages folder layout. Returns the input
    /// unchanged if it is not a parseable NuGet version.
    /// </summary>
    internal static string NormalizeVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version;

    /// <summary>
    /// Parses a version string into a <see cref="NuGetVersion"/>, throwing an
    /// <see cref="InvalidOperationException"/> that names the package and the offending value when it is
    /// not a valid NuGet version. <see cref="NuGetVersion.Parse(string)"/> would otherwise surface a raw
    /// <see cref="ArgumentException"/> with a message less actionable than the rest of this service.
    /// </summary>
    private static NuGetVersion ParseVersion(string packageId, string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            throw new InvalidOperationException(
                $"'{version}' is not a valid NuGet version for package '{packageId}'. Specify a valid NuGet version such as 1.2.3 or 1.2.3-preview.");
        }

        return parsed;
    }

    /// <summary>
    /// Detects whether the global winapp directory is a test override (not the real user profile .winapp).
    /// </summary>
    private static bool IsTestOverride(DirectoryInfo globalDir)
    {
        var defaultWinapp = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp");
        return !string.Equals(globalDir.FullName, defaultWinapp, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY"));
    }

    /// <summary>
    /// Mutable state for a single <see cref="InstallPackageAsync"/> walk: what has been selected, what each
    /// selected version requires, and what failed. Bundled rather than passed as separate parameters so the
    /// resolver can answer "is this package still part of the graph?" at any point, which is what makes the
    /// constraint set self-correcting when an upgrade discards a subtree.
    /// </summary>
    private sealed class InstallGraph(string rootPackage)
    {
        public string RootPackage { get; } = rootPackage;

        /// <summary>The selected version of each package id, and the value returned to callers.</summary>
        public Dictionary<string, string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A recorded failure, tagged with the package version that hit it and, when it concerns a specific
        /// dependency, that dependency's identity and required range. Both are needed to decide later whether
        /// the failure still describes the resolved graph.
        /// </summary>
        public sealed record FailureRecord(string Package, string Version, string? DependencyId, VersionRange? DependencyRange, string Message);

        public List<FailureRecord> Failures { get; } = [];

        /// <summary>
        /// Records a dependency failure against the package version that hit it. <paramref name="dependencyId"/>
        /// and <paramref name="dependencyRange"/> are supplied whenever the failure is about one dependency, so
        /// it can be dropped if that requirement is later satisfied.
        /// </summary>
        public void AddFailure(string package, string version, string message, string? dependencyId = null, VersionRange? dependencyRange = null) =>
            Failures.Add(new FailureRecord(package, version, dependencyId, dependencyRange, message));

        /// <summary>
        /// Failures that still describe the resolved graph. Two things can retire one.
        ///
        /// The version that recorded it may have been replaced or orphaned, in which case the failure belongs
        /// to a branch that no longer exists: with C 1.0 -> Missing and a C 2.0 that needs nothing, upgrading C
        /// resolves cleanly and C 1.0's missing dependency must not still be fatal.
        ///
        /// Or the requirement itself may have been met since. Resolution is order-dependent, so a branch can be
        /// blocked by a constraint that a later upgrade removes — a conflict on X [2] recorded while a
        /// since-orphaned package pinned X [1] is moot once another branch pulls X up to 2. The declaring
        /// package is still selected there, so only re-checking the requirement against the final selection
        /// catches it.
        /// </summary>
        public IEnumerable<string> GetActiveFailures()
        {
            var reachable = GetReachablePackages();
            foreach (var failure in Failures)
            {
                if (!reachable.Contains(failure.Package)
                    || !Installed.TryGetValue(failure.Package, out var selectedVersion)
                    || !string.Equals(selectedVersion, failure.Version, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (failure.DependencyId is not null
                    && failure.DependencyRange is not null
                    && Installed.TryGetValue(failure.DependencyId, out var dependencyVersion)
                    && NuGetVersion.TryParse(dependencyVersion, out var parsedDependencyVersion)
                    && RangeSatisfiesWithFloat(failure.DependencyRange, parsedDependencyVersion))
                {
                    continue;
                }

                yield return failure.Message;
            }
        }

        /// <summary>
        /// The ranges declared by each package's SELECTED version, keyed by the DECLARING package id. Keying by
        /// declaring id (rather than appending every range ever seen to a per-dependency list) means re-walking
        /// an upgraded package overwrites its entry, so the replaced version's requirements stop applying.
        /// </summary>
        public Dictionary<string, Dictionary<string, VersionRange>> Declared { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The package ids the graph still reaches from the root, following the dependencies declared by each
        /// package's selected version. An upgrade can orphan a whole subtree, and those packages linger in
        /// <see cref="Installed"/> and <see cref="Declared"/> until pruned, so membership here — not mere
        /// presence in the dictionaries — is what makes a package part of the resolved graph.
        /// </summary>
        public HashSet<string> GetReachablePackages()
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Installed.ContainsKey(RootPackage))
            {
                // The root is recorded before its dependencies are walked, so this only happens if the root
                // itself never installed. Return every id rather than an empty set: callers use this to
                // discard things, and discarding everything would be worse than discarding nothing.
                return [.. Installed.Keys];
            }

            reachable.Add(RootPackage);
            var pending = new Queue<string>();
            pending.Enqueue(RootPackage);

            while (pending.Count > 0)
            {
                if (!Declared.TryGetValue(pending.Dequeue(), out var deps))
                {
                    continue;
                }

                // Materialized before the loop mutates `reachable`: the filter reads the same set the body
                // writes, so a lazily evaluated sequence would be deciding membership against a set that is
                // changing underneath it. deps.Keys is already distinct, so one pass cannot yield a duplicate.
                var newlyReached = deps.Keys
                    .Where(depName => Installed.ContainsKey(depName) && !reachable.Contains(depName))
                    .ToList();

                foreach (var depName in newlyReached)
                {
                    reachable.Add(depName);
                    pending.Enqueue(depName);
                }
            }

            return reachable;
        }

        /// <summary>
        /// Every range the graph currently places on <paramref name="dependencyId"/>, counting only packages
        /// still reachable from the root. Requirements from a subtree an upgrade discarded must not be able to
        /// veto a version: with C 1.0 -> D -> E [1.0], upgrading to a C 2.0 that has no D leaves D's E [1.0]
        /// behind, and counting it would reject a later branch that legitimately needs E [2.0].
        /// </summary>
        public IEnumerable<VersionRange> GetActiveConstraints(string dependencyId)
        {
            var reachable = GetReachablePackages();
            foreach (var (declaringPackage, declared) in Declared)
            {
                if (reachable.Contains(declaringPackage) && declared.TryGetValue(dependencyId, out var range))
                {
                    yield return range;
                }
            }
        }

        /// <summary>
        /// Drops packages the resolved graph no longer reaches. Callers such as <c>WorkspaceSetupService</c>
        /// copy headers, libs, WinMDs and runtimes for every returned entry, so an orphan left here publishes
        /// assets from a package the resolution rejected into <c>.winapp</c>.
        /// </summary>
        public void PruneUnreachablePackages()
        {
            var reachable = GetReachablePackages();
            foreach (var orphan in Installed.Keys.Where(name => !reachable.Contains(name)).ToList())
            {
                Installed.Remove(orphan);
            }
        }
    }

    public async Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        NugetSourceProvider.EnsureCredentialService();
        var graph = new InstallGraph(package);
        using var cacheContext = new SourceCacheContext();
        await InstallPackageRecursiveAsync(package, version, graph, taskContext, cacheContext, cancellationToken);

        // A downloaded root package with unresolvable/uninstallable REQUIRED transitive dependencies is an
        // incomplete install, not a success. Each gap was surfaced as a warning above (and the rest of the
        // tree was still installed best-effort); now fail the operation so callers such as `restore` exit
        // non-zero instead of reporting a partial install as complete.
        // Failures recorded against a package version that a later upgrade replaced describe a branch that is
        // no longer in the graph, so only the ones still reachable make this install incomplete.
        var activeFailures = graph.GetActiveFailures().ToList();
        if (activeFailures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Installed {package} {version} but {activeFailures.Count} required dependency(ies) could not be installed: {string.Join("; ", activeFailures)}.");
        }

        graph.PruneUnreachablePackages();

        return graph.Installed;
    }

    /// <summary>
    /// Downloads and extracts a NuGet package to the global packages cache, then recursively installs dependencies.
    /// </summary>
    private async Task InstallPackageRecursiveAsync(string package, string version, InstallGraph graph, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        // Already processed this package?
        if (graph.Installed.ContainsKey(package))
        {
            return;
        }

        // Store the canonical (normalized) version so the value recorded in `installed` matches the
        // on-disk NuGet folder layout (e.g. "1.0" -> "1.0.0"). Downstream consumers build cache paths by
        // concatenating this value, so an un-normalized shorthand would point them at a folder that the
        // global-packages writer never created.
        var normalizedVersion = NormalizeVersion(version);

        var packageDir = GetNuGetPackageDir(package, normalizedVersion);

        // Already fully installed on disk? Gate on the shared completion-marker predicate (see
        // IsPackageInstalled): the directory merely existing is not enough, because an interrupted extraction
        // can leave a partial folder with no ".nupkg.metadata" marker. Accepting that corrupt entry would let
        // ReadDependenciesFromNuspec return an empty set and restore report a truncated graph as success. When
        // the marker is missing, fall through so the downloader re-extracts and completes the entry.
        if (HasCompletionMarker(packageDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Skip} {package} {normalizedVersion} already present");
            graph.Installed[package] = normalizedVersion;
            // Still resolve dependencies to populate installed dictionary
            await ResolveDependenciesAsync(packageDir, package, normalizedVersion, graph, taskContext, cacheContext, cancellationToken);
            return;
        }

        // Download and extract the package from the user's configured NuGet sources into the
        // global packages folder (using the standard NuGet on-disk layout). Throws with the
        // underlying source error if no configured source can provide the package.
        var identity = new PackageIdentity(package, ParseVersion(package, normalizedVersion));
        await _downloader.DownloadPackageAsync(identity, GetNuGetGlobalPackagesDir().FullName, cacheContext, cancellationToken);

        graph.Installed[package] = normalizedVersion;
        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {package} {normalizedVersion}");

        // Recursively install dependencies
        await ResolveDependenciesAsync(packageDir, package, normalizedVersion, graph, taskContext, cacheContext, cancellationToken);
    }

    /// <summary>
    /// Reads the .nuspec from an extracted package and recursively installs dependencies.
    /// </summary>
    private async Task ResolveDependenciesAsync(DirectoryInfo packageDir, string package, string version, InstallGraph graph, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        Dictionary<string, VersionRange> deps;
        try
        {
            deps = ReadDependenciesFromNuspec(packageDir, package);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or XmlException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or PackagingException)
        {
            // Anything that stops the manifest being read is treated the same way. The list is the set
            // ReadDependenciesFromNuspec can actually produce: file/permission errors, malformed XML, NuGet's
            // own parse failures, and the package-id shape guard. Nothing is swallowed — every one of these is
            // recorded below and makes InstallPackageAsync throw — so the filter exists to let a genuinely
            // unexpected exception keep its own stack rather than being relabelled a manifest problem.
            //
            // The package was downloaded, but its .nuspec cannot be read so its dependency graph is unknown.
            // This is different from a package that simply declares no dependencies (that reads back as an
            // empty set without throwing): an unreadable manifest means required transitive packages may be
            // silently missing. Record it as a failure — like the dependency resolution/install errors below —
            // so the overall install fails loudly instead of reporting success with an incomplete graph.
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not read dependencies for {package} {version}: {NugetErrorMessage.Redact(ex.Message)}");
            graph.AddFailure(package, version, $"{package} {version}: dependency manifest could not be read: {NugetErrorMessage.Redact(ex.Message)}");
            return;
        }

        // Record what THIS version of the package requires, replacing whatever the previously selected version
        // of the same id declared. Only the selected version of each id is ever present, so the map always
        // describes the current graph and never the branches that an upgrade discarded.
        graph.Declared[package] = deps;

        foreach (var (depName, depVersionRange) in deps)
        {
            if (graph.Installed.TryGetValue(depName, out var installedDepVersion))
            {
                if (NuGetVersion.TryParse(installedDepVersion, out var selectedVersion)
                    && RangeSatisfiesWithFloat(depVersionRange, selectedVersion))
                {
                    // The version fixed by an earlier branch already satisfies this one.
                    continue;
                }

                // It does not. winapp resolves as it installs, so an earlier branch can fix a lower version
                // than a later branch requires — the real Windows App SDK graph does exactly this (a branch
                // selects InteractiveExperiences 2.1.3 while WindowsAppSDK requires 2.1.6). Keeping the lower
                // version would report success for a graph that does not satisfy every requirement, leaving
                // consumers without APIs a package declared it needs.
                //
                // Resolve this branch's range and, when the result also satisfies every range the CURRENT graph
                // places on this id, upgrade to it. Versions only ever move up, so this terminates. If no such
                // version exists (e.g. two conflicting exact pins) it is a genuine conflict and fails loudly.
                string? candidate;
                try
                {
                    candidate = await ResolveDependencyVersionAsync(depName, depVersionRange, cacheContext, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not re-resolve dependency {depName} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}");
                    graph.AddFailure(package, version, $"{depName} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}", depName, depVersionRange);
                    continue;
                }

                if (!string.IsNullOrEmpty(candidate)
                    && NuGetVersion.TryParse(candidate, out var candidateVersion)
                    && graph.GetActiveConstraints(depName).All(r => RangeSatisfiesWithFloat(r, candidateVersion)))
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Rocket} {depName}: {installedDepVersion} → {candidate} (required by {package} {version})");
                    // Drop the earlier selection so the recursive install re-walks the upgraded version's own
                    // dependency graph rather than short-circuiting on the package id.
                    graph.Installed.Remove(depName);
                    try
                    {
                        await InstallPackageRecursiveAsync(depName, candidate, graph, taskContext, cacheContext, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Without this the exception escaped to whichever ancestor happened to be inside the
                        // fresh-install try below, where it was recorded against THAT package's dependency —
                        // one which was installed and satisfied, so the failure was immediately retired and the
                        // operation reported success. Meanwhile the selection was already removed above, so the
                        // package vanished from the graph entirely.
                        //
                        // Restore the version that was working before the upgrade was attempted: it does not
                        // satisfy this branch, which is exactly what the failure below records, but keeping it
                        // leaves the graph describing what is actually on disk rather than silently short.
                        graph.Installed[depName] = installedDepVersion;
                        taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not upgrade {depName} to {candidate} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}");
                        graph.AddFailure(
                            package,
                            version,
                            $"{depName} could not be upgraded to {candidate} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}",
                            depName,
                            depVersionRange);
                    }

                    continue;
                }

                var rangeText = depVersionRange.OriginalString ?? depVersionRange.ToNormalizedString();
                var conflict = $"{depName} cannot be resolved to a single version: already selected {installedDepVersion}, but {package} {version} requires '{rangeText}' and no available version satisfies every requirement";
                taskContext.AddStatusMessage($"{UiSymbols.Warning} Dependency version conflict: {conflict}");
                graph.AddFailure(package, version, conflict, depName, depVersionRange);
                continue;
            }

            try
            {
                var depVersion = await ResolveDependencyVersionAsync(depName, depVersionRange, cacheContext, cancellationToken);
                if (string.IsNullOrEmpty(depVersion))
                {
                    // A null result now means only a version-less / fully unbounded dependency (the range
                    // constrains nothing); skip it rather than guessing a version to install. A bounded range
                    // that cannot be resolved throws instead and is surfaced by the catch below as a
                    // non-fatal per-dependency warning, so it is no longer silently dropped.
                    continue;
                }

                await InstallPackageRecursiveAsync(depName, depVersion, graph, taskContext, cacheContext, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Never mask a genuine cancellation as a successful (but incomplete) install.
                throw;
            }
            catch (Exception ex)
            {
                // A single transitive dependency that cannot be resolved (e.g. its only satisfying source
                // could not be queried) or installed should not abort the rest of the tree — keep installing
                // the remaining dependencies best-effort — but the failure must be both visible (not hidden
                // behind verbose-only logging) AND fail the overall operation. Record it so InstallPackageAsync
                // exits non-zero rather than reporting an incomplete install as success.
                taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not install dependency {depName} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}");
                graph.AddFailure(package, version, $"{depName} (required by {package} {version}): {NugetErrorMessage.Redact(ex.Message)}", depName, depVersionRange);
            }
        }
    }

    /// <summary>
    /// Like <see cref="VersionRange.Satisfies(NuGetVersion)"/> but also applies a floating range's full
    /// semantics. <c>VersionRange.Satisfies</c> only checks the declared min/max bounds, and a float declares no
    /// upper bound and carries prerelease/prefix eligibility that the bounds don't express — so a stable
    /// <c>1.*</c> reports satisfying <c>2.0.0</c> (above the band) and <c>1.5.0-preview</c> (a prerelease the
    /// stable float excludes). <see cref="FloatRange.Satisfies(NuGetVersion)"/> is the authoritative predicate
    /// for that (ceiling, prerelease eligibility, and prefix), so defer to it for floating ranges. Without this,
    /// a diamond where one branch already fixed the shared dependency to a version outside a later branch's
    /// float (e.g. the 2.* branch installs 2.0.0 first, then the 1.* branch is checked) would be silently
    /// accepted as satisfied.
    /// </summary>
    internal static bool RangeSatisfiesWithFloat(VersionRange range, NuGetVersion version)
    {
        if (!range.Satisfies(version))
        {
            return false;
        }

        if (range.IsFloating && range.Float is { } floatRange)
        {
            return floatRange.Satisfies(version);
        }

        return true;
    }
}
