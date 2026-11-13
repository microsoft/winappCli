// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Verifies the "fetching…" notice plumbing: the <c>onFetchStarting</c> callback
/// threaded from the command through <see cref="ControlsSearchService"/> into each
/// <see cref="CachedProviderBase"/> fires only when a provider actually reaches the
/// network (cold/stale cache or forced refresh), never on a warm-cache or memoized load.
/// </summary>
[TestClass]
public class ControlsFetchNoticeTests
{
    private static readonly string[] GalleryOnly = ["Gallery (WinUI 3)"];
    private static readonly string[] ToolkitOnly = ["CommunityToolkit"];
    private static readonly string[] GalleryAndToolkit = ["Gallery (WinUI 3)", "CommunityToolkit"];

    /// <summary>A real <see cref="CachedProviderBase"/> backed by a temp cache dir and a
    /// canned payload, so the base class's cache-vs-fetch decision (and thus the notice)
    /// is exercised for real.</summary>
    private sealed class StubProvider : CachedProviderBase
    {
        private readonly ProviderData _data;
        public int FetchCalls { get; private set; }

        public StubProvider(string cacheRoot, string id, string displayName, ProviderData data)
            : base(cacheRoot)
        {
            Id = id;
            DisplayName = displayName;
            _data = data;
        }

        public override string Id { get; }
        public override string DisplayName { get; }

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(_data);
        }
    }

    private static ProviderData SampleData(string controlId) =>
        new(
            [new Scenario { Id = $"{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "s", Source = controlId }],
            new Dictionary<string, string[]> { [controlId] = [controlId] },
            new());

    private static string NewTempCacheRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winapp-findui-fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public async Task Provider_ColdCache_InvokesNotice_ThenWarmCacheDoesNot()
    {
        var root = NewTempCacheRoot();
        try
        {
            var provider = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));

            // Cold cache → must fetch → notice fires once with the display name.
            var cold = new List<string>();
            var data = await provider.LoadAsync(onFetchStarting: cold.Add);
            Assert.AreEqual(1, provider.FetchCalls);
            Assert.AreEqual(1, data.Scenarios.Length);
            Assert.AreSequenceEqual(GalleryOnly, cold);

            // Warm cache (the cold load primed it) → served from disk → no fetch, no notice.
            var warm = new List<string>();
            await provider.LoadAsync(onFetchStarting: warm.Add);
            Assert.AreEqual(1, provider.FetchCalls, "warm cache must not re-fetch");
            Assert.AreEqual(0, warm.Count, "warm cache must not raise the fetching notice");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Provider_ForceRefresh_InvokesNotice()
    {
        var root = NewTempCacheRoot();
        try
        {
            var provider = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            await provider.LoadAsync();                       // prime the cache

            var notices = new List<string>();
            await provider.LoadAsync(forceRefresh: true, onFetchStarting: notices.Add);

            Assert.AreEqual(2, provider.FetchCalls, "forceRefresh must re-fetch even with a warm cache");
            Assert.AreSequenceEqual(GalleryOnly, notices);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Service_ColdProviders_ForwardsNoticeForEach_ThenMemoizedRunIsSilent()
    {
        var rootA = NewTempCacheRoot();
        var rootB = NewTempCacheRoot();
        try
        {
            var gallery = new StubProvider(rootA, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            var toolkit = new StubProvider(rootB, "toolkit", "CommunityToolkit", SampleData("toolkit"));
            var service = new ControlsSearchService([gallery, toolkit]);

            // First (cold) build: both providers fetch → notice fires for each.
            var cold = new List<string>();
            await service.GetEngineAsync(onFetchStarting: cold.Add);
            Assert.AreSequenceEqual(GalleryAndToolkit, cold, SequenceOrder.InAnyOrder);

            // Second call: complete corpus was memoized → providers aren't reloaded → silent.
            var second = new List<string>();
            await service.GetEngineAsync(onFetchStarting: second.Add);
            Assert.AreEqual(0, second.Count, "a memoized engine must not raise the fetching notice");
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [TestMethod]
    public async Task Service_PartialCorpus_NotMemoized_ReraisesNoticeForFetchingProviderNextCall()
    {
        var rootA = NewTempCacheRoot();
        var rootB = NewTempCacheRoot();
        try
        {
            // gallery has data (caches after first fetch); the second provider always comes
            // back empty (its fetch never populates a cache), so the corpus is partial and
            // the service must NOT memoize it — the next call re-attempts both providers.
            //
            // The empty provider deliberately uses an id with no baked corpus. A real
            // provider id would fall through to the embedded snapshot, making the corpus
            // complete and correctly memoizable — which is the fix for #704, not a partial
            // corpus, and is covered by EmbeddedSnapshotTests.
            var gallery = new StubProvider(rootA, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            var toolkit = new StubProvider(rootB, "toolkit-no-snapshot", "CommunityToolkit", ProviderData.Empty);
            var service = new ControlsSearchService([gallery, toolkit]);

            var first = new List<string>();
            await service.GetEngineAsync(onFetchStarting: first.Add);
            Assert.AreSequenceEqual(GalleryAndToolkit, first, SequenceOrder.InAnyOrder);

            // Not memoized (partial) → second call reloads. gallery is now warm (silent), but
            // the empty toolkit re-fetches → the notice is raised again for it.
            var second = new List<string>();
            await service.GetEngineAsync(onFetchStarting: second.Add);
            Assert.AreSequenceEqual(ToolkitOnly, second,
                "the still-empty provider must re-fetch and re-raise the notice; the warm one stays silent");
            Assert.AreEqual(1, gallery.FetchCalls, "gallery served from warm cache on the second call");
            Assert.AreEqual(2, toolkit.FetchCalls, "empty toolkit re-fetched on the second call");
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [TestMethod]
    public async Task Provider_OfflineColdFetchThrows_StillInvokesNotice()
    {
        var root = NewTempCacheRoot();
        try
        {
            // Simulate an offline cold start: FetchAsync throws. The notice fires before the
            // fetch attempt, and LoadAsync swallows the failure into Empty.
            //
            // Deliberately uses an id with no baked corpus so this stays a test of the
            // notice alone. A real provider id would fall through to the embedded snapshot
            // (covered by EmbeddedSnapshotTests), which is orthogonal to the notice.
            var provider = new ThrowingProvider(root, "notice-only", "Gallery (WinUI 3)");
            var notices = new List<string>();

            var data = await provider.LoadAsync(onFetchStarting: notices.Add);

            Assert.AreEqual(0, data.Scenarios.Length, "an offline cold fetch with no floor yields no data");
            Assert.AreSequenceEqual(GalleryOnly, notices,
                "the notice fires even when the fetch itself fails");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Provider_ExpiredCache_OfflineNonForcedRefetch_ServesStale()
    {
        var root = NewTempCacheRoot();
        try
        {
            // Uses an id with no baked corpus to isolate the stale-cache path: with a real
            // provider id a 30-day-old cache would correctly lose to the newer embedded
            // snapshot, which is a different behaviour tested in EmbeddedSnapshotTests.
            const string id = "stale-only";

            // Prime a cache, then backdate its timestamp well past the TTL.
            var seed = new StubProvider(root, id, "Gallery (WinUI 3)", SampleData(id));
            await seed.LoadAsync();
            var tsPath = Path.Join(root, id, "last-updated.txt");
            Assert.IsTrue(File.Exists(tsPath), "priming load should have written the cache");
            File.WriteAllText(tsPath, DateTime.UtcNow.AddDays(-30).ToString("o"));

            // A NON-forced load now misses on the TTL, attempts a fetch, and the fetch
            // fails (offline). It must fall back to the stale cache rather than returning
            // Empty — otherwise an offline user loses find-ui a day after their last fetch.
            var offline = new ThrowingProvider(root, id, "Gallery (WinUI 3)");
            var data = await offline.LoadAsync(forceRefresh: false);

            Assert.AreEqual(1, data.Scenarios.Length,
                "an expired cache must be served when an offline non-forced refetch fails");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A provider whose fetch always throws — models an offline cold start.</summary>
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
            => throw new HttpRequestException("offline");
    }
}
