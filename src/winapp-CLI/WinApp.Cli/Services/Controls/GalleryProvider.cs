// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.RegularExpressions;

/// <summary>
/// WinUI 3 Gallery scenarios (<c>microsoft/WinUI-Gallery</c>), read from the sample index
/// that repository publishes and validates on every PR. Downloading and mapping it is
/// <see cref="SampleIndexFetcher"/>'s job; what remains here is the one thing the index
/// does not supply — Gallery's search enrichment tags.
///
/// <para>Those tags are not cosmetic. <see cref="SearchEngine"/> builds a control's BM25
/// document from its name, id, curated keywords (weight 5.0), enrichment tags (3.0) and
/// scenario headers (0.8) — a control's prose description is never indexed. So for a query
/// expressing intent rather than a control name ("reusable style definitions"), the
/// enrichment tags are the only field that can match, and a control without them is
/// reachable only by naming it.</para>
///
/// <para>The index's own <c>keywords</c> can't fill that role: Gallery populates them from
/// a control's base classes (<c>Object</c>, <c>DependencyObject</c>, <c>UIElement</c>),
/// which describe the type hierarchy rather than what the control is for, and which every
/// control shares — indexing them would make "control" or "element" match the whole
/// catalog. The intent-bearing tags are therefore derived here, from the control name and
/// description, exactly as they were when this data came from <c>ControlInfoData.json</c>
/// via a scraper. The index's separate <c>curatedKeywords</c> are hand-written intent
/// phrases ("tab control", "notification dot") and are carried through as the weight-5.0
/// curated keyword field, which the scraper had no way to populate.</para>
/// </summary>
internal sealed partial class GalleryProvider : CachedProviderBase
{
    public GalleryProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "gallery";
    public override string DisplayName => "Gallery (WinUI 3)";

    /// <summary>Cap on derived tags per control. Beyond roughly a dozen terms the marginal
    /// term is noise that dilutes BM25 scoring rather than adding recall.</summary>
    private const int MaxDerivedTags = 12;

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, _, curatedKeywords) = await SampleIndexFetcher
            .FetchAsync(SampleIndexFetcher.GalleryIndexUrl, Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var scenario in scenarios)
        {
            NormalizeForPaste(scenario);
        }

        return scenarios.Length > 0
            ? new ProviderData(scenarios, BuildEnrichmentTags(scenarios), curatedKeywords)
            : ProviderData.Empty;
    }

    /// <summary>Gallery's own page class declaration, e.g.
    /// <c>x:Class="WinUIGallery.ControlPages.ButtonPage"</c>.</summary>
    [GeneratedRegex(@"x:Class=""(?:WinUIGallery|AppUIBasics)\.[^""]+""")]
    private static partial Regex GalleryClassRegex();

    /// <summary>Gallery's per-scenario navigation targets (<c>SamplePage</c>, <c>SamplePage1</c>, …),
    /// which exist only inside the Gallery app. Anchored at the start of an identifier but not the
    /// end: <c>SamplePage2Item</c> is Gallery-only too, while prose naming a Gallery source file
    /// (<c>TabViewWindowingSamplePage.xaml</c>) is left alone.</summary>
    [GeneratedRegex(@"\bSamplePage\d*")]
    private static partial Regex SamplePageNameRegex();

    /// <summary>A <c>namespace</c> declaration putting the sample in Gallery's own namespace.</summary>
    [GeneratedRegex(@"namespace\s+(?:WinUIGallery|AppUIBasics)[^{;\n]*;?")]
    private static partial Regex GalleryNamespaceRegex();

    /// <summary>A <c>using</c> of Gallery's own namespace.</summary>
    [GeneratedRegex(@"using\s+(?:WinUIGallery|AppUIBasics)[^;\n]*;?")]
    private static partial Regex GalleryUsingRegex();

    /// <summary>A statement calling <c>UIHelper</c>, Gallery's internal accessibility helper.</summary>
    [GeneratedRegex(@"^[^\S\n]*UIHelper\.[^;]+;.*\n?", RegexOptions.Multiline)]
    private static partial Regex UiHelperCallRegex();

    /// <summary>
    /// Make one index scenario pasteable.
    ///
    /// <para>The index publishes Gallery's sample source verbatim, so a scenario still
    /// refers to types that exist only inside the Gallery app — its page classes, its
    /// <c>SamplePageN</c> navigation targets, its <c>UIHelper</c> accessibility helper —
    /// and still wires XAML events to handlers whose implementations live in page
    /// code-behind the index does not carry. Pasted into a user's app as-is, those
    /// snippets do not compile, which defeats the point of serving them.</para>
    ///
    /// <para><see cref="ToolkitFetcher"/> runs the same normalization over its own source;
    /// this is Gallery's half of it.</para>
    /// </summary>
    private static void NormalizeForPaste(Scenario scenario)
    {
        if (!string.IsNullOrEmpty(scenario.CSharp))
        {
            var csharp = UiHelperCallRegex().Replace(scenario.CSharp, "");
            csharp = GalleryUsingRegex().Replace(csharp, "// adapt namespace to your app");
            csharp = GalleryNamespaceRegex().Replace(csharp, "namespace YourApp;");
            csharp = SamplePageNameRegex().Replace(csharp, "YourPage");
            csharp = GalleryClassRegex().Replace(csharp, @"x:Class=""YourApp.YourPage""");
            scenario.CSharp = string.IsNullOrWhiteSpace(csharp) ? null : csharp.Trim();
        }

        if (!string.IsNullOrEmpty(scenario.Xaml))
        {
            var xaml = GalleryClassRegex().Replace(scenario.Xaml, @"x:Class=""YourApp.YourPage""");
            xaml = SamplePageNameRegex().Replace(xaml, "YourPage");

            // Strip handlers against the C# settled above, so an attribute survives only
            // when the code we actually serve declares its method. This is deliberately
            // narrower than a blanket rule: TabView really does supply TabView_Loaded, and
            // that handler is kept.
            scenario.Xaml = ControlSnippetText.StripUnbackedEventHandlers(xaml, scenario.CSharp);
        }
    }

    /// <summary>Word characters that make up a derived tag: three or more letters, so
    /// articles and fragments don't become search terms.</summary>
    [GeneratedRegex(@"[a-z]{3,}")]
    private static partial Regex TagWordRegex();

    /// <summary>Boundary between a lowercase and an uppercase letter, so "AutoSuggestBox"
    /// also contributes "auto" and "suggest".</summary>
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    /// <summary>
    /// Build the per-control enrichment tag dictionary: hand-curated terms where we have
    /// them, otherwise terms derived from the control's own name and description.
    /// </summary>
    /// <remarks>
    /// Curated tags win outright rather than merging with derived ones. They exist because
    /// the derived terms were judged insufficient for that control, so appending the
    /// rejected terms back would reinstate what the curation set out to replace.
    /// </remarks>
    private static Dictionary<string, string[]> BuildEnrichmentTags(Scenario[] scenarios)
    {
        var curated = DataLoader.LoadGalleryTags();
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var scenario in scenarios)
        {
            var controlId = scenario.ControlId;
            if (string.IsNullOrEmpty(controlId) || tags.ContainsKey(controlId)) continue;

            tags[controlId] = curated.TryGetValue(controlId, out var manual)
                ? StopWords.FilterTagList(manual)
                : DeriveTags(controlId, scenario);
        }

        // Curated entries for controls the index doesn't carry still belong in the
        // dictionary: they cost nothing when the control is absent, and they keep a
        // control searchable through a release where upstream temporarily drops it.
        foreach (var (controlId, manual) in curated)
        {
            if (!tags.ContainsKey(controlId)) tags[controlId] = StopWords.FilterTagList(manual);
        }

        return tags;
    }

    /// <summary>
    /// Derive intent tags for one control from the text the index already carries: its
    /// display name, its one-line summary, and its longer description. This is the same
    /// source text ("Title Subtitle Description") the scraper read straight out of
    /// <c>ControlInfoData.json</c>, so the derived vocabulary is unchanged by the move to
    /// the index.
    /// </summary>
    private static string[] DeriveTags(string controlId, Scenario scenario)
    {
        var text = $"{scenario.ControlName} {scenario.ControlDescription} {scenario.Description}";
        text = CamelCaseBoundaryRegex().Replace(text, " ");

        var tags = new List<string> { controlId };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { controlId };

        foreach (Match m in TagWordRegex().Matches(text.ToLowerInvariant()))
        {
            if (StopWords.IsTagNoise(m.Value)) continue;
            if (seen.Add(m.Value)) tags.Add(m.Value);
            if (tags.Count >= MaxDerivedTags) break;
        }

        return [.. tags];
    }
}
