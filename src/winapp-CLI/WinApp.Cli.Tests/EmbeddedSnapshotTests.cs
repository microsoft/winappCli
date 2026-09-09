// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Guards the corpus baked into the binary — the floor that keeps <c>find-ui</c> working
/// where <c>raw.githubusercontent.com</c> is unreachable (agent sandboxes, corporate
/// proxies), which is the environment most of its users are in.
///
/// Two classes of test live here. The <i>build gates</i> fail when the committed data and
/// the code that reads it drift apart — a <see cref="CacheVersion"/> bump without a
/// re-bake, a new provider with no snapshot, or a <c>.json.br</c> that no longer matches
/// the reviewable <c>.json</c> beside it. Because <see cref="EmbeddedSnapshot"/>
/// deliberately swallows every failure (a broken snapshot must not take the command down),
/// these gates are the only thing that turns a bad bake into a red build instead of a
/// silent loss of the offline path. The rest cover the load-order behaviour.
/// </summary>
[TestClass]
public class EmbeddedSnapshotTests
{
    /// <summary>
    /// Fraction of code-bearing scenarios that must still carry code after sanitizing.
    /// Measured at 94.4% for the gallery corpus (18 of 321 samples are unbalanced
    /// fragments upstream), so 90% leaves headroom for normal upstream churn while still
    /// failing a bake that captured a truncated or mangled corpus.
    /// </summary>
    private const double MinCodeRetention = 0.90;

    // ------------------------------------------------------------------
    // Build gates: the committed snapshot must match the code that reads it
    // ------------------------------------------------------------------

    [TestMethod]
    public void Manifest_Ships_AndMatchesCurrentCacheVersion()
    {
        var manifest = EmbeddedSnapshot.Manifest;

        Assert.IsNotNull(manifest,
            "no snapshot manifest is embedded — find-ui has no offline floor. Re-bake with WinApp.Cli.SnapshotBaker.");
        Assert.AreEqual(CacheVersion.Current, manifest.CacheVersion,
            $"the embedded snapshot was baked at CacheVersion '{manifest.CacheVersion}' but the code is at " +
            $"'{CacheVersion.Current}'. EmbeddedSnapshot rejects a version mismatch, so shipping this would " +
            "silently remove the offline corpus. Re-bake the snapshot in the same change as the CacheVersion bump.");
        Assert.IsTrue(EmbeddedSnapshot.BakedAtUtc is { } baked && baked > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "the manifest must carry a real bake timestamp; it decides whether a user's cache or the snapshot is newer");
    }

    [TestMethod]
    public void EveryRegisteredProvider_HasAnEmbeddedCorpus()
    {
        // A provider added without a snapshot silently drops out of the corpus offline,
        // which looks like "no results for that source" rather than an error.
        foreach (var descriptor in ProviderRegistry.Descriptors)
        {
            var data = EmbeddedSnapshot.TryLoad(descriptor.Id);

            Assert.IsNotNull(data, $"no embedded corpus ships for provider '{descriptor.Id}'");
            Assert.IsTrue(data.Scenarios.Length > 0, $"the embedded corpus for '{descriptor.Id}' is empty");
            Assert.AreEqual(CorpusOrigin.Embedded, data.Origin,
                "a snapshot load must report itself as Embedded so callers can tell it from a live fetch");
        }
    }

    [TestMethod]
    public void EmbeddedScenarios_AreAttributedToTheirOwnProvider()
    {
        // Scenario.Source drives --source filtering and decides which provider a result is
        // credited to. A snapshot baked under the wrong id would be filtered out of its own
        // source and silently attributed to another.
        foreach (var descriptor in ProviderRegistry.Descriptors)
        {
            var data = EmbeddedSnapshot.TryLoad(descriptor.Id)!;

            Assert.IsTrue(
                data.Scenarios.All(s => string.Equals(s.Source, descriptor.Id, StringComparison.OrdinalIgnoreCase)),
                $"every scenario in the '{descriptor.Id}' snapshot must carry Source='{descriptor.Id}'");
            Assert.IsTrue(
                data.Scenarios.All(s => !string.IsNullOrWhiteSpace(s.Id)),
                $"the '{descriptor.Id}' snapshot contains a scenario with no id");
            Assert.AreEqual(
                data.Scenarios.Length,
                data.Scenarios.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                $"scenario ids in the '{descriptor.Id}' snapshot must be unique — --id lookup resolves by id");
        }
    }

    [TestMethod]
    public void GalleryCorpus_CarriesNoWinappAuthoredSample()
    {
        // Gallery samples are served exactly as upstream publishes them. Scenario.Source
        // becomes the "[gallery]" tag and the "gallery-" id prefix, so anything baked in
        // here is presented to users — and to the WinUI-Gallery maintainers we are asking
        // to publish an index (#703) — as content their repository owns. winapp used to
        // append these samples because no Gallery sample demos UniformGridLayout directly
        // and upstream's CommandBar sample is doc-elided; that guidance now lives in
        // Notes.cs, where it is labelled as ours.
        var gallery = EmbeddedSnapshot.TryLoad("gallery")!;

        string[] winappAuthoredHeaders =
        [
            "Photo gallery: image grid (UniformGridLayout)",
            "Primary and secondary commands",
        ];

        foreach (var header in winappAuthoredHeaders)
        {
            Assert.IsFalse(
                gallery.Scenarios.Any(s => string.Equals(s.HeaderText, header, StringComparison.Ordinal)),
                $"the winapp-authored sample \"{header}\" is baked into the gallery corpus — upstream it to " +
                "WinUI-Gallery or put the guidance in Notes.cs instead of merging it into their samples");
        }
    }

    [TestMethod]
    public void CompressedSnapshot_MatchesTheManifest()
    {
        // Only the Brotli blob is committed now, so there is no readable sibling to diff
        // it against. What can still diverge is the blob and the manifest: they are
        // written as a set by one bake, so a hand-edit or a half-applied re-bake shows up
        // as counts that disagree. The manifest is also what the release assertion and the
        // drift job read, so a wrong count there is load-bearing, not cosmetic.
        var manifest = EmbeddedSnapshot.Manifest;
        Assert.IsNotNull(manifest, "no snapshot manifest is embedded — find-ui has no offline floor.");

        foreach (var descriptor in ProviderRegistry.Descriptors)
        {
            var fromResource = ReadEmbeddedSnapshot(descriptor.Id);
            Assert.IsNotNull(fromResource, $"snapshot-{descriptor.Id}.json.br did not parse");

            Assert.IsTrue(manifest.ScenarioCounts.TryGetValue(descriptor.Id, out var declared),
                $"snapshot-manifest.json has no scenario count for '{descriptor.Id}'. " +
                "The manifest and the blobs are written by one bake — re-bake rather than editing either by hand.");

            Assert.AreEqual(declared, fromResource.Scenarios.Length,
                $"snapshot-manifest.json says '{descriptor.Id}' has {declared} scenarios but the embedded " +
                $"blob carries {fromResource.Scenarios.Length}. Re-bake — do not edit either by hand.");
        }
    }

    [TestMethod]
    public void EmbeddedCorpus_SurvivesTheSanitizer()
    {
        // ScenarioSanitizer is the corpus boundary guard: it nulls XAML that isn't
        // well-formed and C# whose braces don't balance. Running it over the committed
        // snapshot catches a bake that captured truncated or mangled samples.
        //
        // The bar is deliberately a regression floor rather than zero. A handful of
        // upstream samples genuinely are unbalanced fragments, and the sanitizer strips
        // them identically on a live fetch — that is issue #716 (truncated samples), not a
        // property of baking. What this test must catch is the snapshot becoming materially
        // worse than the live corpus it stands in for.
        // Deserialized fresh from the embedded resource rather than through
        // EmbeddedSnapshot.TryLoad: TryLoad memoizes one ProviderData per provider for the
        // life of the process, Scenario is a mutable class, and SanitizeAll rewrites it in
        // place — so sanitizing a TryLoad result would permanently strip code out of the
        // corpus every other test in this parallel assembly reads.
        var report = new List<string>();
        var regressed = new List<string>();

        foreach (var descriptor in ProviderRegistry.Descriptors)
        {
            var scenarios = ReadEmbeddedSnapshot(descriptor.Id)!.Scenarios;
            var before = scenarios.Count(HasCode);

            ScenarioSanitizer.SanitizeAll(scenarios);

            var after = scenarios.Count(HasCode);
            report.Add($"{descriptor.Id}: {after}/{before} kept code of {scenarios.Length} scenarios");

            if (before == 0)
            {
                regressed.Add($"{descriptor.Id} ships no code at all");
            }
            else if (after < before * MinCodeRetention)
            {
                regressed.Add(
                    $"{descriptor.Id} lost code from {before - after} of {before} scenarios " +
                    $"({(double)after / before:P1} retained, floor {MinCodeRetention:P0})");
            }

            Assert.IsTrue(scenarios.All(s => !string.IsNullOrWhiteSpace(s.Id)),
                "sanitizing must not empty a scenario id");
        }

        Assert.AreEqual(0, regressed.Count,
            $"the baked corpus degraded under the sanitizer: {string.Join("; ", regressed)}. " +
            $"Observed: {string.Join(" | ", report)}");
    }

    // ------------------------------------------------------------------
    // Load order: the snapshot is a floor, never a ceiling
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task ColdCache_FetchFails_ServesEmbeddedCorpus()
    {
        // The bug this whole feature exists for: first run, no cache, blocked host.
        // Before the floor this returned nothing and find-ui was non-functional.
        var root = NewTempCacheRoot();
        try
        {
            var provider = new ThrowingProvider(root, "gallery", "Gallery (WinUI 3)");

            var data = await provider.LoadAsync();

            Assert.IsTrue(data.Scenarios.Length > 0,
                "a cold cache with no network must fall back to the corpus baked into the binary");
            Assert.AreEqual(CorpusOrigin.Embedded, data.Origin);
            Assert.IsFalse(Directory.Exists(Path.Join(root, "gallery")),
                "serving the embedded corpus must not write a cache — it is not a fetched result");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SuccessfulFetch_BeatsEmbeddedCorpus()
    {
        // The floor must never mask live data.
        var root = NewTempCacheRoot();
        try
        {
            var provider = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));

            var data = await provider.LoadAsync();

            Assert.AreEqual(CorpusOrigin.Network, data.Origin, "a successful fetch must win over the snapshot");
            Assert.AreEqual(1, data.Scenarios.Length, "the fetched payload must be served verbatim");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheOlderThanSnapshot_LosesToEmbeddedCorpus()
    {
        // The upgrade case: a machine that fetched before this binary's corpus was baked
        // installs a new CLI carrying a more recent one. The cache is still inside its
        // freshness window, so nothing else goes looking — only the bake-date comparison
        // stops the older cached copy from being served.
        //
        // The TTL is pinned open deliberately. The state under test is "cache is FRESH
        // but older than the bake", which stops being reachable in real time once the
        // shipped bake date ages past the TTL — at which point this silently becomes a
        // test of the fetch path instead (it did exactly that, and went red, eight days
        // after the corpus was baked). Pinning keeps the assertion on PreferNewerOf and
        // independent of the wall clock.
        var root = NewTempCacheRoot();
        try
        {
            var seed = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"), TimeSpan.MaxValue);
            await seed.LoadAsync();
            BackdateCache(root, "gallery", EmbeddedSnapshot.BakedAtUtc!.Value.AddDays(-1));

            var data = await new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"), TimeSpan.MaxValue)
                .LoadAsync();

            Assert.AreEqual(CorpusOrigin.Embedded, data.Origin,
                "a snapshot baked more recently than the cache was written must win");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Freshness window: 24 hours (winapp CLI spec review, 2026-08-12)
    // ------------------------------------------------------------------
    // Uses a provider id with no baked corpus so the embedded floor can't decide the
    // outcome — these pin the TTL boundary itself, nothing else.

    [TestMethod]
    public async Task CacheWithinOneDay_IsServedWithoutRefetching()
    {
        var root = NewTempCacheRoot();
        try
        {
            const string id = "ttl-probe";
            await new StubProvider(root, id, "TTL probe", SampleData(id)).LoadAsync();
            BackdateCache(root, id, DateTime.UtcNow.AddHours(-23));

            var data = await new StubProvider(root, id, "TTL probe", SampleData(id)).LoadAsync();

            Assert.AreEqual(CorpusOrigin.Cache, data.Origin,
                "a cache written less than 24h ago must be served without a re-fetch");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheOlderThanOneDay_IsRefetched()
    {
        var root = NewTempCacheRoot();
        try
        {
            const string id = "ttl-probe";
            await new StubProvider(root, id, "TTL probe", SampleData(id)).LoadAsync();
            BackdateCache(root, id, DateTime.UtcNow.AddHours(-25));

            var data = await new StubProvider(root, id, "TTL probe", SampleData(id)).LoadAsync();

            Assert.AreEqual(CorpusOrigin.Network, data.Origin,
                "a cache older than 24h must be refreshed from upstream");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheWrittenByAnOlderSchema_IsDiscarded()
    {
        var root = NewTempCacheRoot();
        try
        {
            const string id = "schema-probe";
            await new StubProvider(root, id, "Schema probe", SampleData(id)).LoadAsync();

            // Fresh by the clock but stamped by a superseded CacheVersion — the exact
            // state every version bump leaves behind on an existing install. Without
            // the stamp check the old-schema payload would deserialize into the new
            // shape and be served as if it were current.
            var versionPath = Path.Join(root, id, "schema-version.txt");
            Assert.AreEqual(CacheVersion.Current, File.ReadAllText(versionPath).Trim(),
                "priming the cache must stamp it with the current schema version");
            File.WriteAllText(versionPath, "0");

            var data = await new StubProvider(root, id, "Schema probe", SampleData(id)).LoadAsync();

            Assert.AreEqual(CorpusOrigin.Network, data.Origin,
                "a cache stamped with a superseded schema version must be re-fetched, not served");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheNewerThanSnapshot_Wins()
    {
        // The steady state: a user who has fetched since the release must keep their own,
        // fresher corpus. Getting this backwards would make every upgrade a regression.
        var root = NewTempCacheRoot();
        try
        {
            var seed = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            await seed.LoadAsync();   // writes "now", which is after any shipped bake

            var data = await new ThrowingProvider(root, "gallery", "Gallery (WinUI 3)").LoadAsync();

            Assert.AreEqual(CorpusOrigin.Cache, data.Origin,
                "a cache written after the snapshot was baked must be preferred");
            Assert.AreEqual(1, data.Scenarios.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownProvider_HasNoFloor_AndDegradesToEmpty()
    {
        // A provider with no baked corpus must not resurrect another provider's data.
        var root = NewTempCacheRoot();
        try
        {
            var data = await new ThrowingProvider(root, "not-a-real-provider", "Nope").LoadAsync();

            Assert.AreEqual(0, data.Scenarios.Length);
            Assert.AreEqual(CorpusOrigin.None, data.Origin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Provenance reported to the caller
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task LoadedOrigin_ReportsTheLeastFreshProvider()
    {
        // find-ui merges providers into one answer, so the honest label for that answer is
        // its weakest input: if any source fell back to the snapshot, the result as a whole
        // is not live and the user is told so.
        var live = new FakeSearchProvider("gallery", DataWithOrigin("gallery", "tabview", CorpusOrigin.Network));
        var floor = new FakeSearchProvider("toolkit", DataWithOrigin("toolkit", "datagrid", CorpusOrigin.Embedded));
        var sut = new ControlsSearchService([live, floor]);

        await sut.GetEngineAsync();

        Assert.AreEqual(CorpusOrigin.Embedded, sut.LoadedOrigin);
    }

    [TestMethod]
    public async Task LoadedOrigin_AllProvidersLive_ReportsNetwork()
    {
        var gallery = new FakeSearchProvider("gallery", DataWithOrigin("gallery", "tabview", CorpusOrigin.Network));
        var toolkit = new FakeSearchProvider("toolkit", DataWithOrigin("toolkit", "datagrid", CorpusOrigin.Network));
        var sut = new ControlsSearchService([gallery, toolkit]);

        await sut.GetEngineAsync();

        Assert.AreEqual(CorpusOrigin.Network, sut.LoadedOrigin);
    }

    [TestMethod]
    public async Task LoadedOrigin_EmptyProviderDoesNotCountAsAnOrigin()
    {
        // ProviderData.Empty carries Origin.None, meaning "nothing loaded" rather than a
        // real source. Treating it as the weakest value would mislabel every partial
        // corpus, which is a routine outcome when one provider's fetch fails.
        var gallery = new FakeSearchProvider("gallery", DataWithOrigin("gallery", "tabview", CorpusOrigin.Cache));
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        await sut.GetEngineAsync();

        Assert.AreEqual(CorpusOrigin.Cache, sut.LoadedOrigin);
    }

    [TestMethod]
    public async Task LoadedOrigin_CoreOnly_ReportsNoCorpus()
    {
        // --source core never consults a provider, so there is no upstream origin to claim.
        var gallery = new FakeSearchProvider("gallery", DataWithOrigin("gallery", "tabview", CorpusOrigin.Network));
        var sut = new ControlsSearchService([gallery]);

        await sut.GetEngineAsync(coreOnly: true);

        Assert.AreEqual(CorpusOrigin.None, sut.LoadedOrigin);
    }

    [TestMethod]
    public async Task LoadedOrigin_MemoizedEngineHit_ReportsThatEngineSOrigin()
    {
        // The origin has to travel with the engine it describes. A core-only request
        // legitimately reports "no corpus", and the memoized full engine is returned
        // without being rebuilt — so if the fast path doesn't restore its origin, the next
        // search silently loses `"corpus"` from --json and stops warning that results came
        // from the embedded floor.
        var gallery = new FakeSearchProvider("gallery", DataWithOrigin("gallery", "tabview", CorpusOrigin.Embedded));
        var sut = new ControlsSearchService([gallery]);

        await sut.GetEngineAsync();
        Assert.AreEqual(CorpusOrigin.Embedded, sut.LoadedOrigin);

        await sut.GetEngineAsync(coreOnly: true);
        Assert.AreEqual(CorpusOrigin.None, sut.LoadedOrigin);

        await sut.GetEngineAsync();

        Assert.AreEqual(CorpusOrigin.Embedded, sut.LoadedOrigin,
            "a memoized engine must keep reporting the origin its corpus was loaded from");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static bool HasCode(Scenario s) =>
        !string.IsNullOrWhiteSpace(s.Xaml) || !string.IsNullOrWhiteSpace(s.CSharp);

    private static ProviderData DataWithOrigin(string source, string controlId, CorpusOrigin origin) =>
        new(
            [new Scenario { Id = $"{source}-{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "s", Source = source }],
            new Dictionary<string, string[]> { [controlId] = [controlId] },
            new(),
            origin);

    private static ProviderData SampleData(string controlId) =>
        new(
            [new Scenario { Id = $"{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "s", Source = controlId }],
            new Dictionary<string, string[]> { [controlId] = [controlId] },
            new());

    private static ProviderSnapshot? ReadEmbeddedSnapshot(string providerId)
    {
        using var compressed = typeof(EmbeddedSnapshot).Assembly
            .GetManifestResourceStream($"snapshot-{providerId}.json.br");
        Assert.IsNotNull(compressed, $"snapshot-{providerId}.json.br is not embedded in the assembly");

        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(brotli, ControlsJsonContext.Default.ProviderSnapshot);
    }

    private static void BackdateCache(string root, string providerId, DateTime writtenAtUtc)
    {
        var timestampPath = Path.Join(root, providerId, "last-updated.txt");
        Assert.IsTrue(File.Exists(timestampPath), "the priming load should have written a cache timestamp");
        File.WriteAllText(timestampPath, writtenAtUtc.ToString("o"));
    }

    private static string NewTempCacheRoot()
    {
        var dir = Path.Join(Path.GetTempPath(), "winapp-findui-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A provider whose fetch always fails — models a blocked host.</summary>
    private sealed class ThrowingProvider : CachedProviderBase
    {
        public ThrowingProvider(string cacheRoot, string id, string displayName) : base(cacheRoot)
        {
            Id = id;
            DisplayName = displayName;
        }

        public override string Id { get; }
        public override string DisplayName { get; }

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
            => throw new HttpRequestException("blocked");
    }

    /// <summary>A provider that fetches a canned payload, so the base class's real
    /// cache-write and cache-read paths are exercised.</summary>
    private sealed class StubProvider : CachedProviderBase
    {
        private readonly ProviderData _data;
        private readonly TimeSpan? _ttl;

        public StubProvider(string cacheRoot, string id, string displayName, ProviderData data, TimeSpan? ttl = null)
            : base(cacheRoot)
        {
            Id = id;
            DisplayName = displayName;
            _data = data;
            _ttl = ttl;
        }

        public override string Id { get; }
        public override string DisplayName { get; }

        protected override TimeSpan CacheTtl => _ttl ?? base.CacheTtl;

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
            => Task.FromResult(_data);
    }
}
