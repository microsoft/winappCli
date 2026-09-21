// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using WinApp.Cli.Services;

/// <summary>
/// Builds and memoizes the <see cref="SearchEngine"/> that backs
/// <c>winapp find-ui</c>. Scenario data comes from the registered
/// <see cref="ISearchProvider"/>s (WinUI Gallery + Community Toolkit + Reactor),
/// each of which serves a fresh per-user cache, a GitHub fetch, or — when neither is
/// available — the corpus baked into the binary. The curated core patterns are baked in
/// separately (they have no upstream endpoint).
///
/// Because every provider has that embedded floor, an offline cold start returns real
/// results rather than failing. <see cref="ControlsDataUnavailableException"/> is now
/// reserved for the genuinely broken case: no cache, no network, and no usable embedded
/// snapshot.
/// </summary>
internal interface IControlsSearchService
{
    /// <summary>
    /// Returns a configured engine, building it lazily on first use and memoizing
    /// it thereafter. Pass <paramref name="forceRefresh"/> to bypass both the
    /// in-memory engine and the on-disk cache and re-fetch from GitHub.
    /// When <paramref name="allowCoreOnly"/> is true and no network corpus can be
    /// loaded (offline cold cache), a core-only engine is returned instead of
    /// throwing — the embedded core patterns are always available, so
    /// <c>--list</c>, <c>--source core</c>, and <c>--id &lt;core-id&gt;</c> keep
    /// working offline. <paramref name="coreOnly"/> goes further: when true the
    /// request is satisfiable by the embedded core patterns <i>alone</i> (an
    /// explicit <c>--source core</c> search, or an all-core <c>--id</c> set), so
    /// the network providers are skipped entirely — no load, no fetch, and the
    /// <paramref name="onFetchStarting"/> notice never fires. <paramref name="includeReactor"/> gates the opt-in Reactor
    /// source: when false (the default — e.g. a plain search or <c>--list</c>) the
    /// Reactor provider is neither loaded nor fetched, so its C#-only samples never
    /// pollute results for standard XAML apps; the command sets it true only for a
    /// <c>--source reactor</c> search or a <c>reactor-*</c> <c>--id</c> fetch. The
    /// with- and without-Reactor engines are memoized separately.
    /// <paramref name="onFetchStarting"/> is invoked (with a
    /// provider display name) the first time any provider starts a network fetch,
    /// so the caller can show a one-time "fetching…" notice; it is never invoked
    /// when everything is served from cache or the memoized engine.
    /// </summary>
    Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, bool allowCoreOnly = false, bool coreOnly = false, bool includeReactor = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Least-fresh origin among the providers loaded by the most recent
    /// <see cref="GetEngineAsync"/> call. Reported to callers so an offline fallback is
    /// distinguishable from a live answer — if one source came from the network and
    /// another from the embedded floor, the embedded one is what the caller needs to know
    /// about. A core-only request reports <see cref="CorpusOrigin.Embedded"/>: the curated
    /// core patterns are compiled into the binary and never fetched, so they belong in the
    /// embedded freshness tier. <see cref="CorpusOrigin.None"/> is reserved for the one
    /// case where nothing loaded at all.
    /// </summary>
    CorpusOrigin LoadedOrigin { get; }

    /// <summary>Delete every provider's per-user cache so the next load re-fetches.</summary>
    void ClearCache();
}

/// <summary>Thrown when no scenario data could be loaded — typically a cold cache with no network.</summary>
internal sealed class ControlsDataUnavailableException : Exception
{
    public ControlsDataUnavailableException(string message) : base(message) { }
}

internal sealed class ControlsSearchService : IControlsSearchService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISearchProvider[] _providers;
    private SearchEngine? _engine;
    // Reactor is opt-in, so the corpus differs by whether it's included. Memoize
    // the two shapes separately: a default (Gallery + Toolkit + core) engine and a
    // with-Reactor engine, so switching between a normal search and a
    // `--source reactor` search within one process doesn't clobber either cache.
    private SearchEngine? _engineWithReactor;
    // The origin each memoized engine was built from. Kept beside the engine rather than
    // only in LoadedOrigin because a memoized hit returns without rebuilding: without
    // these, a core-only call (which reports the embedded tier) followed by a cache hit on
    // the full engine would keep reporting "embedded" for a network- or cache-backed
    // corpus, understating the freshness of the answer in --json and on stderr.
    private CorpusOrigin _engineOrigin = CorpusOrigin.None;
    private CorpusOrigin _engineWithReactorOrigin = CorpusOrigin.None;
    // A core-only engine (embedded patterns, no network) for requests satisfiable by
    // core alone (--source core, all-core --id). Deterministic embedded data, so it's
    // safe to memoize; kept separate from the network-backed engines above.
    private SearchEngine? _coreOnlyEngine;

    /// <summary>
    /// Least-fresh origin across the providers contributing to the memoized engine.
    /// Recomputed on each build; a memoized hit keeps reporting the origin the corpus was
    /// actually loaded from, which is what the caller wants to surface.
    /// </summary>
    public CorpusOrigin LoadedOrigin { get; private set; } = CorpusOrigin.None;

    /// <summary>Production constructor: providers are rooted at the managed
    /// global <c>.winapp</c> cache directory so environment/test path overrides
    /// (<c>WINAPP_CLI_CACHE_DIRECTORY</c>) and repo-wide path policy apply.</summary>
    public ControlsSearchService(IWinappDirectoryService directoryService)
        : this(ProviderRegistry.CreateProviders(
            Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "find-ui")))
    {
    }

    /// <summary>Test seam: inject providers directly (e.g. fakes with a temp cache).</summary>
    internal ControlsSearchService(ISearchProvider[] providers)
    {
        _providers = providers;
    }

    public async Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, bool allowCoreOnly = false, bool coreOnly = false, bool includeReactor = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default)
    {
        // Core-only requests (--source core, all-core --id) are satisfied entirely by
        // the embedded patterns — skip every network provider so there is no fetch and
        // the "fetching…" notice never fires. Returned before any provider is touched.
        if (coreOnly)
        {
            // The core patterns are baked into the binary and never fetched, so they are
            // an embedded corpus — not "nothing loaded". Reporting None here would make
            // the caller's `corpus` field null for a successful core result, which is
            // indistinguishable from a genuine load failure.
            LoadedOrigin = CorpusOrigin.Embedded;
            if (_coreOnlyEngine != null)
            {
                return _coreOnlyEngine;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_coreOnlyEngine == null)
                {
                    _coreOnlyEngine = new SearchEngine(
                        Array.Empty<Scenario>(), DataLoader.LoadCorePatterns(), new(), new());
                }

                return _coreOnlyEngine;
            }
            finally
            {
                _gate.Release();
            }
        }

        var memoized = includeReactor ? _engineWithReactor : _engine;
        if (memoized != null && !forceRefresh)
        {
            LoadedOrigin = includeReactor ? _engineWithReactorOrigin : _engineOrigin;
            return memoized;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            memoized = includeReactor ? _engineWithReactor : _engine;
            if (memoized != null && !forceRefresh)
            {
                LoadedOrigin = includeReactor ? _engineWithReactorOrigin : _engineOrigin;
                return memoized;
            }

            // A forced refresh re-fetches the corpus from GitHub and rewrites the
            // on-disk cache for every provider it loads. Invalidate BOTH memoized
            // engines up front so the OTHER variant (which this call won't rebuild)
            // can't keep serving pre-refresh, in-memory scenario data on a later
            // call — a `--refresh` must not leave one engine shape stale.
            if (forceRefresh)
            {
                _engine = null;
                _engineWithReactor = null;
                _engineOrigin = CorpusOrigin.None;
                _engineWithReactorOrigin = CorpusOrigin.None;
            }

            var allScenarios = new List<Scenario>();
            // Tags/keywords are namespaced by provider id ("{providerId}:{controlId}")
            // so colliding bare controlIds across sources (gallery + toolkit both
            // expose "colorpicker") don't overwrite each other.
            var allTags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var allKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            bool anyProviderEmpty = false;
            var leastFreshOrigin = CorpusOrigin.None;

            foreach (var provider in _providers)
            {
                // Reactor is opt-in. Unless this invocation explicitly needs it
                // (a --source reactor search or a reactor-* --id fetch), skip the
                // provider entirely — no load, no network fetch — so Reactor's
                // C#-only samples never compete in a default search for a standard
                // XAML app.
                if (!includeReactor && ProviderRegistry.IsReactorSource(provider.Id))
                {
                    continue;
                }

                var data = await provider.LoadAsync(forceRefresh, onFetchStarting, cancellationToken).ConfigureAwait(false);
                if (data.Scenarios.Length == 0)
                {
                    anyProviderEmpty = true;
                    continue;
                }
                leastFreshOrigin = Weakest(leastFreshOrigin, data.Origin);
                allScenarios.AddRange(data.Scenarios);
                foreach (var kv in data.Tags) allTags[$"{provider.Id}:{kv.Key}"] = kv.Value;
                foreach (var kv in data.Keywords) allKeywords[$"{provider.Id}:{kv.Key}"] = kv.Value;
            }

            if (allScenarios.Count == 0)
            {
                LoadedOrigin = CorpusOrigin.None;

                // Neither cache, network, nor embedded snapshot produced anything — the
                // embedded floor is missing or unreadable. The curated core patterns are
                // compiled in separately and always available, so requests a core-only
                // corpus can satisfy (--list, --source core, --id <core-id>) still work.
                // Not memoized: the network corpus may load on a later call, and this
                // degraded engine must not be pinned.
                if (allowCoreOnly)
                {
                    return new SearchEngine(
                        Array.Empty<Scenario>(),
                        DataLoader.LoadCorePatterns(),
                        new(),
                        new());
                }

                throw new ControlsDataUnavailableException(
                    "No WinUI control data could be loaded. find-ui serves the WinUI Gallery, " +
                    "Community Toolkit, and Reactor corpora from a snapshot baked into the CLI, " +
                    "refreshed from GitHub when reachable — if you are seeing this, that snapshot " +
                    "is missing or unreadable. Run with --refresh while online to repopulate the cache.");
            }

            LoadedOrigin = leastFreshOrigin;

            // Single corpus-boundary guard: strip terminal-control characters and drop
            // structurally-broken XAML / brace-unbalanced C# before anything downstream
            // (search, --id output, --json) can emit it. Runs once per engine build.
            ScenarioSanitizer.SanitizeAll(allScenarios);

            var engine = new SearchEngine(
                allScenarios.ToArray(),
                DataLoader.LoadCorePatterns(),
                allTags,
                allKeywords);

            // Only memoize a COMPLETE corpus. If a provider came back empty (its
            // cold-cache fetch failed), serve the partial result for this call but
            // don't cache it in-memory, so a later invocation re-attempts the
            // missing source instead of being pinned to a degraded engine. The
            // with- and without-Reactor corpora memoize into separate slots.
            if (!anyProviderEmpty)
            {
                if (includeReactor)
                {
                    _engineWithReactor = engine;
                    _engineWithReactorOrigin = leastFreshOrigin;
                }
                else
                {
                    _engine = engine;
                    _engineOrigin = leastFreshOrigin;
                }
            }
            return engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Least-fresh of two origins, ignoring <see cref="CorpusOrigin.None"/> (which means
    /// "nothing loaded" rather than a real source). The enum is ordered by freshness, so
    /// the lower value wins: one source falling back to the embedded floor is the fact
    /// worth reporting even if another was fetched live.
    /// </summary>
    private static CorpusOrigin Weakest(CorpusOrigin current, CorpusOrigin candidate)
    {
        if (current == CorpusOrigin.None) return candidate;
        if (candidate == CorpusOrigin.None) return current;
        return (CorpusOrigin)Math.Min((int)current, (int)candidate);
    }

    /// <summary>Delete every provider's per-user cache so the next load re-fetches.</summary>
    public void ClearCache()
    {
        _engine = null;
        _engineWithReactor = null;
        _coreOnlyEngine = null;
        _engineOrigin = CorpusOrigin.None;
        _engineWithReactorOrigin = CorpusOrigin.None;

        var failures = new List<Exception>();
        foreach (var provider in _providers)
        {
            try { provider.ClearCache(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Failed to clear one or more find-ui cache directories. This usually means a " +
                "cache file is locked by another process.",
                failures);
        }
    }

    public void Dispose() => _gate.Dispose();
}
