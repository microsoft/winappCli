// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ControlsSearchService"/> using fake providers
/// (no GitHub / no disk), exercising memoization, forced refresh, partial-corpus
/// handling, and the all-empty error path.
/// </summary>
[TestClass]
public class ControlsSearchServiceTests
{
    private static ProviderData Data(string source, params string[] controlIds)
    {
        var scenarios = controlIds.Select((cid, i) => new Scenario
        {
            Id = $"{cid}-1",
            ControlId = cid,
            ControlName = cid,
            HeaderText = $"{cid} sample",
            Source = source,
        }).ToArray();
        var tags = controlIds.ToDictionary(cid => cid, cid => new[] { cid });
        return new ProviderData(scenarios, tags, new());
    }

    [TestMethod]
    public async Task GetEngineAsync_MemoizesCompleteCorpus()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var e1 = await sut.GetEngineAsync();
        var e2 = await sut.GetEngineAsync();

        Assert.AreSame(e1, e2, "second call should return the memoized engine");
        Assert.AreEqual(1, gallery.LoadCalls);
        Assert.AreEqual(1, toolkit.LoadCalls);
    }

    [TestMethod]
    public async Task GetEngineAsync_ForceRefresh_RebuildsAndReloadsProviders()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        await sut.GetEngineAsync();
        await sut.GetEngineAsync(forceRefresh: true);

        Assert.AreEqual(2, gallery.LoadCalls, "forceRefresh must re-load providers");
        Assert.AreEqual(2, toolkit.LoadCalls);
    }

    [TestMethod]
    public async Task GetEngineAsync_PartialCorpus_ServesButDoesNotMemoize()
    {
        // toolkit empty (its cold-cache fetch failed); gallery has data.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        var e1 = await sut.GetEngineAsync();
        Assert.IsNotNull(e1, "partial corpus should still serve results");

        // Not memoized → next call re-attempts the missing provider.
        await sut.GetEngineAsync();
        Assert.AreEqual(2, toolkit.LoadCalls, "a degraded engine must not be pinned; re-attempt the empty provider");
    }

    [TestMethod]
    public async Task GetEngineAsync_AllProvidersEmpty_Throws()
    {
        var gallery = new FakeSearchProvider("gallery", ProviderData.Empty);
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        await Assert.ThrowsExactlyAsync<ControlsDataUnavailableException>(
            async () => await sut.GetEngineAsync());
    }

    [TestMethod]
    public async Task GetEngineAsync_AllProvidersEmpty_AllowCoreOnly_ReturnsCoreOnlyEngine()
    {
        // Offline cold cache: both network providers empty. --list, --source core, and
        // --id <core-id> must still work off the embedded (non-network) core patterns.
        var gallery = new FakeSearchProvider("gallery", ProviderData.Empty);
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        var engine = await sut.GetEngineAsync(allowCoreOnly: true);

        var ids = engine.ListAll().Select(x => x.id).ToList();
        Assert.IsTrue(ids.Count > 0, "the embedded core patterns must be listable offline");
        Assert.IsTrue(ids.All(id => ProviderRegistry.ForScenarioId(id) is null),
            "a core-only engine exposes only the embedded (non-network) core patterns");

        // A degraded core-only engine must NOT be memoized — a later call re-attempts
        // the network providers rather than being pinned offline.
        await sut.GetEngineAsync(allowCoreOnly: true);
        Assert.AreEqual(2, gallery.LoadCalls, "the core-only fallback must not be pinned as the memoized engine");
    }

    [TestMethod]
    public async Task GetEngineAsync_CollidingControlIdsAcrossSources_BothSurvive()
    {
        // Both providers expose the same bare controlId "colorpicker" (and a
        // same-named tag). Tags are namespaced by provider id ("{id}:{controlId}")
        // so neither the scenarios nor the tag entries overwrite each other —
        // both source-prefixed scenarios must be present in the merged corpus.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "colorpicker"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "colorpicker"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var engine = await sut.GetEngineAsync();
        var ids = engine.ListAll().Select(x => x.id).ToList();

        Assert.Contains("gallery-colorpicker-1", ids, "gallery colorpicker must survive the merge");
        Assert.Contains("toolkit-colorpicker-1", ids, "toolkit colorpicker must survive the merge");
    }

    [TestMethod]
    public async Task GetEngineAsync_CoreOnly_SkipsAllNetworkProvidersAndNotice()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);
        var notices = new List<string>();

        var engine = await sut.GetEngineAsync(coreOnly: true, onFetchStarting: notices.Add);
        var ids = engine.ListAll().Select(x => x.id).ToList();

        Assert.AreEqual(0, gallery.LoadCalls, "core-only must not load the gallery provider");
        Assert.AreEqual(0, toolkit.LoadCalls, "core-only must not load the toolkit provider");
        Assert.AreEqual(0, reactor.LoadCalls, "core-only must not load the reactor provider");
        Assert.AreEqual(0, notices.Count, "core-only touches no network, so the 'fetching…' notice must never fire");
        Assert.IsTrue(ids.Count > 0, "the embedded core patterns must be available");
        Assert.IsTrue(ids.All(id => ProviderRegistry.ForScenarioId(id) is null),
            "a core-only engine exposes only the embedded (non-network) core patterns");
    }

    [TestMethod]
    public async Task GetEngineAsync_CoreOnly_MemoizesAndDoesNotClobberFullEngine()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var core1 = await sut.GetEngineAsync(coreOnly: true);
        var core2 = await sut.GetEngineAsync(coreOnly: true);
        var full = await sut.GetEngineAsync();

        Assert.AreSame(core1, core2, "the core-only engine is memoized");
        Assert.AreNotSame(core1, full, "a normal request must not receive the core-only engine");
        Assert.IsTrue(full.ListAll().Any(x => x.id.Contains("tabview", StringComparison.Ordinal)),
            "the full engine still loads the network corpus after a prior core-only call");
    }

    [TestMethod]
    public async Task GetEngineAsync_DefaultExcludesReactor_DoesNotLoadProvider()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);

        var engine = await sut.GetEngineAsync();
        var ids = engine.ListAll().Select(x => x.id).ToList();

        Assert.AreEqual(0, reactor.LoadCalls, "Reactor is opt-in; a default search must not even load (or fetch) it");
        Assert.IsFalse(ids.Any(id => id.Contains("flex", StringComparison.Ordinal)),
            "Reactor scenarios must be absent from the default corpus");
    }

    [TestMethod]
    public async Task GetEngineAsync_IncludeReactor_LoadsProviderAndSurfacesScenarios()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);

        var engine = await sut.GetEngineAsync(includeReactor: true);
        var ids = engine.ListAll().Select(x => x.id).ToList();

        Assert.AreEqual(1, reactor.LoadCalls, "opting in (--source reactor / reactor-* id) must load the Reactor provider");
        Assert.IsTrue(ids.Any(id => id.Contains("flex", StringComparison.Ordinal)),
            "Reactor scenarios must be present when opted in");
    }

    [TestMethod]
    public async Task GetEngineAsync_MemoizesReactorVariantSeparately()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);

        var def1 = await sut.GetEngineAsync();
        var rea1 = await sut.GetEngineAsync(includeReactor: true);
        var def2 = await sut.GetEngineAsync();
        var rea2 = await sut.GetEngineAsync(includeReactor: true);

        Assert.AreNotSame(def1, rea1, "the with- and without-Reactor corpora are distinct engines");
        Assert.AreSame(def1, def2, "the default (no-Reactor) engine is memoized independently");
        Assert.AreSame(rea1, rea2, "the with-Reactor engine is memoized independently");
        Assert.AreEqual(1, reactor.LoadCalls, "Reactor loads once (for the with-Reactor engine) and never for the default");
    }

    [TestMethod]
    public async Task GetEngineAsync_ForceRefresh_InvalidatesSiblingReactorEngine()
    {
        // Warm both the default and the with-Reactor engines, then force-refresh the
        // default. A refresh re-fetches the whole corpus, so the with-Reactor engine
        // must NOT keep serving its pre-refresh in-memory data: the next opt-in call
        // has to rebuild it (reloading the reactor provider).
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);

        await sut.GetEngineAsync();                       // build default (reactor skipped)
        var rea1 = await sut.GetEngineAsync(includeReactor: true); // build with-reactor (reactor loads once)
        Assert.AreEqual(1, reactor.LoadCalls);

        await sut.GetEngineAsync(forceRefresh: true);     // refresh default → invalidate both slots
        var rea2 = await sut.GetEngineAsync(includeReactor: true); // must rebuild with-reactor

        Assert.AreEqual(2, reactor.LoadCalls, "a forced refresh must invalidate the sibling engine so Reactor reloads");
        Assert.AreNotSame(rea1, rea2, "the with-Reactor engine must be rebuilt after a refresh, not served stale");
    }

    [TestMethod]
    public async Task ClearCache_ResetsBothMemoizedEngines()
    {
        // Warm the default and with-Reactor engines, clear, then re-request both.
        // Every provider (including reactor, for the opt-in engine) must reload,
        // proving both _engine and _engineWithReactor were reset.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var reactor = new FakeSearchProvider("reactor", Data("reactor", "flex"));
        var sut = new ControlsSearchService([gallery, toolkit, reactor]);

        var def1 = await sut.GetEngineAsync();
        var rea1 = await sut.GetEngineAsync(includeReactor: true);
        Assert.AreEqual(1, reactor.LoadCalls, "reactor loads once (only for the opt-in engine) while both are warm");

        sut.ClearCache();

        var def2 = await sut.GetEngineAsync();
        var rea2 = await sut.GetEngineAsync(includeReactor: true);

        Assert.AreNotSame(def1, def2, "the default engine must be rebuilt after ClearCache");
        Assert.AreNotSame(rea1, rea2, "the with-Reactor engine must be rebuilt after ClearCache");
        Assert.AreEqual(2, reactor.LoadCalls, "ClearCache must reset the with-Reactor memo slot so reactor reloads");
    }

    [TestMethod]
    public async Task GetEngineAsync_CoreOnly_ForceRefresh_KeepsMemoizedEngineAndSkipsProviders()
    {
        // The core-only engine wraps deterministic embedded data, so forceRefresh
        // is intentionally a no-op for it: the memoized instance is returned and
        // no network provider is loaded.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var core1 = await sut.GetEngineAsync(coreOnly: true);
        var core2 = await sut.GetEngineAsync(coreOnly: true, forceRefresh: true);

        Assert.AreSame(core1, core2, "forceRefresh must not rebuild the embedded core-only engine");
        Assert.AreEqual(0, gallery.LoadCalls, "a forced core-only refresh must still skip the gallery provider");
        Assert.AreEqual(0, toolkit.LoadCalls, "a forced core-only refresh must still skip the toolkit provider");
    }

    [TestMethod]
    public async Task ClearCache_ResetsCoreOnlyEngine()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var core1 = await sut.GetEngineAsync(coreOnly: true);
        sut.ClearCache();
        var core2 = await sut.GetEngineAsync(coreOnly: true);

        Assert.AreNotSame(core1, core2, "ClearCache must reset the core-only memo slot so it is rebuilt");
    }

    [TestMethod]
    public void ClearCache_DelegatesToEveryProvider()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        sut.ClearCache();

        Assert.AreEqual(1, gallery.ClearCalls);
        Assert.AreEqual(1, toolkit.ClearCalls);
    }
}
