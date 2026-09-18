// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

/// <summary>
/// Where a provider's loaded corpus actually came from. Surfaced so callers can tell a
/// live result from the embedded floor — a stale-but-present corpus and a freshly
/// fetched one are very different things to an agent reasoning about the answer.
/// </summary>
internal enum CorpusOrigin
{
    /// <summary>No corpus loaded.</summary>
    None,

    /// <summary>Served from the corpus baked into the binary (no network, no cache).</summary>
    Embedded,

    /// <summary>Served from this machine's previously fetched cache.</summary>
    Cache,

    /// <summary>Fetched from GitHub during this invocation.</summary>
    Network
}

/// <summary>
/// Data contributed by a search provider: its scenarios plus the per-control
/// tag and keyword dictionaries. Tag/keyword keys are bare controlIds — the
/// engine namespaces them by provider id (<c>{providerId}:{controlId}</c>).
/// </summary>
internal sealed record ProviderData(
    Scenario[] Scenarios,
    Dictionary<string, string[]> Tags,
    Dictionary<string, string[]> Keywords,
    CorpusOrigin Origin = CorpusOrigin.None)
{
    public static ProviderData Empty { get; } =
        new(Array.Empty<Scenario>(), new(), new(), CorpusOrigin.None);
}

/// <summary>
/// A source of WinUI scenarios (WinUI Gallery, Community Toolkit, …). A single
/// stable <see cref="Id"/> ties together everything downstream: it is the
/// <c>Scenario.Source</c> value, the on-disk cache subdirectory, the scenario
/// id prefix (<c>{Id}-…</c>), the <c>--source</c> token, and the composite
/// tag/keyword key namespace. Register a new provider in
/// <see cref="ProviderRegistry"/> and the rest of the tool picks it up.
/// </summary>
internal interface ISearchProvider
{
    /// <summary>Lowercase, stable identifier, e.g. <c>"gallery"</c> / <c>"toolkit"</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable heading used by <c>winapp find-ui --list</c>.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Load this provider's data. Serves the per-user cache when fresh, otherwise
    /// fetches from GitHub and primes the cache, and falls back to the corpus baked
    /// into the binary when neither is available — so a cold cache with no network
    /// still returns a usable corpus. <paramref name="onFetchStarting"/> is invoked
    /// (with <see cref="DisplayName"/>) immediately before a network fetch begins,
    /// so callers can show a one-time "fetching…" notice; a warm-cache load never
    /// invokes it.
    /// </summary>
    Task<ProviderData> LoadAsync(bool forceRefresh = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default);

    /// <summary>Delete this provider's cache directory so the next load re-fetches.</summary>
    void ClearCache();
}

/// <summary>
/// Boilerplate shared by every GitHub-backed provider: the on-disk cache
/// protocol (schema-version stamp + 24-hour TTL + atomic writes), the fetch flow,
/// and the embedded-snapshot floor that keeps the provider usable with no
/// network. Concrete providers only supply their identity and their GitHub
/// fetch. The cache root is injected so it can live under the managed
/// <c>.winapp</c> directory (and be redirected in tests).
/// </summary>
internal abstract class CachedProviderBase : ISearchProvider
{
    private readonly string _cacheRoot;
    internal CacheStorage? Storage { get; set; }

    protected CachedProviderBase(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    /// <summary>
    /// How long a cached corpus is served before the provider re-fetches. Upstream
    /// sample repos change daily, so a day is the longest window that still lets a
    /// user see today's samples today. A miss is cheap by construction: every failure
    /// path below falls back to the stale cache and then to the embedded floor, so a
    /// shorter TTL costs a fetch, never a working command.
    ///
    /// Overridable so tests can pin the freshness window. Without that seam a test for
    /// "cache is fresh but older than the bake" silently decays into a test of the
    /// fetch path as soon as the shipped bake date ages past the TTL.
    /// </summary>
    protected virtual TimeSpan CacheTtl => TimeSpan.FromHours(24);

    private string CacheDir => Path.Combine(_cacheRoot, Id);

    /// <summary>Fully-prepared GitHub fetch (tags already cleaned). Return
    /// <see cref="ProviderData.Empty"/> to leave the cache untouched.</summary>
    protected abstract Task<ProviderData> FetchAsync(CancellationToken cancellationToken);

    /// <summary>Hook to re-normalize tags read back from cache. Defaults to
    /// identity; providers that clean tags on write override this to match.</summary>
    protected virtual Dictionary<string, string[]> NormalizeTagsOnRead(
        Dictionary<string, string[]> tags) => tags;

    public async Task<ProviderData> LoadAsync(bool forceRefresh = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cached = TryReadCache();
            if (cached != null) return PreferNewerOf(cached.Value.Data, cached.Value.WrittenAt);
        }

        // Cold/stale cache (or forced): fetch from GitHub. Treat a thrown
        // transport/parse failure the same as an empty result so an offline run falls
        // through to the cache and then to the embedded snapshot rather than throwing.
        ProviderData fetched;
        try
        {
            // About to hit the network — signal the caller so it can show a
            // one-time "fetching…" notice. Warm-cache loads return above and
            // never reach here, so the notice only appears on a real fetch.
            // Best-effort: a throwing observer must never abort the fetch itself.
            try { onFetchStarting?.Invoke(DisplayName); }
            catch { /* the fetching notice is cosmetic; ignore observer failures */ }

            fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            fetched = ProviderData.Empty;
        }

        if (fetched.Scenarios.Length > 0)
        {
            await TryWriteCacheAsync(fetched, forceRefresh, cancellationToken).ConfigureAwait(false);
            return fetched with { Origin = CorpusOrigin.Network };
        }

        // Fetch failed (offline / proxy / upstream error). Fall back to any existing
        // cache rather than dropping the provider — a stale corpus beats no corpus.
        // Ignore the TTL here: the whole point of the fallback is to serve data that is
        // (by definition) past its freshness window when the network is unreachable.
        var staleCached = TryReadCache(ignoreTtl: true);
        if (staleCached != null) return PreferNewerOf(staleCached.Value.Data, staleCached.Value.WrittenAt);

        // Nothing on disk either. The corpus baked into the binary is the floor that
        // keeps find-ui working in the environment it is built for: agent sandboxes and
        // corporate networks where raw.githubusercontent.com is filtered, where every
        // path above fails on a first run.
        return LoadEmbedded() ?? ProviderData.Empty;
    }

    /// <summary>
    /// The embedded snapshot for this provider, with the provider's own read-side tag
    /// normalization applied so it is indistinguishable from a cache read. Baked tags are
    /// already written clean, so this is idempotent — it exists so a provider that changes
    /// its normalization can't have the snapshot silently diverge from every other path.
    /// </summary>
    private ProviderData? LoadEmbedded()
    {
        var snapshot = EmbeddedSnapshot.TryLoad(Id);
        return snapshot is null ? null : snapshot with { Tags = NormalizeTagsOnRead(snapshot.Tags) };
    }

    /// <summary>
    /// Choose between a cache read and the embedded snapshot by which one pulled from
    /// upstream more recently. Both timestamps mean the same thing, so they compare
    /// directly. This matters on upgrade: a newly installed binary can carry a corpus
    /// baked more recently than this machine last fetched, and without the comparison
    /// the TTL would pin the older cached copy for the rest of its freshness window.
    /// </summary>
    private ProviderData PreferNewerOf(ProviderData cached, DateTime cacheWrittenAt)
    {
        var bakedAt = EmbeddedSnapshot.BakedAtUtc;
        if (bakedAt is null || bakedAt <= cacheWrittenAt)
        {
            return cached;
        }

        return LoadEmbedded() ?? cached;
    }

    private (ProviderData Data, DateTime WrittenAt)? TryReadCache(bool ignoreTtl = false)
    {
        try
        {
            return Storage is null
                ? ReadCache(CacheDir, ignoreTtl)
                : Storage.Run(root => ReadCache(Path.Combine(root, Id), ignoreTtl));
        }
        catch (Exception ex) when (CacheStorage.IsStorageFailure(ex) && Storage?.IsExplicit != true)
        {
            Storage?.WarnUnavailable(ex);
            return null;
        }
    }

    private (ProviderData Data, DateTime WrittenAt)? ReadCache(string cacheDir, bool ignoreTtl)
    {
        var scenariosPath = Path.Combine(cacheDir, "scenarios.json");
        var tagsPath = Path.Combine(cacheDir, "tags.json");
        var keywordsPath = Path.Combine(cacheDir, "keywords.json");
        var timestampPath = Path.Combine(cacheDir, "last-updated.txt");
        var versionPath = Path.Combine(cacheDir, "schema-version.txt");
        try
        {
            if (File.ReadAllText(versionPath).Trim() != CacheVersion.Current) return null;
            var lastUpdated = ControlsCacheIo.ReadTimestamp(timestampPath);
            // Reject future-dated timestamps so a clock reset can't pin stale data.
            // The TTL (age >= CacheTtl) is skipped for the fetch-failure fallback so
            // an offline forced-refresh can still serve an expired-but-valid corpus.
            if (!lastUpdated.HasValue || lastUpdated.Value > DateTime.UtcNow
                || (!ignoreTtl && DateTime.UtcNow - lastUpdated.Value >= CacheTtl))
                return null;

            var scenarios = JsonSerializer.Deserialize(
                File.ReadAllText(scenariosPath), ControlsJsonContext.Default.ScenarioArray);
            var tags = JsonSerializer.Deserialize(
                File.ReadAllText(tagsPath), ControlsJsonContext.Default.DictionaryStringStringArray);
            if (scenarios == null || scenarios.Length == 0 || tags == null) return null;

            Dictionary<string, string[]>? keywords = null;
            if (File.Exists(keywordsPath))
            {
                try
                {
                    keywords = JsonSerializer.Deserialize(
                        File.ReadAllText(keywordsPath), ControlsJsonContext.Default.DictionaryStringStringArray);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException) { keywords = null; }
            }

            return (new ProviderData(scenarios, NormalizeTagsOnRead(tags), keywords ?? new(), CorpusOrigin.Cache),
                    lastUpdated.Value);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task TryWriteCacheAsync(ProviderData data, bool required, CancellationToken cancellationToken)
    {
        try
        {
            if (Storage is null) await WriteCacheAsync(CacheDir, data, cancellationToken).ConfigureAwait(false);
            else await Storage.RunAsync(async root =>
            {
                await WriteCacheAsync(Path.Combine(root, Id), data, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (CacheStorage.IsStorageFailure(ex) && !required && Storage?.IsExplicit != true)
        {
            Storage?.WarnUnavailable(ex);
        }
    }

    private static async Task WriteCacheAsync(string cacheDir, ProviderData data, CancellationToken cancellationToken)
    {
        var scenariosPath = Path.Combine(cacheDir, "scenarios.json");
        var tagsPath = Path.Combine(cacheDir, "tags.json");
        var keywordsPath = Path.Combine(cacheDir, "keywords.json");
        var timestampPath = Path.Combine(cacheDir, "last-updated.txt");
        var versionPath = Path.Combine(cacheDir, "schema-version.txt");

        Directory.CreateDirectory(cacheDir);

        // Invalidate the freshness marker BEFORE mutating any data file. On a
        // refresh of an already-fresh cache the old timestamp would otherwise
        // still be valid, so a crash after rewriting some (but not all) data
        // files would pair mismatched generations under a "fresh" stamp for up
        // to the TTL. Removing it first means any mid-write crash leaves no
        // valid timestamp ⇒ next read misses ⇒ clean re-fetch.
        if (File.Exists(timestampPath))
        {
            File.Delete(timestampPath);
        }

        // Atomic per-file writes (temp + rename via the shared PathSafety
        // helper). Order: data first, version next, timestamp LAST, so a
        // partially-written set is detected as still-stale on the next read
        // (no fresh timestamp ⇒ cache miss ⇒ re-fetch).
        await PathSafety.AtomicWriteAllTextAsync(scenariosPath,
            JsonSerializer.Serialize(data.Scenarios, ControlsJsonContext.Default.ScenarioArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        await PathSafety.AtomicWriteAllTextAsync(tagsPath,
            JsonSerializer.Serialize(data.Tags, ControlsJsonContext.Default.DictionaryStringStringArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        if (data.Keywords.Count > 0)
        {
            await PathSafety.AtomicWriteAllTextAsync(keywordsPath,
                JsonSerializer.Serialize(data.Keywords, ControlsJsonContext.Default.DictionaryStringStringArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(keywordsPath))
        {
            // A refresh with no keywords must not leave a stale keywords.json behind.
            File.Delete(keywordsPath);
        }
        await PathSafety.AtomicWriteAllTextAsync(versionPath, CacheVersion.Current, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        await PathSafety.AtomicWriteAllTextAsync(timestampPath, DateTime.UtcNow.ToString("o"), Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public void ClearCache()
    {
        try
        {
            if (Storage is null)
            {
                if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true);
            }
            else Storage.Clear(root =>
            {
                var path = Path.Combine(root, Id);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            });
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not clear find-ui cache '{CacheDir}': {ex.Message}", ex);
        }
    }
}

/// <summary>Lightweight, instance-free descriptor of a provider — its id and
/// display name — used for <c>--source</c> validation and id-prefix mapping
/// without constructing (network-backed) provider instances.</summary>
internal sealed record ProviderDescriptor(string Id, string DisplayName);

/// <summary>
/// The single registry of scenario providers. <see cref="Descriptors"/> lists
/// them without side effects (for validation), and <see cref="CreateProviders"/>
/// builds live instances rooted at a caller-supplied cache directory. This is
/// the one place to register a new source.
/// </summary>
internal static class ProviderRegistry
{
    /// <summary>Provider descriptors in display order (gallery first).</summary>
    public static readonly ProviderDescriptor[] Descriptors =
    {
        new("gallery", "Gallery (WinUI 3)"),
        new("toolkit", "CommunityToolkit"),
        new("reactor", "Reactor (WinUI)"),
    };

    /// <summary>Build live provider instances whose caches live under
    /// <paramref name="cacheRoot"/> (each in its own <c>{Id}</c> subfolder).</summary>
    public static ISearchProvider[] CreateProviders(string cacheRoot) =>
    [
        new GalleryProvider(cacheRoot),
        new ToolkitProvider(cacheRoot),
        new ReactorProvider(cacheRoot),
    ];

    /// <summary>Provider ids plus the pseudo-source <c>"core"</c> — the valid
    /// values for <c>--source</c>.</summary>
    public static IEnumerable<string> SourceFilterValues =>
        Descriptors.Select(d => d.Id).Append("core");

    public static bool IsValidSourceFilter(string source) =>
        SourceFilterValues.Any(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>The opt-in Reactor source id. Reactor's C#-only declarative
    /// samples don't paste into a standard XAML app, so it is excluded from a
    /// default search and only loaded when a caller explicitly asks for it via
    /// <c>--source reactor</c> or a <c>reactor-*</c> id. This is the single
    /// source of truth for that id so the command and the search service don't
    /// drift.</summary>
    public const string ReactorSourceId = "reactor";

    /// <summary>True when <paramref name="source"/> is the opt-in Reactor
    /// <c>--source</c> value (case-insensitive).</summary>
    public static bool IsReactorSource(string? source) =>
        string.Equals(source, ReactorSourceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="scenarioId"/> belongs to the Reactor
    /// source (its <c>reactor-</c> prefix maps to the Reactor provider).</summary>
    public static bool IsReactorScenarioId(string scenarioId) =>
        string.Equals(ForScenarioId(scenarioId)?.Id, ReactorSourceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Descriptor whose <c>{Id}-</c> prefix matches <paramref name="scenarioId"/>, if any.
    /// Case-insensitive so a hand-copied id like <c>REACTOR-FLEX-1</c> still routes to its provider.</summary>
    public static ProviderDescriptor? ForScenarioId(string scenarioId) =>
        Descriptors.FirstOrDefault(d => scenarioId.StartsWith($"{d.Id}-", StringComparison.OrdinalIgnoreCase));
}
