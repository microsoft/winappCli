// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

[TestClass]
public class FindUiCommandTests : BaseCommandTests
{
    private IControlsSearchService _fakeService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        // Resolve the current fake at invoke time so each test can pick engine-vs-unavailable.
        return services.AddSingleton<IControlsSearchService>(_ => _fakeService);
    }

    private static SearchEngine BuildEngine() => new(
        [
            Scn("gallery", "tabview", "TabView", "tabview-1", "Add, close, and rearrange tabs"),
            Scn("toolkit", "datagrid", "DataGrid", "datagrid-1", "Sortable grid"),
        ],
        corePatterns: [],
        enrichmentTags: new(),
        curatedKeywords: new());

    /// <summary>A corpus of core patterns alone — what a <c>--source core</c> search sees.</summary>
    private static SearchEngine CoreEngine() => new(
        [],
        corePatterns:
        [
            new CorePattern
            {
                Id = "system-tray-icon",
                Scenario = "System tray icon",
                Tags = ["system", "tray", "notification area"],
                Description = "Show an icon in the notification area",
                CSharp = "// tray icon",
            },
        ],
        enrichmentTags: new(),
        curatedKeywords: new());

    private static Scenario Scn(string source, string controlId, string controlName, string id, string header)
        => new() { Id = id, ControlId = controlId, ControlName = controlName, HeaderText = header, Source = source, Xaml = $"<{controlName} />" };

    private FindUiCommand Command()
    {
        _fakeService ??= FakeControlsSearchService.WithEngine(BuildEngine());
        return GetRequiredService<FindUiCommand>();
    }

    [TestMethod]
    public async Task Id_CodeWithBrackets_RendersVerbatim_Exit0()
    {
        // Code and headers containing '[' / ']' must be escaped/written verbatim, not
        // interpreted as Spectre markup (which would throw or mangle the snippet).
        var engine = new SearchEngine(
            [
                new Scenario
                {
                    Id = "tabview-1",
                    ControlId = "tabview",
                    ControlName = "TabView",
                    HeaderText = "Brackets [test]",
                    Source = "gallery",
                    Xaml = "<Grid Tag=\"[binding]\" />",
                    CSharp = "var first = items[0];",
                },
            ],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());
        _fakeService = FakeControlsSearchService.WithEngine(engine);

        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "items[0]", "C# code must survive verbatim");
        StringAssert.Contains(TestAnsiConsole.Output, "[binding]", "XAML brackets must survive verbatim");
        StringAssert.Contains(TestAnsiConsole.Output, "Brackets [test]", "heading brackets must survive verbatim");
    }

    [TestMethod]
    public async Task Id_LabelWithBackticks_RendersVerbatim_Exit0()
    {
        // A reactor scenario emits "**Setup:** NuGet `Microsoft.UI.Reactor`". The label is
        // tinted and the backticked trailing content is escaped and rendered verbatim.
        var engine = new SearchEngine(
            [
                new Scenario
                {
                    Id = "flex-1",
                    ControlId = "flex",
                    ControlName = "Flex",
                    HeaderText = "Flex container",
                    Source = "reactor",
                    CSharp = "new FlexElement()",
                    NuGetPackage = "Microsoft.UI.Reactor",
                    ApiNamespace = "Microsoft.UI.Reactor",
                },
            ],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());
        _fakeService = FakeControlsSearchService.WithEngine(engine);

        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "reactor-flex-1"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "**Setup:**", "the metadata label must survive");
        StringAssert.Contains(TestAnsiConsole.Output, "NuGet `Microsoft.UI.Reactor`", "backticked trailing content must render verbatim");
    }

    [TestMethod]
    public async Task Id_CodeLineWiderThanConsole_IsNotWordWrapped_Exit0()
    {
        // Code bodies must reach the terminal verbatim. Routing them through IAnsiConsole
        // renders each line as a Spectre `Text`, which word-wraps to the console width; with
        // no wide TTY (piping to a file, CI logs, a narrow window) a wrap can land mid-token
        // — e.g. "</DataTemplate>" split into "</DataT" + "emplate>" — yielding invalid XAML.
        // A single line wider than the console must survive on one line.
        var longXaml =
            "<TextBlock Foreground=\"{ThemeResource SystemControlPageTextBaseMediumBrush}\" Text=\""
            + new string('A', 80) + "\" />";
        var engine = new SearchEngine(
            [
                new Scenario
                {
                    Id = "wideline-1",
                    ControlId = "widelineprobe",
                    ControlName = "WideLineProbe",
                    HeaderText = "Wide line",
                    Source = "gallery",
                    Xaml = longXaml,
                },
            ],
            corePatterns: [],
            enrichmentTags: new(),
            curatedKeywords: new());
        _fakeService = FakeControlsSearchService.WithEngine(engine);

        // Pin a narrow width with no interactive TTY — the exact condition that reflows output.
        TestAnsiConsole.Profile.Width = 80;

        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-wideline-1"]);

        Assert.AreEqual(0, exit);

        // Direct proof: the full wide line appears intact, never split across a wrap.
        StringAssert.Contains(TestAnsiConsole.Output, longXaml,
            "a code line wider than the console must not be word-wrapped");

        // Width-independent invariant (Nikola's suggestion): the plain render has exactly the
        // line count of the canonical --json content. Wrapping would inflate it. The probe
        // control carries no curated notes, so every structural line is short — the wide code
        // line is the only wrap candidate.
        var (content, _, _) = engine.GetPattern("gallery-wideline-1");
        static int LineCount(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        Assert.AreEqual(LineCount(content), LineCount(TestAnsiConsole.Output),
            "plain render line count must match the canonical --json content — extra lines mean a snippet was word-wrapped");
    }

    [TestMethod]
    public async Task Search_NonJson_PassesFetchNoticeCallback()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        Assert.AreEqual(0, exit);
        Assert.IsNotNull(fake.LastOnFetchStarting, "interactive (non-json) runs should supply a fetching-notice callback");
    }

    [TestMethod]
    public async Task Search_Json_SuppressesFetchNoticeCallback()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        Assert.AreEqual(0, exit);
        Assert.IsNull(fake.LastOnFetchStarting, "--json must suppress the fetching notice so stdout stays clean");
    }

    [TestMethod]
    public async Task List_PassesAllowCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--list"]);
        Assert.IsTrue(fake.LastAllowCoreOnly, "--list should allow a core-only corpus so it works offline");
    }

    [TestMethod]
    public async Task SourceCore_PassesAllowCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["file picker", "--source", "core"]);
        Assert.IsTrue(fake.LastAllowCoreOnly, "--source core should allow a core-only corpus offline");
    }

    [TestMethod]
    public async Task Search_DoesNotAllowCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        Assert.IsFalse(fake.LastAllowCoreOnly, "a normal search needs the network corpus; keep the cold-start error");
    }

    [TestMethod]
    public async Task IdCoreOnly_PassesAllowCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "file-picker-desktop"]);
        Assert.IsTrue(fake.LastAllowCoreOnly, "a core-prefixed id needs no network");
    }

    [TestMethod]
    public async Task IdNetwork_DoesNotAllowCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);
        Assert.IsFalse(fake.LastAllowCoreOnly, "a gallery id needs the network corpus");
    }

    [TestMethod]
    public async Task SourceCore_PassesCoreOnly_SkipsNetwork()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["file picker", "--source", "core"]);
        Assert.IsTrue(fake.LastCoreOnly, "--source core is satisfiable by embedded patterns; skip the network providers");
    }

    [TestMethod]
    public async Task IdCore_PassesCoreOnly_SkipsNetwork()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "file-picker-desktop"]);
        Assert.IsTrue(fake.LastCoreOnly, "an all-core --id set needs no network");
    }

    [TestMethod]
    public async Task Search_DoesNotPassCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        Assert.IsFalse(fake.LastCoreOnly, "a normal search must still load the network corpus");
    }

    [TestMethod]
    public async Task List_DoesNotPassCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--list"]);
        Assert.IsFalse(fake.LastCoreOnly, "--list lists every source, so it still wants the network corpus online");
    }

    [TestMethod]
    public async Task IdNetwork_DoesNotPassCoreOnly()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);
        Assert.IsFalse(fake.LastCoreOnly, "a gallery id needs the network corpus");
    }

    [TestMethod]
    public async Task Search_DoesNotIncludeReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["data grid"]);
        Assert.IsFalse(fake.LastIncludeReactor, "a default search must exclude the opt-in Reactor source");
    }

    [TestMethod]
    public async Task List_DoesNotIncludeReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--list"]);
        Assert.IsFalse(fake.LastIncludeReactor, "--list browses the default corpus; Reactor stays opt-in");
    }

    [TestMethod]
    public async Task SourceReactor_IncludesReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["flex layout", "--source", "reactor"]);
        Assert.IsTrue(fake.LastIncludeReactor, "--source reactor is the explicit opt-in and must load Reactor");
    }

    [TestMethod]
    public async Task SourceGallery_DoesNotIncludeReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--source", "gallery"]);
        Assert.IsFalse(fake.LastIncludeReactor, "a non-reactor --source must not pull in Reactor");
    }

    [TestMethod]
    public async Task IdReactor_IncludesReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "reactor-flex-1"]);
        Assert.IsTrue(fake.LastIncludeReactor, "fetching a reactor-* id must load Reactor so the id resolves");
    }

    [TestMethod]
    public async Task IdGallery_DoesNotIncludeReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);
        Assert.IsFalse(fake.LastIncludeReactor, "a non-reactor id must not load Reactor");
    }

    [TestMethod]
    public async Task IdMixedWithReactor_IncludesReactor()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1", "--id", "reactor-flex-1"]);
        Assert.IsTrue(fake.LastIncludeReactor, "any reactor-* id in a batch must load Reactor");
    }

    [TestMethod]
    public async Task NoArgs_PrintsGuidance_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), []);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task InvalidSource_Json_EmitsError_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--source", "wpf", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "--source must be one of");
    }

    [TestMethod]
    public async Task MaxZero_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--max", "0"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task SourceWithList_Rejected_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--list", "--source", "toolkit", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "--source only applies to search");
    }

    [TestMethod]
    public async Task Search_Hit_ListsScenarioId_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
    }

    [TestMethod]
    public async Task Search_Miss_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["zzzznotacontrol"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task Search_Json_EmitsMatchCount_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"matchCount\"");
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
    }

    [TestMethod]
    public async Task SourceCore_Json_ReportsEmbeddedCorpus_Exit0()
    {
        // A successful core-only search must name its corpus. The core patterns are
        // compiled into the binary, so "embedded" is the honest label; a null corpus on a
        // successful result is indistinguishable from a failed load on the JSON surface.
        var fake = FakeControlsSearchService.WithEngine(CoreEngine());
        fake.LoadedOrigin = CorpusOrigin.Embedded;
        _fakeService = fake;

        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["system tray", "--source", "core", "--json"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"corpus\": \"embedded\"");
    }

    [TestMethod]
    public async Task NothingLoaded_Json_OmitsCorpus_Exit1()
    {
        // The other side of the contract: an absent/null corpus means nothing loaded, and
        // nothing else.
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        fake.LoadedOrigin = CorpusOrigin.None;
        _fakeService = fake;

        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["zzzznotacontrol", "--json"]);

        Assert.AreEqual(1, exit);
        Assert.IsFalse(TestAnsiConsole.Output.Contains("\"corpus\"", StringComparison.Ordinal),
            $"no corpus loaded must leave the field unreported. Got: {TestAnsiConsole.Output}");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-global Console.Error
    public async Task SourceCore_DoesNotWarnThatTheCorpusMayLagUpstream()
    {
        // The core patterns are curated and compiled in — they track no upstream repo and
        // --refresh cannot change them, so the staleness notice would be wrong on both
        // counts even though the origin is the embedded tier.
        var fake = FakeControlsSearchService.WithEngine(CoreEngine());
        fake.LoadedOrigin = CorpusOrigin.Embedded;
        _fakeService = fake;

        var originalError = Console.Error;
        using var stderr = new StringWriter();
        int exit;
        try
        {
            Console.SetError(stderr);
            exit = await ParseAndInvokeWithCaptureAsync(Command(), ["system tray", "--source", "core"]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(0, exit);
        Assert.IsFalse(stderr.ToString().Contains("built into the CLI", StringComparison.Ordinal),
            $"a core-only result must not be labeled a stale upstream snapshot. Got: {stderr}");
    }

    [TestMethod]
    public async Task Id_Found_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "TabView");
    }

    [TestMethod]
    public async Task Id_Unknown_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "nope-xyz"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task List_Exit0_ListsScenarios()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--list"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "toolkit-datagrid-1");
    }

    [TestMethod]
    public async Task DataUnavailable_Json_EmitsError_Exit1()
    {
        _fakeService = FakeControlsSearchService.Unavailable();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "No WinUI control data is available");
    }

    [TestMethod]
    public async Task MultipleModes_Rejected_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--list", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "Choose one of");
    }

    [TestMethod]
    public async Task Id_MultipleValues_AllFetched()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1", "--id", "toolkit-datagrid-1"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "TabView");
        StringAssert.Contains(TestAnsiConsole.Output, "DataGrid");
    }

    [TestMethod]
    public async Task BareIdWithoutValue_Rejected()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        // --id requires at least one operand (OneOrMore); a bare --id is a parse error.
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "--list"]);
        Assert.AreNotEqual(0, exit, "a bare --id with no value must not silently fall through to another mode");
    }

    [TestMethod]
    public async Task Refresh_PassedThroughToService()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--refresh"]);
        Assert.AreEqual(0, exit);
        Assert.IsTrue(fake.LastForceRefresh, "--refresh should be forwarded to the search service");
    }

    // ── H4: a source that never loaded must not be reported as a plain "no match" ──
    // BuildEngine() holds only gallery + toolkit scenarios, so this engine models a
    // partially-warm corpus in which the reactor fetch failed but another source is warm.

    [TestMethod]
    public async Task SearchSourceReactor_SourceFailedToLoad_ReportsUnavailable_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["flex layout", "--source", "reactor", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "control data could be loaded",
            "a --source whose corpus never loaded must surface the friendly error, not a false 'no match'");
        StringAssert.Contains(TestAnsiConsole.Output, "reactor", "the error must name the unavailable source");
    }

    [TestMethod]
    public async Task IdReactor_SourceFailedToLoad_ReportsUnavailable_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "reactor-flex-1", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "control data could be loaded",
            "a --id whose source never loaded must surface the friendly error, not 'Pattern not found'");
        StringAssert.Contains(TestAnsiConsole.Output, "reactor", "the error must name the unavailable source");
    }

    [TestMethod]
    public async Task SearchSourceGallery_SourceLoaded_StillSearches_Exit0()
    {
        // A loaded source must NOT trip the H4 guard — regression check that HasSource is honored.
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--source", "gallery"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
    }
}
