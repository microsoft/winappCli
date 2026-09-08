// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using System.Diagnostics.CodeAnalysis;

namespace WinApp.Cli.Services;

/// <summary>
/// Resolves NuGet package sources, credentials and <c>&lt;packageSourceMapping&gt;</c> from the user's
/// <c>nuget.config</c> hierarchy. The hierarchy is rooted at the process working directory by default, or
/// at the explicit project/config directory supplied via <see cref="SetConfigRoot"/> (e.g. by
/// <c>init &lt;dir&gt;</c> / <c>restore &lt;dir&gt;</c> / <c>--config-dir &lt;dir&gt;</c>). Owns the
/// source/configuration
/// concern for <see cref="NugetService"/> so private/custom feeds and mirrors are honored when restoring
/// SDK packages, and so this logic can be tested independently of package download/version resolution.
/// </summary>
internal sealed class NugetSourceProvider
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // Lazy (ExecutionAndPublication) guarantees the credential-service setup runs exactly once AND that
    // every caller blocks until it has fully completed. Publishing an "initialized" flag before setup
    // finished (e.g. via Interlocked.Exchange) would let a concurrent NuGet operation build its HTTP
    // resources against a not-yet-configured credential service and hit a private feed anonymously.
    private static readonly Lazy<bool> CredentialServiceInitializer = new(() =>
    {
        // Prompting is only safe on a real interactive console. Treat the process as non-interactive
        // whenever stdin OR stdout is redirected, it isn't running in a user-interactive session (a Windows
        // service, a task runner), or a CI marker is set — mirroring WorkspaceSetupService's interactive
        // gating. Relying on stdin alone would let a credential-provider plugin raise a prompt that blocks
        // the process indefinitely under a task runner / service / unrecognized CI whose stdin isn't
        // redirected.
        var nonInteractive = Console.IsInputRedirected
            || Console.IsOutputRedirected
            || !Environment.UserInteractive
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

        DefaultCredentialServiceUtility.SetupDefaultCredentialService(Logger, nonInteractive);
        return true;
    });

    private readonly ICurrentDirectoryProvider _currentDirectoryProvider;

    // The directory the nuget.config hierarchy is resolved from. Null means "use the process working
    // directory"; SetConfigRoot overrides it for commands that select an explicit project/config dir.
    private DirectoryInfo? _configRoot;

    // All three caches are Lazy (default ExecutionAndPublication mode: thread-safe, initialized exactly
    // once) rather than plain '??=' fields. NugetSourceProvider is a DI singleton and WorkspaceSetupService
    // resolves versions for many packages concurrently (Task.WhenAll over GetLatestVersionAsync), so a
    // bare '??=' could race two threads into building duplicate providers/mappings or observing a
    // half-initialized cache. They are re-created by SetConfigRoot (before any concurrent work begins).
    private Lazy<ISettings> _settings;
    private Lazy<SourceRepositoryProvider> _sourceRepositoryProvider;
    private Lazy<PackageSourceMapping> _packageSourceMapping;
    private Lazy<string> _configScopeKey;

    public NugetSourceProvider(ICurrentDirectoryProvider currentDirectoryProvider)
    {
        _currentDirectoryProvider = currentDirectoryProvider;
        InitializeCaches();
    }

    [MemberNotNull(nameof(_settings), nameof(_sourceRepositoryProvider), nameof(_packageSourceMapping), nameof(_configScopeKey))]
    private void InitializeCaches()
    {
        _settings = new Lazy<ISettings>(() =>
            NuGet.Configuration.Settings.LoadDefaultSettings(
                root: _configRoot?.FullName ?? _currentDirectoryProvider.GetCurrentDirectory()));
        _sourceRepositoryProvider = new Lazy<SourceRepositoryProvider>(() =>
            new SourceRepositoryProvider(new PackageSourceProvider(Settings), Repository.Provider.GetCoreV3()));
        _packageSourceMapping = new Lazy<PackageSourceMapping>(() =>
            NuGet.Configuration.PackageSourceMapping.GetPackageSourceMapping(Settings));
        // A stable fingerprint of the effective source set + global packages folder + the FULL
        // packageSourceMapping rules, so callers that keep a process-wide cache keyed only by package/version
        // (e.g. the dependency cache in NugetService) can additionally scope it to THIS configuration and
        // never serve results resolved against a different config root, private feed, global folder or
        // package-to-source mapping after SetConfigRoot switches it.
        _configScopeKey = new Lazy<string>(() =>
        {
            var globalFolder = SettingsUtility.GetGlobalPackagesFolder(Settings);
            // Preserve source ORDER: dependency resolution returns the graph from the FIRST eligible source
            // that has the package (see NugetService.FetchDirectDependenciesAsync), so two configs with the
            // same feeds listed in a different order can resolve DIFFERENT dependency graphs and must not
            // share the cache. Sorting here would collapse them to one key and serve the wrong graph.
            // Exclude insecure plain-HTTP sources (mirroring GetRepositories): they are not part of the
            // EFFECTIVE eligible set, so two roots that differ only in allowInsecureConnections resolve from
            // different feeds and must get distinct keys — otherwise the opted-in root's cached dependencies
            // could be served to a non-opted-in root, bypassing the HTTP rejection.
            // Include the source PROPERTIES that change how a repository behaves, not just its name and URL:
            // two roots listing the same feed at a different protocolVersion talk to it through a different
            // protocol (v2 vs v3) and can resolve different graphs, and allowInsecureConnections decides
            // whether a plain-HTTP feed is usable at all. Sharing one cache entry across those would serve a
            // graph resolved under settings that do not apply.
            var sources = string.Join(
                ";",
                new PackageSourceProvider(Settings).LoadPackageSources()
                    .Where(s => s.IsEnabled && !IsInsecureSource(s))
                    .Select(s => $"{s.Name}|{s.Source}|v{s.ProtocolVersion}|insecure={s.AllowInsecureConnections}"));
            // Record the complete mapping entries (source key -> ordered patterns), not just whether mapping
            // is enabled: two configs with identical sources/global folder but different package-to-source
            // mappings resolve dependencies from different feeds and must not share the cache.
            var mapping = string.Join(
                ";",
                new PackageSourceMappingProvider(Settings).GetPackageSourceMappingItems()
                    .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(m => $"{m.Key}=>{string.Join(",", m.Patterns.Select(p => p.Pattern).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))}"));
            return $"gpf={globalFolder}\nsources={sources}\nmapping={mapping}";
        });
    }

    /// <summary>
    /// Overrides the directory the nuget.config hierarchy is resolved from. Commands that accept an
    /// explicit project/config directory (e.g. <c>init &lt;dir&gt;</c>, <c>restore --config-dir &lt;dir&gt;</c>)
    /// call this so the user's project-level nuget.config — private feeds, credentials and
    /// <c>globalPackagesFolder</c> — is honored even when the process working directory differs. It is
    /// invoked synchronously at the start of a workspace setup, before any concurrent version/download
    /// work begins, so re-creating the (not-yet-evaluated) caches here is safe.
    /// </summary>
    internal void SetConfigRoot(DirectoryInfo configRoot)
    {
        _configRoot = configRoot;
        InitializeCaches();
    }

    /// <summary>
    /// The NuGet settings resolved from the user's nuget.config hierarchy. Exposed so consumers can read
    /// the global packages folder, client policy, etc. from the same configuration.
    /// </summary>
    internal ISettings Settings => _settings.Value;

    /// <summary>
    /// A stable fingerprint of the effective configuration (global packages folder, enabled sources in their
    /// configured order and the full <c>&lt;packageSourceMapping&gt;</c> entries). Consumers that maintain a
    /// process-wide, static cache keyed only by package identity use this to additionally scope entries to the
    /// current config root/feed set, so a cache populated under one <c>nuget.config</c> is never reused under
    /// another after <see cref="SetConfigRoot"/> switches it. Source order is part of the fingerprint because
    /// dependency resolution is first-source-wins. Recomputed whenever the caches are re-created.
    /// </summary>
    internal string ConfigScopeKey => _configScopeKey.Value;

    /// <summary>
    /// Configures NuGet's default credential service so authenticated (private) feeds work using
    /// credentials stored in nuget.config, environment-based credentials, or credential-provider
    /// plugins. Interactive prompting is only enabled for real interactive terminals. Setup runs
    /// exactly once and every caller blocks until it has completed, so concurrent NuGet operations
    /// never observe a half-initialized credential service.
    /// </summary>
    internal static void EnsureCredentialService() => _ = CredentialServiceInitializer.Value;

    private IReadOnlyList<SourceRepository> GetAllConfiguredRepositories() =>
        [.. _sourceRepositoryProvider.Value.GetRepositories()];

    /// <summary>
    /// The configured sources with insecure plain-HTTP feeds removed. The restored packages are executable
    /// SDK tools, so a plaintext feed is a man-in-the-middle code-substitution vector. NuGet's low-level
    /// protocol APIs (unlike its <c>restore</c> command) don't enforce this, so any <c>http://</c> source
    /// that has not explicitly opted in with <c>allowInsecureConnections="true"</c> is dropped; HTTPS and
    /// local-folder sources are always kept.
    /// </summary>
    private IReadOnlyList<SourceRepository> GetRepositories() =>
        [.. GetAllConfiguredRepositories().Where(r => !IsInsecureSource(r.PackageSource))];

    // A plain-HTTP source (non-HTTPS, non-local) that has not opted in to insecure connections. IsHttp is
    // true for both http and https, so require !IsHttps to isolate plaintext http://.
    private static bool IsInsecureSource(PackageSource source) =>
        source.IsHttp && !source.IsHttps && !source.AllowInsecureConnections;

    private PackageSourceMapping PackageSourceMapping => _packageSourceMapping.Value;

    /// <summary>
    /// Returns the configured package sources eligible to serve <paramref name="packageId"/>, honoring
    /// <c>&lt;packageSourceMapping&gt;</c> when it is enabled. When mapping is enabled but no source is
    /// mapped to the package, an empty list is returned (matching NuGet restore semantics, which fails
    /// rather than falling back to an unmapped feed).
    /// </summary>
    internal IReadOnlyList<SourceRepository> GetRepositoriesForPackage(string packageId)
    {
        var repositories = GetRepositories();

        var mapping = PackageSourceMapping;
        if (!mapping.IsEnabled)
        {
            return repositories;
        }

        var mappedSources = mapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return [];
        }

        var allowed = new HashSet<string>(mappedSources, StringComparer.OrdinalIgnoreCase);
        return [.. repositories.Where(r => allowed.Contains(r.PackageSource.Name))];
    }

    /// <summary>
    /// Explains why no source was eligible to serve <paramref name="packageId"/>, distinguishing the
    /// distinct causes — no sources configured at all, the package matching no
    /// <c>&lt;packageSourceMapping&gt;</c> pattern, or the package being mapped to a source that is
    /// disabled/missing — so the error points the user at the right nuget.config fix.
    /// </summary>
    internal string DescribeNoEligibleSources(string packageId)
    {
        // If there are no enabled sources at all, mapping is irrelevant — an empty eligible set can only
        // mean the feed list itself is empty, regardless of whether packageSourceMapping is enabled.
        if (GetRepositories().Count == 0)
        {
            // Distinguish "nothing configured" from "the only configured source(s) were dropped because they
            // are insecure plain-HTTP feeds", so the user knows to switch to HTTPS or opt in rather than add
            // a source.
            var insecure = GetAllConfiguredRepositories()
                .Where(r => IsInsecureSource(r.PackageSource))
                .Select(r => r.PackageSource.Name)
                .ToList();
            if (insecure.Count > 0)
            {
                return $"the only configured NuGet source(s) [{string.Join(", ", insecure)}] use plain HTTP; winapp refuses to download executable SDK packages over an insecure connection (switch the source to HTTPS, or set allowInsecureConnections=\"true\" on it in nuget.config to opt in)";
            }

            return "no enabled NuGet sources are configured (add or enable a source in the <packageSources> section of your nuget.config)";
        }

        // Sources exist, so packageSourceMapping is what pruned them. Separate "the package matches no
        // mapping pattern" from "the package is mapped, but to a source that isn't enabled/configured"
        // (e.g. the mapped key names a disabled or misspelled source) — the fixes are different.
        var mappedSources = PackageSourceMapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return $"no <packageSourceMapping> pattern maps '{packageId}' to a source (add a matching entry in nuget.config)";
        }

        var mapped = string.Join(", ", mappedSources);

        // A source can be configured AND enabled yet still be excluded from the eligible set because it is an
        // insecure plain-HTTP feed (dropped by GetRepositories). When the package maps only to such source(s),
        // the non-empty check above bypassed the dedicated insecure-source message, so detect it here and give
        // the HTTPS / opt-in guidance rather than the misleading "not enabled/configured" (which is meant for
        // disabled or misspelled mapped keys).
        var mappedSet = new HashSet<string>(mappedSources, StringComparer.OrdinalIgnoreCase);
        var insecureMapped = GetAllConfiguredRepositories()
            .Where(r => mappedSet.Contains(r.PackageSource.Name) && IsInsecureSource(r.PackageSource))
            .Select(r => r.PackageSource.Name)
            .ToList();
        if (insecureMapped.Count > 0)
        {
            return $"'{packageId}' is mapped to source(s) [{string.Join(", ", insecureMapped)}] that use plain HTTP; winapp refuses to download executable SDK packages over an insecure connection (switch the source to HTTPS, or set allowInsecureConnections=\"true\" on it in nuget.config to opt in)";
        }

        return $"'{packageId}' is mapped to source(s) [{mapped}] that are not enabled/configured (enable or fix the mapped source in the <packageSources> section of your nuget.config)";
    }
}
