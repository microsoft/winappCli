// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the ported find-ui search engine + provider registry directly with
/// synthetic scenarios so the tests are hermetic (no GitHub fetch).
/// </summary>
[TestClass]
public class FindUiSearchTests
{
    private static readonly string[] ExpectedProviderIds = ["gallery", "toolkit", "reactor"];

    private static Scenario Scn(string source, string controlId, string controlName, string id, string header, string? desc = null)
        => new()
        {
            Id = id,
            ControlId = controlId,
            ControlName = controlName,
            HeaderText = header,
            Source = source,
            ControlDescription = desc,
            Xaml = $"<{controlName} />",
        };

    private static SearchEngine BuildEngine() => new(
        [
            Scn("gallery", "tabview", "TabView", "tabview-1", "Add, close, and rearrange tabs", "Displays a collection of tabs."),
            Scn("gallery", "colorpicker", "ColorPicker", "colorpicker-1", "ColorPicker properties", "Selectable color spectrum."),
            Scn("toolkit", "colorpicker", "ColorPicker", "colorpicker-1", "Basic usage", "Extended color picker."),
            Scn("toolkit", "datagrid", "DataGrid", "datagrid-1", "Sortable grid", "Tabular data grid."),
            ReactorScn("flex", "Flex", "flex-1", "CSS-style flex layout", "Declarative flex container."),
        ],
        corePatterns: [],
        enrichmentTags: new(),
        curatedKeywords: new());

    /// <summary>Reactor scenarios are C#-only (no XAML) and carry a uniform NuGet package.</summary>
    private static Scenario ReactorScn(string controlId, string controlName, string id, string header, string? desc = null)
        => new()
        {
            Id = id,
            ControlId = controlId,
            ControlName = controlName,
            HeaderText = header,
            Source = "reactor",
            ControlDescription = desc,
            Xaml = null,
            CSharp = $"new {controlName}()",
            NuGetPackage = "Microsoft.UI.Reactor",
            ApiNamespace = "Microsoft.UI.Reactor",
        };

    [TestMethod]
    public void SearchGrouped_MatchesControlByName()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("tabview", maxControls: 5);
        Assert.IsTrue(groups.Count > 0, "expected at least one match");
        Assert.AreEqual("TabView", groups[0].ControlName);
        Assert.AreEqual("gallery-tabview-1", groups[0].Scenarios[0].Id);
    }

    [TestMethod]
    public void HasSource_LoadedSource_True()
    {
        var engine = BuildEngine();
        Assert.IsTrue(engine.HasSource("gallery"));
        Assert.IsTrue(engine.HasSource("toolkit"));
        Assert.IsTrue(engine.HasSource("reactor"));
    }

    [TestMethod]
    public void HasSource_IsCaseInsensitive()
    {
        Assert.IsTrue(BuildEngine().HasSource("Gallery"));
    }

    [TestMethod]
    public void HasSource_UnloadedSource_False()
    {
        // A corpus with no reactor scenarios (its fetch failed) must report the
        // source as absent so the command surfaces the friendly "run online once"
        // error rather than a false "no match".
        var engine = new SearchEngine(
            [Scn("gallery", "tabview", "TabView", "tabview-1", "Tabs")],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());
        Assert.IsFalse(engine.HasSource("reactor"));
        Assert.IsFalse(engine.HasSource("toolkit"));
    }

    [TestMethod]
    public void HasSource_Core_TrueOnlyWhenCorePatternsPresent()
    {
        var withCore = new SearchEngine(
            System.Array.Empty<Scenario>(),
            corePatterns: [new CorePattern { Id = "file-picker-desktop", Scenario = "Pick a file", CSharp = "// ..." }],
            enrichmentTags: new(),
            curatedKeywords: new());
        Assert.IsTrue(withCore.HasSource("core"));

        var noCore = new SearchEngine(
            [Scn("gallery", "tabview", "TabView", "tabview-1", "Tabs")],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());
        Assert.IsFalse(noCore.HasSource("core"));
    }

    [TestMethod]
    public void SearchGrouped_SourceFilter_RestrictsToProvider()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("color picker", maxControls: 5, sourceFilter: "toolkit");
        Assert.IsTrue(groups.Count > 0);
        Assert.IsTrue(groups.All(g => g.Source == "toolkit"), "every result must be from the toolkit source");
    }

    [TestMethod]
    public void SearchGrouped_SourceFilter_RestrictsToReactor()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("flex layout", maxControls: 5, sourceFilter: "reactor");
        Assert.IsTrue(groups.Count > 0);
        Assert.IsTrue(groups.All(g => g.Source == "reactor"), "every result must be from the reactor source");
    }

    [TestMethod]
    public void GetPattern_ReactorScenario_TagsAndNuGetSetup_NoXaml()
    {
        var engine = BuildEngine();
        var (formatted, found, _) = engine.GetPattern("reactor-flex-1");
        Assert.IsTrue(found);
        StringAssert.Contains(formatted, "[Reactor]");
        StringAssert.Contains(formatted, "**Setup:** NuGet `Microsoft.UI.Reactor`");
        // All reactor controls share Microsoft.UI.Reactor, so the namespace hint is suppressed.
        Assert.IsFalse(formatted.Contains("**Namespace:**"), "reactor must not emit a **Namespace:** line");
        // Reactor samples are C#-only — no XAML block.
        Assert.IsFalse(formatted.Contains("**XAML:**"), "reactor scenarios have no XAML");
    }

    [TestMethod]
    public void SearchGrouped_NoMatch_ReturnsEmpty()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("zzzznonexistentcontrol", maxControls: 5);
        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public void GetPattern_ByPrefixedId_ReturnsScenario()
    {
        var engine = BuildEngine();
        var (formatted, found, canonicalId) = engine.GetPattern("gallery-tabview-1");
        Assert.IsTrue(found);
        StringAssert.Contains(formatted, "TabView");
        Assert.AreEqual("gallery-tabview-1", canonicalId, "an exact id resolves to itself");
    }

    [TestMethod]
    public void GetPattern_ByBareControlId_ReturnsCanonicalScenarioId()
    {
        // The fallback resolver accepts a bare control id ("gallery-tabview") and
        // returns the lowest-numbered scenario. GetPattern must report the CANONICAL
        // scenario id (gallery-tabview-1), not echo the caller's bare token — this is
        // what usage telemetry emits, so it must never be the raw user input.
        var engine = BuildEngine();
        var (_, found, canonicalId) = engine.GetPattern("gallery-tabview");
        Assert.IsTrue(found, "a bare control id must still resolve");
        Assert.AreEqual("gallery-tabview-1", canonicalId, "must be the canonical scenario id, not the bare input");
    }

    [TestMethod]
    public void GetPattern_RespectsSourcePrefix_OnCollidingId()
    {
        var engine = BuildEngine();
        // Both gallery and toolkit expose colorpicker-1; the prefix must disambiguate.
        var (gallery, gFound, gId) = engine.GetPattern("gallery-colorpicker-1");
        var (toolkit, tFound, tId) = engine.GetPattern("toolkit-colorpicker-1");
        Assert.IsTrue(gFound);
        Assert.IsTrue(tFound);
        StringAssert.Contains(gallery, "ColorPicker properties");
        StringAssert.Contains(toolkit, "Basic usage");
        Assert.AreEqual("gallery-colorpicker-1", gId);
        Assert.AreEqual("toolkit-colorpicker-1", tId);
    }

    [TestMethod]
    public void GetPattern_IsCaseInsensitive_ForPrefixExactAndBareIds()
    {
        // #718: ids are copied by humans and agents, so casing must not matter. An
        // uppercase id resolves to the same scenario and reports the canonical (lowercase)
        // id — for exact ids, source-prefixed bare ids, and reactor (opt-in) ids alike.
        var engine = BuildEngine();

        var (upper, upperFound, upperId) = engine.GetPattern("GALLERY-TABVIEW-1");
        Assert.IsTrue(upperFound, "an uppercase exact id must resolve");
        StringAssert.Contains(upper, "TabView");
        Assert.AreEqual("gallery-tabview-1", upperId, "canonical id must be the lowercase form, not the caller's casing");

        var (_, bareFound, bareId) = engine.GetPattern("Gallery-TabView");
        Assert.IsTrue(bareFound, "a mixed-case bare control id must resolve via the fallback");
        Assert.AreEqual("gallery-tabview-1", bareId);

        var (_, reactorFound, reactorId) = engine.GetPattern("REACTOR-FLEX-1");
        Assert.IsTrue(reactorFound, "an uppercase reactor id must resolve");
        Assert.AreEqual("reactor-flex-1", reactorId);
    }

    [TestMethod]
    public void GetPattern_UnknownId_ReturnsNotFound()
    {
        var engine = BuildEngine();
        var (_, found, canonicalId) = engine.GetPattern("does-not-exist");
        Assert.IsFalse(found);
        Assert.IsNull(canonicalId, "an unresolved id has no canonical id (and is never emitted)");
    }

    [TestMethod]
    public void ListAll_EnumeratesEveryScenario()
    {
        var engine = BuildEngine();
        var ids = engine.ListAll().Select(x => x.id).ToList();
        CollectionAssert.Contains(ids, "gallery-tabview-1");
        CollectionAssert.Contains(ids, "toolkit-datagrid-1");
        CollectionAssert.Contains(ids, "reactor-flex-1");
        Assert.AreEqual(5, ids.Count);
    }

    [TestMethod]
    public void ProviderRegistry_ValidatesSourceFilterValues()
    {
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("gallery"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("toolkit"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("reactor"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("core"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("GALLERY"), "source filter is case-insensitive");
        Assert.IsFalse(ProviderRegistry.IsValidSourceFilter("wpf"));
    }

    [TestMethod]
    public void ProviderRegistry_CreateProviders_IncludesReactor()
    {
        var providers = ProviderRegistry.CreateProviders(Path.Combine(Path.GetTempPath(), "find-ui-test-cache"));
        var ids = providers.Select(p => p.Id).ToList();
        CollectionAssert.AreEqual(ExpectedProviderIds, ids,
            "the live provider factory must wire gallery, toolkit, and reactor in display order");
        Assert.IsInstanceOfType<ReactorProvider>(providers.Single(p => p.Id == "reactor"));
    }

    [TestMethod]
    public void ProviderRegistry_ForScenarioId_MapsPrefixToProvider()
    {
        Assert.AreEqual("gallery", ProviderRegistry.ForScenarioId("gallery-tabview-1")?.Id);
        Assert.AreEqual("toolkit", ProviderRegistry.ForScenarioId("toolkit-datagrid-1")?.Id);
        Assert.AreEqual("reactor", ProviderRegistry.ForScenarioId("reactor-flex-1")?.Id);
        Assert.IsNull(ProviderRegistry.ForScenarioId("core-navview"));
    }

    [TestMethod]
    public void ProviderRegistry_ForScenarioId_IsCaseInsensitive()
    {
        // #718: source routing (which provider to load, incl. opt-in reactor) must not
        // depend on the casing of a hand-copied id, or "REACTOR-FLEX-1" would resolve as
        // a scenario yet skip loading the reactor provider.
        Assert.AreEqual("gallery", ProviderRegistry.ForScenarioId("GALLERY-TABVIEW-1")?.Id);
        Assert.AreEqual("reactor", ProviderRegistry.ForScenarioId("Reactor-Flex-1")?.Id);
        Assert.IsTrue(ProviderRegistry.IsReactorScenarioId("REACTOR-FLEX-1"),
            "an uppercase reactor id must still be recognized as reactor so the provider loads");
    }

    /// <summary>
    /// Corpus for the adjectival-intent tests. Tags mirror the shipped
    /// <c>gallery-tags.json</c> entries verbatim, because the bug this guards against
    /// only reproduces with the real tag distribution: SwipeControl's tags carry
    /// neither "list" nor "rows", while ListView's carry both.
    /// </summary>
    private static SearchEngine BuildSwipeEngine() => new(
        [
            Scn("gallery", "swipecontrol", "SwipeControl", "swipecontrol-3", "Custom Swipe in a ListView", "Touch gesture for quick menu actions on items."),
            Scn("gallery", "swipecontrol", "SwipeControl", "swipecontrol-1", "Swipe right to reveal actions", "Touch gesture for quick menu actions on items."),
            Scn("gallery", "listview", "ListView", "listview-1", "Basic ListView with Simple DataTemplate", "Presents a collection of items in a vertical list."),
        ],
        corePatterns: [],
        enrichmentTags: new()
        {
            ["gallery:swipecontrol"] = ["swipecontrol", "swipe", "gesture", "reveal", "action", "delete", "touch", "quick", "menu", "actions"],
            ["gallery:listview"] = ["listview", "list", "scroll", "select", "virtualized", "data", "collection", "vertical", "table", "columns", "column", "sort", "sorting", "editable", "datagrid", "rows", "details"],
        },
        curatedKeywords: new());

    [TestMethod]
    public void SearchGrouped_AdvertisedSwipeableQuery_RanksSwipeControlFirst()
    {
        // "swipeable list rows" is advertised in docs/usage.md, the find-ui skill and
        // the skill description, so it must actually work. It regressed three ways at
        // once: the stemmer had no -able rule, so "swipeable" never reached the "swipe"
        // tag; the coverage gate then zeroed SwipeControl for covering only 1 of the 3
        // typed tokens; and ListView won on the incidental "list"/"rows" tags.
        var groups = BuildSwipeEngine().SearchGrouped("swipeable list rows", maxControls: 5);
        Assert.IsTrue(groups.Count > 0, "the advertised query must return matches");
        Assert.AreEqual("SwipeControl", groups[0].ControlName, "SwipeControl must outrank ListView");
    }

    [TestMethod]
    public void SearchGrouped_AdjectivalIntent_SurvivesCoverageGate()
    {
        // Same intent with no supporting nouns the other control shares. The coverage
        // gate only applies at >= 3 typed tokens, so this pins the gate bypass itself
        // rather than the ranking.
        var groups = BuildSwipeEngine().SearchGrouped("swipeable rows of items", maxControls: 5);
        Assert.IsTrue(groups.Any(g => g.ControlName == "SwipeControl"),
            "a synonym that names a control must bypass the raw-token coverage gate");
    }

    [TestMethod]
    public void Stem_AbleAndIbleSuffixes_YieldVerbBase()
    {
        // Agents phrase intent adjectivally while the corpus indexes the verb.
        CollectionAssert.Contains(Synonyms.Stem("swipeable").ToList(), "swipe");
        CollectionAssert.Contains(Synonyms.Stem("scrollable").ToList(), "scroll");
        CollectionAssert.Contains(Synonyms.Stem("resizable").ToList(), "resize");
        CollectionAssert.Contains(Synonyms.Stem("collapsible").ToList(), "collapse");
        CollectionAssert.Contains(Synonyms.Stem("draggable").ToList(), "drag");
    }

    [TestMethod]
    public void Stem_ShortAbleWords_AreNotStripped()
    {
        // "table", "enable", "usable", "visible" and "disable" are whole words, not
        // inflections — stripping them would inject junk tokens into every query.
        foreach (var w in new[] { "table", "enable", "usable", "visible", "disable" })
        {
            Assert.AreEqual(0, Synonyms.Stem(w).Count(), $"'{w}' must not be suffix-stripped");
        }
    }

    /// <summary>Image and Grid are controls in their own right, so a photo-grid query
    /// competes with them on its literal words.</summary>
    [TestMethod]
    public void Preprocess_ImageGridPhrase_RoutesToCollectionControls()
    {
        // "image grid" is the plainest phrasing for the layout the ItemsRepeater notes
        // describe, but both of its words name other controls, so the literal tokens pull
        // the query to Image and Grid. Merging the phrase is what puts the collection
        // controls in front of the ranker at all — "photo grid" already worked.
        var expanded = Synonyms.Expand(BM25.Tokenize(Synonyms.Preprocess("image grid"))).ToList();

        foreach (var control in new[] { "itemsrepeater", "gridview", "itemsview" })
        {
            CollectionAssert.Contains(expanded, control, $"'image grid' must reach {control}");
        }
    }

    [TestMethod]
    public void GetPattern_ScenarioWithNoUpstreamCode_StillCarriesWinappGuidance()
    {
        // Upstream's CommandBar sample is a doc-style elided fragment ("..." inside the
        // markup), so the sanitizer drops its XAML and there is no code to emit. The
        // pattern still has to reach the caller as winapp-attributed guidance instead of
        // a successful-looking result with nothing in it.
        var engine = new SearchEngine(
            [
                new Scenario
                {
                    Id = "commandbar-1",
                    ControlId = "commandbar",
                    ControlName = "CommandBar",
                    HeaderText = "A command bar with labels on the side free floating in a page",
                    Source = "gallery",
                    Xaml = null,
                    CSharp = null,
                }
            ],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());

        var (formatted, found, _) = engine.GetPattern("gallery-commandbar-1");

        Assert.IsTrue(found, "the id find-ui advertises must resolve");
        StringAssert.Contains(formatted, "AppBarButton", "the toolbar shape must survive in the notes");
        StringAssert.Contains(formatted, "CommandBar.SecondaryCommands", "overflow placement must survive in the notes");
    }
}
