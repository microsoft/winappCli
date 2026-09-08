// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace WinApp.Cli.Services;

/// <summary>
/// Version selection and dependency-graph resolution for <see cref="NugetService"/> (partial): latest-version
/// selection (fail-closed across sources so a source outage cannot cause a downgrade), resolving a declared
/// dependency range to a concrete version (including unlisted pins), and building the transitive dependency
/// graph. Split from the install/cache half of the class to keep each file focused and within the
/// repository's file-size guidance.
/// </summary>
internal partial class NugetService
{
    /// <summary>
    /// Reads dependencies from the .nuspec file embedded in an extracted NuGet package.
    /// Returns every declared dependency across all target-framework groups (first occurrence wins).
    /// </summary>
    private static Dictionary<string, VersionRange> ReadDependenciesFromNuspec(DirectoryInfo packageDir, string packageName)
    {
        var dependencies = new Dictionary<string, VersionRange>(StringComparer.OrdinalIgnoreCase);

        // The .nuspec file is at the root of the extracted package, named {lowercase-id}.nuspec. Force the
        // segment to a bare file name: the id originates from a dependency entry in another package's
        // manifest, so a rooted or multi-segment value must not be able to escape the extracted package.
        // Callers reach here via GetNuGetPackageDir, which already rejects invalid ids, so this is defense in
        // depth that keeps the invariant local and checkable.
        var nuspecFileName = Path.GetFileName($"{packageName.ToLowerInvariant()}.nuspec");

        // GetFileName above strips any directory part; assert the result really is a plain relative name so a
        // hostile or malformed id can never resolve outside the extracted package.
        if (Path.IsPathRooted(nuspecFileName))
        {
            throw new InvalidOperationException(
                $"'{packageName}' is not a usable NuGet package id: it does not resolve to a file name inside the package directory.");
        }

        var nuspecPath = Path.Join(packageDir.FullName, nuspecFileName);
        if (!File.Exists(nuspecPath))
        {
            // Try finding any .nuspec file
            var nuspecFiles = Directory.GetFiles(packageDir.FullName, "*.nuspec", SearchOption.TopDirectoryOnly);
            if (nuspecFiles.Length == 0)
            {
                // A validly extracted NuGet package always contains a root .nuspec, so its absence (in a
                // directory the caller already accepted via the completion marker) means the cache entry is
                // corrupt or was partially deleted. Returning an empty set here would be indistinguishable from
                // a package that genuinely declares no dependencies, letting the install report success while
                // silently omitting required transitive packages. Throw so the caller records a dependency
                // failure and the overall operation fails loudly instead.
                throw new FileNotFoundException(
                    $"No .nuspec found for package '{packageName}' in '{packageDir.FullName}'. The cached package directory is corrupt or incompletely extracted.");
            }
            nuspecPath = nuspecFiles[0];
        }

        var nuspec = new NuspecReader(nuspecPath);
        foreach (var group in nuspec.GetDependencyGroups())
        {
            // Skip framework/runtime reference packages (same filter as FetchDirectDependenciesAsync)
            // so we don't attempt to install non-winapp packages that aren't served by the feed.
            var applicable = group.Packages.Where(dependency =>
                !string.IsNullOrEmpty(dependency.Id)
                && dependency.VersionRange != null
                && !IgnoredDependencyPrefixes.Any(p => dependency.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

            foreach (var dependency in applicable)
            {
                dependencies.TryAdd(dependency.Id, dependency.VersionRange);
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Parses a NuGet version range and extracts the minimum version.
    /// Handles: "1.0.0", "[1.0.0]", "[1.0.0, )", "(1.0.0, 2.0.0)", and the
    /// bracket-stripped form "1.0.0, 2.0.0" (which can happen when callers
    /// pre-clean brackets without splitting on the range separator).
    /// </summary>
    internal static string ParseMinimumVersion(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return string.Empty;
        }

        // Strip brackets/parens (no-op if none are present)
        var trimmed = versionRange.Trim().TrimStart('[', '(').TrimEnd(']', ')');

        // Take the lower bound (before comma if present). Always check for a comma —
        // a NuGet range with brackets stripped (e.g. "1.0.0, 2.0.0") still needs
        // splitting; otherwise we'd treat the whole thing as a literal version.
        var commaIdx = trimmed.IndexOf(',');
        if (commaIdx >= 0)
        {
            trimmed = trimmed[..commaIdx].Trim();
        }

        return trimmed;
    }

    public async Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        if (sdkInstallMode == SdkInstallMode.None)
        {
            throw new ArgumentException("sdkInstallMode cannot be None", nameof(sdkInstallMode));
        }

        var list = await GetListedVersionsAsync(packageName, cancellationToken);
        var totalFound = list.Count;

        // If not winapp SDK, preview and experimental versions are the same
        if (packageName.StartsWith(BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase))
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
            else if (sdkInstallMode == SdkInstallMode.Preview)
            {
                // Only with preview
                list = [.. list.Where(v => v.Contains("-preview", StringComparison.OrdinalIgnoreCase))];
            }
            else if (sdkInstallMode == SdkInstallMode.Experimental)
            {
                // Only with experimental
                list = [.. list.Where(v => v.Contains("-experimental", StringComparison.OrdinalIgnoreCase))];
            }
        }
        else
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
        }

        if (list.Count == 0)
        {
            // Distinguish "the sources returned versions but none matched the requested channel" from
            // "no versions came back at all" so the user knows whether to change the channel or to check
            // the package ID / configured sources / credentials.
            var reason = totalFound > 0
                ? $"found {totalFound} version(s) but none matched the '{sdkInstallMode}' channel"
                : "no versions were returned by the configured NuGet sources — verify the package ID, the configured sources, and any required credentials";
            throw new InvalidOperationException($"No matching versions found for {packageName} ({reason}).");
        }

        list.Sort(CompareVersions);
        return list[^1];
    }

    /// <summary>
    /// Fetches the versions of a package from every source eligible to serve it (honoring
    /// <c>&lt;packageSourceMapping&gt;</c>). Unlisted versions are filtered out on sources that support
    /// registration/metadata, so they are not selected as "latest"; a flat-container-only feed
    /// (<c>PackageBaseAddress</c> with no registration resource) exposes no listed/unlisted flag, so that
    /// filter cannot be applied there and an unlisted version could be enumerated — see
    /// <see cref="GetSourceVersionsAsync"/> and the <see cref="INugetService.GetLatestVersionAsync"/> remarks.
    /// Because the result feeds a MAX ("latest") decision, a source that cannot be queried is treated as fatal
    /// rather than silently skipped: a partial result could otherwise make a caller select an older version
    /// (e.g. <c>update</c> could downgrade a pinned package). A source that exposes only the flat container is
    /// still enumerated through <see cref="GetSourceVersionsAsync"/> rather than skipped, so private feeds of
    /// that shape still contribute versions.
    /// </summary>
    private async Task<List<string>> GetListedVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        NugetSourceProvider.EnsureCredentialService();

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastError = null;
        string? lastErrorSource = null;
        using var cacheContext = new SourceCacheContext();

        var repos = _sourceProvider.GetRepositoriesForPackage(packageName);
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Request listed versions only. Registration/metadata-backed sources honor this and exclude
                // unlisted builds from "latest"; a flat-container-only feed (PackageBaseAddress, no
                // registration) exposes no listed/unlisted flag, so the filter cannot be applied there and an
                // unlisted version could be enumerated. Such a feed is still enumerated via its flat container
                // rather than skipped, so latest resolution keeps working against feeds of that shape.
                foreach (var version in await GetSourceVersionsAsync(repo, packageName, includeUnlisted: false, cacheContext, cancellationToken))
                {
                    versions.Add(version.ToNormalizedString());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagate genuine cancellation instead of masking it as "no versions found".
                throw;
            }
            catch (Exception ex)
            {
                // Record why this source failed (and which one). Because "latest" is a MAX across sources,
                // any eligible-source failure is treated as fatal after the loop (fail-closed) rather than
                // trusting a partial result: a source we could not reach/authenticate could hide a newer
                // version and cause a downgrade or a missed update.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
            }
        }

        // "Latest" is a MAX across sources, so a source we could not reach/authenticate would lower that
        // max and could cause a downgrade or a missed update. If any eligible source failed, surface it
        // (naming the source) instead of returning a partial, non-authoritative result. Inline the inner
        // message because top-level command handlers print only ex.Message.
        if (lastError != null)
        {
            // The source exception is deliberately NOT attached as the inner exception: its message carries
            // the raw feed URL, and sinks that unwrap a chain (UpdateCommand's GetBaseException().Message, the
            // telemetry exception event) would reprint the signed query string this redaction just removed.
            // The redacted text keeps everything actionable — which source failed, and why.
            throw new InvalidOperationException(
                $"Could not reliably determine the versions of {packageName}: source '{lastErrorSource}' could not be queried: {NugetErrorMessage.Redact(lastError.Message)}");
        }

        // No source was eligible at all: give the same actionable guidance as the download/dependency
        // paths (missing mapping vs. disabled mapped source vs. no sources configured) rather than letting
        // the caller fall back to a generic "verify package ID / sources / credentials" message.
        if (repos.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot resolve versions for {packageName}: {_sourceProvider.DescribeNoEligibleSources(packageName)}.");
        }

        return [.. versions];
    }

    /// <summary>
    /// Resolves a dependency's declared <see cref="VersionRange"/> to a single concrete version to install by
    /// selecting the lowest available version that satisfies the range on the configured sources — matching
    /// NuGet's lowest-applicable resolution. The declared lower bound is never assumed to exist: a range such
    /// as <c>[1.2.3, )</c> is satisfied by 1.2.3 only if a source actually offers it, otherwise the next higher
    /// available version is selected (a mirror may carry 1.3.0 but not 1.2.3). This also honors floating ranges.
    /// The candidate set includes UNLISTED versions, because a package legitimately pins an exact dependency
    /// version that the publisher has unlisted (e.g. Windows App SDK experimental builds unlist their
    /// <c>.Runtime</c>/<c>.Foundation</c> sub-packages) and such a pin must still resolve. Returns null ONLY
    /// when the range constrains nothing (a version-less or fully unbounded dependency) — a deliberate skip.
    /// A BOUNDED range that no available version satisfies means a required transitive package cannot be
    /// resolved, so it throws instead of returning null (which both callers read as "omit this dependency"):
    /// otherwise the graph path would report success with a package missing and the install path would
    /// silently skip it. The thrown reason is specific — a source that could have satisfied the range failed
    /// to answer, no source was eligible at all (empty feed list or a <c>packageSourceMapping</c> exclusion),
    /// or eligible sources offered no satisfying version — so the graph path can surface it (fail loudly) and
    /// the install path catch it as a non-fatal per-dependency warning.
    /// </summary>
    private async Task<string?> ResolveDependencyVersionAsync(string packageId, VersionRange? range, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        if (range is null)
        {
            return null;
        }

        // A dependency declared with no version (`<dependency id="X" />`) parses as an unbounded range. NuGet
        // treats that as an unconstrained REQUIRED dependency and resolves the lowest available version (while
        // warning NU1602); winapp does the same rather than silently omitting it, which would report a
        // successful install of a graph that is missing a declared package. An unbounded range that cannot be
        // resolved fails loudly through the same diagnostics as any other range.

        // Resolve every bounded/floating range against the versions the sources actually offer and pick the
        // lowest one that satisfies it (NuGet's lowest-applicable rule). Never shortcut to the declared lower
        // bound: it may be excluded by the range (an exclusive bound) or simply absent from the source.
        var candidates = await GetCandidateVersionsForRangeAsync(packageId, cacheContext, cancellationToken);
        var best = range.FindBestMatch(candidates.Versions);

        // FindBestMatch treats a float as a preference, not a hard constraint: for 1.* with only 2.0.0 available
        // it returns 2.0.0, which is outside the floated band. Re-check the selection with the float-aware
        // predicate so an out-of-band transitive version is never installed; a rejected match falls through to
        // the "no satisfying version" diagnostics below.
        if (best is not null && RangeSatisfiesWithFloat(range, best))
        {
            return best.ToNormalizedString();
        }

        // Nothing the sources offered satisfied the range. Before failing, and ONLY when the sources could not
        // actually answer, check whether a FULLY installed cache entry already satisfies it. A completed entry
        // is a package winapp (or NuGet) previously extracted, so a graph that is already on disk restores
        // without contacting a feed at all — which is what makes an offline restore, or one under a
        // packageSourceMapping that excludes a transitive package, work.
        //
        // The "could not answer" gate matters: when every eligible source WAS queried successfully and simply
        // offers no satisfying version, that is an authoritative answer and must still fail. Consulting the
        // cache there would let a version left behind by a different feed turn a failing online restore into a
        // success, which is exactly the resolution change this fallback is meant not to make. Folding cached
        // versions into the candidate set above would be wrong for the same reason.
        if (candidates.Error is not null || candidates.EligibleSourceCount == 0)
        {
            var cached = FindSatisfyingCachedVersion(packageId, range);
            if (cached is not null)
            {
                return cached;
            }
        }

        // A bounded range that matches no available version means a REQUIRED transitive package cannot be
        // resolved, so fail loudly instead of returning null (which both callers read as "omit this
        // dependency"): the graph path would otherwise report success with a package missing and the install
        // path would silently skip it. Distinguish the causes so the error is actionable.
        if (candidates.Error is not null)
        {
            // A source that could have satisfied the range could not be queried (feed/auth/network error);
            // surface it rather than masking a real failure as a missing dependency.
            // The source exception is deliberately NOT attached as the inner exception: its message carries
            // the raw feed URL, and sinks that unwrap a chain (UpdateCommand's GetBaseException().Message, the
            // telemetry exception event) would reprint the signed query string this redaction just removed.
            // The redacted text keeps everything actionable — which source failed, and why.
            throw new InvalidOperationException(
                $"Could not resolve a version for dependency '{packageId}' satisfying '{range}': source '{candidates.ErrorSource}' could not be queried: {NugetErrorMessage.Redact(candidates.Error.Message)}");
        }

        if (candidates.EligibleSourceCount == 0)
        {
            // No source was eligible at all — an empty feed list or a packageSourceMapping exclusion left the
            // transitive package with nowhere to resolve from. Reuse the mapping-aware diagnosis so the error
            // points at the specific nuget.config fix (matching the download / direct-dependency paths).
            throw new InvalidOperationException(
                $"Cannot resolve dependency '{packageId}' (required version '{range}'): {_sourceProvider.DescribeNoEligibleSources(packageId)}.");
        }

        // Eligible sources were queried and answered, but none offers a version satisfying the range.
        throw new InvalidOperationException(
            $"Cannot resolve dependency '{packageId}': no version offered by the configured NuGet sources satisfies '{range}'.");
    }

    /// <summary>
    /// Finds the lowest FULLY installed version of <paramref name="packageId"/> in the NuGet global packages
    /// folder that satisfies <paramref name="range"/>, or null when none does. Used only as a fallback once
    /// the configured sources have failed to produce a satisfying version, so a dependency graph that is
    /// already extracted on disk can be restored without contacting a feed.
    ///
    /// Only entries carrying NuGet's completion marker count, matching the predicate the rest of the service
    /// gates "already installed" on: a partial folder left by an interrupted extraction must not be accepted
    /// as a resolvable version. Selection mirrors the online path — lowest applicable, re-checked with the
    /// float-aware predicate so a floating range never resolves to a version outside its band.
    /// </summary>
    private string? FindSatisfyingCachedVersion(string packageId, VersionRange range)
    {
        // The id arrives from a dependency entry in another package's manifest, so prove it is a plain single
        // path segment before it is used to locate a folder: a rooted or separator-bearing value must not be
        // able to point the probe outside the cache. A cache probe has no business throwing, so an unusable id
        // simply means "nothing cached" and the caller falls through to its regular source-based diagnostics.
        var normalizedPackageId = packageId.ToLowerInvariant();
        if (!PackageIdValidator.IsValidPackageId(normalizedPackageId)
            || Path.IsPathRooted(normalizedPackageId)
            || normalizedPackageId.Contains(Path.DirectorySeparatorChar)
            || normalizedPackageId.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        // Locate the package's cache folder by enumerating for it rather than composing a path. This keeps the
        // probe strictly read-only — it must never create a directory in the user's global packages folder as
        // a side effect of a lookup that misses — and the id is validated above, so it carries no wildcard
        // characters and matches at most the one folder.
        var packageRoot = GetNuGetGlobalPackagesDir()
            .EnumerateDirectories(normalizedPackageId)
            .FirstOrDefault();
        if (packageRoot is null)
        {
            return null;
        }

        // The folder name is NuGet's normalized version. A directory that does not parse is not a cache entry
        // this service wrote, and one without the completion marker is a partial extraction, so neither counts
        // as a resolvable version.
        var installed = packageRoot.EnumerateDirectories()
            .Where(versionDir => NuGetVersion.TryParse(versionDir.Name, out _) && HasCompletionMarker(versionDir))
            .Select(versionDir => NuGetVersion.Parse(versionDir.Name))
            .ToList();

        if (installed.Count == 0)
        {
            return null;
        }

        var best = range.FindBestMatch(installed);
        return best is not null && RangeSatisfiesWithFloat(range, best)
            ? best.ToNormalizedString()
            : null;
    }

    /// <summary>
    /// The candidate versions of a package collected across eligible sources, the number of sources that were
    /// eligible (0 signals a packageSourceMapping exclusion or an empty feed list), plus the last source
    /// failure (if any) so the caller can tell "no version satisfies the range", "no source was eligible" and
    /// "a source could not be queried" apart.
    /// </summary>
    private readonly record struct CandidateVersionsResult(IReadOnlyList<NuGetVersion> Versions, Exception? Error, string? ErrorSource, int EligibleSourceCount);

    /// <summary>
    /// Collects the candidate versions of a package across every eligible source, for resolving a dependency's
    /// declared version range to a concrete version. Includes UNLISTED versions: a package legitimately pins an
    /// exact dependency version that its publisher has unlisted (e.g. Windows App SDK experimental builds unlist
    /// their <c>.Runtime</c>/<c>.Foundation</c> sub-packages), and such a pinned dependency must still resolve —
    /// unlike a "latest version" decision, which excludes unlisted versions on purpose. Unlike
    /// <see cref="GetListedVersionsAsync"/> — which feeds a "latest" MAX decision and therefore fails closed if
    /// any source errors — this tolerates a per-source failure and moves on, matching the source-by-source
    /// failover the dependency paths already use: another eligible source may still offer a version that
    /// satisfies the range. The last such failure is still reported back so the caller can surface it when NO
    /// candidate satisfies the range (rather than masking a feed/authentication error as a silent skip).
    /// </summary>
    private async Task<CandidateVersionsResult> GetCandidateVersionsForRangeAsync(string packageId, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var versions = new HashSet<NuGetVersion>();
        Exception? lastError = null;
        string? lastErrorSource = null;

        var repos = _sourceProvider.GetRepositoriesForPackage(packageId);
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Include unlisted versions: a dependency can pin an exact version its publisher has unlisted
                // (e.g. Windows App SDK experimental .Runtime/.Foundation sub-packages), and that pin must
                // still resolve. Unlisted only means "hidden from search/latest", not unavailable. A source
                // that exposes only PackageBaseAddress (no registration resource) is enumerated via its flat
                // container rather than skipped, so ranges still resolve against such feeds.
                foreach (var version in await GetSourceVersionsAsync(repo, packageId, includeUnlisted: true, cacheContext, cancellationToken))
                {
                    versions.Add(version);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagate genuine cancellation instead of masking it as "no versions found".
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort: a source we cannot query contributes no versions; another eligible source may
                // still satisfy the range (the dependency paths already fail over source-by-source). Remember
                // the failure so the caller can distinguish it from a clean "no satisfying version" when
                // nothing ends up matching.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
            }
        }

        return new CandidateVersionsResult([.. versions], lastError, lastErrorSource, repos.Count);
    }

    /// <summary>
    /// Enumerates a single source's versions of a package. Prefers the registration-backed
    /// <see cref="PackageMetadataResource"/> so <paramref name="includeUnlisted"/> is honored (the "latest"
    /// path relies on excluding unlisted versions, the dependency-range path on including them). A v3 HTTP
    /// feed that exposes only <c>PackageBaseAddress</c> advertises no registration resource, yet still hands
    /// back a non-functional <see cref="PackageMetadataResource"/> whose query throws — so this probes the
    /// service index first and, when no registration resource is advertised, reads versions from the
    /// flat-container <see cref="FindPackageByIdResource"/> instead, which every v3 source must support. That
    /// keeps latest/range resolution working against such private feeds (they can already restore pinned
    /// packages). Local/v2 feeds have no service index but a working metadata resource, so they keep using it.
    /// The flat container carries no listed flag, so the fallback cannot filter unlisted versions — acceptable
    /// because a feed without registration exposes no listed/unlisted signal at all. Cancellation is
    /// propagated; any other source failure is left to the caller's fail-closed / best-effort handling.
    /// </summary>
    private static async Task<IReadOnlyList<NuGetVersion>> GetSourceVersionsAsync(
        SourceRepository repo,
        string packageId,
        bool includeUnlisted,
        SourceCacheContext cacheContext,
        CancellationToken cancellationToken)
    {
        // Only a v3 HTTP source has a service index; when it advertises no registration resource its
        // PackageMetadataResource is non-functional (GetMetadataAsync throws), so route it to the flat
        // container. A null service index means a local/v2 feed, whose metadata resource works — keep it.
        // Probe with NuGet's own public, ordered registration service-type list so every registration shape —
        // including RegistrationsBaseUrl/Versioned and any future types NuGet adds — is recognized. A
        // hand-maintained subset could omit an advertised type (e.g. .../Versioned), misclassify a
        // registration-backed feed as flat-container-only, and let an unlisted version be picked as latest.
        var serviceIndex = await repo.GetResourceAsync<ServiceIndexResourceV3>(cancellationToken);
        var registrationUnavailable = serviceIndex is not null
            && serviceIndex.GetServiceEntryUri(ServiceTypes.RegistrationsBaseUrl) is null;

        if (!registrationUnavailable)
        {
            var metadataResource = await repo.GetResourceAsync<PackageMetadataResource>(cancellationToken);
            if (metadataResource is not null)
            {
                var metadata = await metadataResource.GetMetadataAsync(
                    packageId,
                    includePrerelease: true,
                    includeUnlisted,
                    cacheContext,
                    Logger,
                    cancellationToken);
                return [.. metadata.Select(m => m.Identity.Version)];
            }
        }

        // No registration resource (a PackageBaseAddress-only feed): enumerate versions from the flat
        // container, which every v3 source exposes, so such a feed still resolves latest/range versions
        // instead of contributing nothing.
        var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
        if (byIdResource is null)
        {
            return [];
        }

        var allVersions = await byIdResource.GetAllVersionsAsync(packageId, cacheContext, Logger, cancellationToken);
        return allVersions is null ? [] : [.. allVersions];
    }

    /// <inheritdoc />
    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
        => GetPackageDependenciesAsync(packageName, version, [], cancellationToken);

    private async Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, List<string> resolutionPath, CancellationToken cancellationToken)
    {
        // Scope the process-wide cache to the effective configuration (feeds/global folder/mapping), not just
        // package/version: dependency results depend on the configured sources, so a bare package/version key
        // would let a lookup after SetConfigRoot (or another service instance with a different private feed)
        // return dependencies resolved against the previous source hierarchy.
        var cacheKey = $"{_sourceProvider.ConfigScopeKey}\n{packageName}/{version}";
        if (DependencyCache.TryGetValue(cacheKey, out var cached))
        {
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        // Reject a cyclic graph (A -> B -> A) up front. A cache entry is only published after its whole
        // subtree resolves, so a package that reappears in the active resolution chain would re-enter before
        // it is cached and recurse until the stack overflows. NuGet dependency graphs are required to be
        // acyclic, so surface an actionable error naming the chain instead of crashing.
        if (resolutionPath.Contains(packageName, StringComparer.OrdinalIgnoreCase))
        {
            var cycle = string.Join(" -> ", resolutionPath.Append(packageName));
            throw new InvalidOperationException(
                $"Circular package dependency detected: {cycle}. NuGet dependency graphs must be acyclic.");
        }

        NugetSourceProvider.EnsureCredentialService();
        using var cacheContext = new SourceCacheContext();
        var directDeps = await FetchDirectDependenciesAsync(packageName, version, cacheContext, cancellationToken);

        // Recursively resolve transitive dependencies, tracking this package on the active resolution path so
        // a cycle deeper in the graph is detected. Removing it on the way back out keeps unrelated branches
        // that legitimately share a package (a diamond) from being misread as a cycle.
        resolutionPath.Add(packageName);
        try
        {
            var allDeps = new Dictionary<string, string>(directDeps, StringComparer.OrdinalIgnoreCase);
            foreach (var (depId, depVersion) in directDeps)
            {
                var transitiveDeps = await GetPackageDependenciesAsync(depId, depVersion, resolutionPath, cancellationToken);
                foreach (var (transitiveId, transitiveVersion) in transitiveDeps)
                {
                    // First-resolution-wins: the flattened set keeps the version chosen by the first branch to
                    // resolve this transitive id and does not globally reconcile it against other branches'
                    // ranges. In a diamond where two branches pin the same id to different versions, the
                    // returned map can therefore carry a version that violates the other branch's range. This
                    // is a deliberate limitation of winapp's curated-SDK-graph scope (it does not implement
                    // NuGet's full graph unification); documented in docs/usage.md under private feeds.
                    allDeps.TryAdd(transitiveId, transitiveVersion);
                }
            }

            DependencyCache[cacheKey] = allDeps;
            return new Dictionary<string, string>(allDeps, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            resolutionPath.RemoveAt(resolutionPath.Count - 1);
        }
    }

    private async Task<Dictionary<string, string>> FetchDirectDependenciesAsync(string packageName, string version, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nugetVersion = ParseVersion(packageName, version);
        var repos = _sourceProvider.GetRepositoriesForPackage(packageName);
        Exception? lastError = null;
        string? lastErrorSource = null;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FindPackageByIdDependencyInfo? dependencyInfo;
            try
            {
                // Acquiring the resource loads the source's service index, which can throw for an
                // unreachable/unauthorized source; keep it inside the try so we fail over instead.
                var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                if (byIdResource is null)
                {
                    continue;
                }

                dependencyInfo = await byIdResource.GetDependencyInfoAsync(packageName, nugetVersion, cacheContext, Logger, cancellationToken);
            }
            catch (Exception ex) when (ex is FatalProtocolException or IOException or UnauthorizedAccessException)
            {
                // A canceled nuspec fetch can be surfaced as a PackageNotFoundProtocolException (a
                // FatalProtocolException) once retries are exhausted; preserve cancellation instead of
                // recording it as a source failure and later throwing InvalidOperationException. This
                // matches the contract enforced in GetListedVersionsAsync.
                cancellationToken.ThrowIfCancellationRequested();

                // Source unreachable/unauthorized; remember why and try the next one. IO/access failures are
                // included because a local-folder source reads the package straight off disk, so an
                // unreadable or locked feed folder surfaces as those rather than a protocol error — and that
                // must fail over to the next eligible source exactly like an unreachable HTTP feed, not abort
                // resolution for a package a later source can still provide.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
                continue;
            }

            if (dependencyInfo is null)
            {
                // This source responded that it does not have the requested version; try the next one.
                continue;
            }

            foreach (var group in dependencyInfo.DependencyGroups)
            {
                foreach (var dependency in group.Packages)
                {
                    if (string.IsNullOrEmpty(dependency.Id)
                        || IgnoredDependencyPrefixes.Any(p => dependency.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // A bounded range that cannot be resolved now throws (surfacing loudly here so the graph
                    // is never reported complete with a required package missing); a null result means only a
                    // version-less / unbounded dependency, which is skipped.
                    var resolvedVersion = await ResolveDependencyVersionAsync(dependency.Id, dependency.VersionRange, cacheContext, cancellationToken);
                    if (!string.IsNullOrEmpty(resolvedVersion))
                    {
                        dependencies.TryAdd(dependency.Id, resolvedVersion);
                    }
                }
            }

            // Dependencies resolved from the first source that has the package.
            return dependencies;
        }

        // No source produced dependency metadata. If at least one source failed with a protocol error
        // (unreachable/401/403/network), surface it rather than returning an empty graph: a caller would
        // otherwise treat "no dependencies" as success and silently skip installing missing transitive
        // packages. An empty result is only returned when every source cleanly reported the package absent.
        if (lastError != null)
        {
            // The source exception is deliberately NOT attached as the inner exception: its message carries
            // the raw feed URL, and sinks that unwrap a chain (UpdateCommand's GetBaseException().Message, the
            // telemetry exception event) would reprint the signed query string this redaction just removed.
            // The redacted text keeps everything actionable — which source failed, and why.
            throw new InvalidOperationException(
                $"Failed to resolve dependencies for {packageName} {version} from the configured NuGet sources. Last error from source '{lastErrorSource}': {NugetErrorMessage.Redact(lastError.Message)}");
        }

        // No source was even eligible. Fail closed (matching the download path) instead of reporting a
        // dependency-free graph, which a caller would treat as success while required transitive packages
        // remain uninstalled. The reason is either an empty feed list or a packageSourceMapping exclusion.
        if (repos.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot resolve dependencies for {packageName} {version}: {_sourceProvider.DescribeNoEligibleSources(packageName)}.");
        }

        return dependencies;
    }

    public static int CompareVersions(string a, string b)
    {
        // Prefer correct NuGet SemVer 2.0 ordering whenever both inputs parse as NuGet versions. This
        // accounts for prerelease tags (e.g. 1.0.0-preview1 < 1.0.0-preview2 < 1.0.0), which the plain
        // numeric-segment comparison below cannot distinguish (it parses tags as 0, making them equal).
        if (NuGetVersion.TryParse(a, out var va) && NuGetVersion.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        // Fallback for inputs that are not valid NuGet versions: compare numeric segments.
        var ap = a.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            int ai = i < ap.Length && int.TryParse(ap[i], out var av) ? av : 0;
            int bi = i < bp.Length && int.TryParse(bp[i], out var bv) ? bv : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }
        return 0;
    }
}
