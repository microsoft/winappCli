// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.RegularExpressions;

/// <summary>
/// Windows Community Toolkit scenarios (<c>CommunityToolkit/Windows</c>), read from the
/// sample index that repository publishes and checks against its samples on every PR.
/// Downloading and mapping it is <see cref="SampleIndexFetcher"/>'s job; what remains here
/// is the search enrichment the index does not supply, and a final pass over the markup.
///
/// <para>The index replaced a scraper that reconstructed the same data from repository
/// layout across hundreds of requests, and needed hand-maintained tables to correct what
/// it got wrong. Those tables are gone: the exporter reads the samples with the same
/// parser the Toolkit's own sample browser uses, so it cannot disagree with the sample
/// app about what a sample is called or what its options default to.</para>
/// </summary>
internal sealed partial class ToolkitProvider : CachedProviderBase
{
    public ToolkitProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "toolkit";
    public override string DisplayName => "CommunityToolkit";

    /// <summary>Cap on derived tags per control, matching <see cref="GalleryProvider"/>:
    /// beyond roughly a dozen terms the marginal term dilutes BM25 scoring rather than
    /// adding recall.</summary>
    private const int MaxDerivedTags = 12;

    protected override Dictionary<string, string[]> NormalizeTagsOnRead(
        Dictionary<string, string[]> tags) => StopWords.CleanTagDictionary(tags);

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, indexTags, curatedKeywords) = await SampleIndexFetcher
            .FetchAsync(SampleIndexFetcher.ToolkitIndexUrl, Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var scenario in scenarios)
        {
            NormalizeForPaste(scenario);
        }

        return scenarios.Length > 0
            ? new ProviderData(
                scenarios,
                StopWords.CleanTagDictionary(BuildEnrichmentTags(scenarios, indexTags)),
                curatedKeywords)
            : ProviderData.Empty;
    }

    /// <summary>Boundary between a lowercase and an uppercase letter, so "SettingsCard"
    /// also contributes "settings" and "card".</summary>
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    /// <summary>Word characters that make up a derived tag: three or more letters, so
    /// articles and fragments don't become search terms.</summary>
    [GeneratedRegex(@"[a-z]{3,}")]
    private static partial Regex TagWordRegex();

    /// <summary>
    /// Build the per-control enrichment tag dictionary (search weight 3.0).
    /// </summary>
    /// <remarks>
    /// <see cref="SearchEngine"/> builds a control's BM25 document from its name, id,
    /// curated keywords (5.0), enrichment tags (3.0) and scenario headers (0.8) — a
    /// control's prose description is never indexed. So for a query expressing intent
    /// rather than a control name ("settings page layout"), these tags are the only field
    /// that can match, and a control without them is reachable only by naming it.
    ///
    /// <para>The terms are the ones the scraper produced, from the same source text: the
    /// control id, its title split on camel case, the author's keywords, and the category
    /// the component is filed under. What changed is where they are read from — the index
    /// states them, where the scraper re-derived them from raw markdown on every fetch.</para>
    /// </remarks>
    private static Dictionary<string, string[]> BuildEnrichmentTags(
        Scenario[] scenarios,
        Dictionary<string, string[]> indexTags)
    {
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var scenario in scenarios)
        {
            var controlId = scenario.ControlId;
            if (string.IsNullOrEmpty(controlId) || tags.ContainsKey(controlId)) continue;

            var terms = new List<string> { controlId };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { controlId };

            // The index's own keywords are the category and subcategory the component is
            // filed under — "Layout", "Input" — which is intent vocabulary a control name
            // never carries.
            if (indexTags.TryGetValue(controlId, out var fromIndex))
            {
                foreach (var keyword in fromIndex)
                {
                    var term = keyword.Trim().ToLowerInvariant();
                    if (term.Length > 0 && !StopWords.IsTagNoise(term) && seen.Add(term)) terms.Add(term);
                }
            }

            var text = CamelCaseBoundaryRegex().Replace(scenario.ControlName ?? "", " ");
            foreach (Match match in TagWordRegex().Matches(text.ToLowerInvariant()))
            {
                if (StopWords.IsTagNoise(match.Value)) continue;
                if (seen.Add(match.Value)) terms.Add(match.Value);
                if (terms.Count >= MaxDerivedTags) break;
            }

            tags[controlId] = [.. terms];
        }

        return tags;
    }

    /// <summary>The Toolkit's own license header, in the three shapes its sample files use.</summary>
    [GeneratedRegex(@"^//\s*Licensed to.*?(?=\n[^/])", RegexOptions.Singleline)]
    private static partial Regex LicenseBlockRegex();

    [GeneratedRegex(@"^//\s*The \.NET Foundation.*?\n", RegexOptions.Multiline)]
    private static partial Regex LicenseFoundationRegex();

    [GeneratedRegex(@"^//\s*See the LICENSE.*?\n", RegexOptions.Multiline)]
    private static partial Regex LicenseSeeAlsoRegex();

    /// <summary>
    /// The sample's own class declaration.
    /// </summary>
    /// <remarks>
    /// The <c>Sample</c> suffix is required, and is not decoration: a sample may declare
    /// helper types beside itself — <c>AdvancedCollectionViewSample</c> ships a
    /// <c>Person</c> class its binding needs — and those are types the user is meant to
    /// keep under their own names. Matching the first class in the file renamed
    /// <c>Person</c> to <c>YourPage</c> and left the sample class alone, which is exactly
    /// backwards. Upstream names the sample class after its file, so the suffix always
    /// holds for the one declaration that should be renamed.
    /// </remarks>
    [GeneratedRegex(@"\b(?:public\s+|internal\s+)?(?:sealed\s+)?partial\s+class\s+(\w+Sample)\b")]
    private static partial Regex SampleClassRegex();

    /// <summary>
    /// The sample class's name as it survives in published code that no longer carries the
    /// declaration itself: a constructor.
    /// </summary>
    /// <remarks>
    /// The index publishes a sample's members without the class that contained them, because
    /// the user is pasting into their own page. A constructor is the one member that still
    /// spells the old class name, so it is the one that stops compiling. Requiring the
    /// <c>Sample</c> suffix keeps this from renaming a helper type the sample legitimately
    /// declares beside itself.
    /// </remarks>
    [GeneratedRegex(@"\b(?:public|internal|private|protected)\s+(\w+Sample)\s*\(")]
    private static partial Regex SampleConstructorRegex();

    /// <summary>The attributes the Toolkit's docs source generator reads to build the sample
    /// browser and its options pane. They are build-time metadata for an app the user is not
    /// building, and they reference a generator package a pasted sample does not reference.</summary>
    [GeneratedRegex(@"\[Toolkit(?:Sample|SampleOptionsPane|SampleMultiChoiceOption|SampleNumericOption|SampleBoolOption|SampleTextOption)\b[^\]]*\][\r\n]*", RegexOptions.Singleline)]
    private static partial Regex ToolkitSampleAttributeRegex();

    [GeneratedRegex(@"\[SuppressMessage[^\]]*\][\r\n]*", RegexOptions.Singleline)]
    private static partial Regex SuppressMessageAttributeRegex();

    /// <summary>A <c>namespace</c> declaration putting the sample in the Toolkit's own
    /// namespace, in either block or file-scoped form.</summary>
    [GeneratedRegex(@"namespace\s+[\w.]+\s*(?:;|\{)\s*")]
    private static partial Regex ToolkitNamespaceRegex();

    /// <summary>The docs-only <c>ConvertStringTo…</c> helpers that back the option-pane
    /// attributes stripped above: a single expression-bodied static returning a switch
    /// expression. With the attributes gone nothing calls them.</summary>
    [GeneratedRegex(@"public\s+static\s+[\w?<>]+\s+ConvertString\w+\s*\([^)]*\)\s*=>\s*\w+\s+switch\s*\{[^}]*\}\s*;", RegexOptions.Singleline)]
    private static partial Regex OptionConverterHelperRegex();

    /// <summary>Three or more consecutive newlines, left behind by the removals above.</summary>
    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLineRunRegex();

    /// <summary>A namespace belonging to the Toolkit's own sample app rather than to a
    /// shipping package: every component's sample project is named <c>&lt;Component&gt;Experiment</c>
    /// and declares its samples under <c>.Samples</c>.</summary>
    /// <remarks>
    /// <para>Deliberately narrower than the Gallery equivalent, which treats every Gallery
    /// namespace as private. <c>using:CommunityToolkit.WinUI.Controls</c> is the real shipping
    /// namespace of the NuGet package the user is told to install, so only the sample app's own
    /// namespaces are rewritten here. This mirrors the pattern
    /// <c>EmbeddedSnapshotTests.ToolkitCorpus_ServesNoSampleAppSymbols</c> rejects, so the two
    /// cannot drift into disagreeing about what "private" means.</para>
    /// </remarks>
    [GeneratedRegex(@"using:[\w.]*(?:Experiment|Samples)[\w.]*")]
    private static partial Regex ToolkitSampleAppXmlnsRegex();

    /// <summary>A package-relative path into the Toolkit sample app's own <c>Assets</c> folder,
    /// so a pasted sample naming one resolves to nothing in the user's app. Group 1 captures the
    /// prefix so its form is preserved verbatim; group 2 is everything after it, which may name a
    /// subfolder (<c>BrushAssets/</c>).</summary>
    /// <remarks>Deliberately broader than <see cref="GalleryProvider"/>'s equivalent, which scopes
    /// itself to the two subfolders Gallery keeps its media in. The Toolkit sample app puts its
    /// files directly under <c>Assets/</c>, so there is no subfolder to key on and every match is
    /// one of its own. Braces are excluded from the path because these occur inside XAML markup
    /// extensions (<c>"{ui:BitmapIcon Source=ms-appx:///Assets/AppTitleBar.scale-200.png}"</c>) —
    /// swallowing the terminator would rewrite the extension to <c>.png}</c> and leave markup that
    /// no longer parses.</remarks>
    [GeneratedRegex(@"((?:ms-appx:///)?/?Assets/)([^""'\s<>){}]+)")]
    private static partial Regex ToolkitAssetRegex();

    /// <summary>Rewrite every Toolkit-only asset path to an obvious placeholder. The result is
    /// equally absent from the user's app — that is the point: it fails visibly, where the
    /// upstream path fails as a blank control with nothing to explain it.</summary>
    private static string ReplaceToolkitAssets(string content) =>
        ToolkitAssetRegex().Replace(content, static m =>
            ControlSnippetText.AssetPlaceholder(m.Groups[1].Value, m.Groups[2].Value));

    /// <summary>
    /// Make one Toolkit scenario pasteable.
    ///
    /// <para>The index already publishes each sample as its own members, without the license
    /// header, the Toolkit namespace, the <c>[ToolkitSample…]</c> attributes its docs
    /// generator consumes, the <c>ConvertStringTo…</c> helpers that back them, or the
    /// <c>#if</c> branches for UWP and Uno. What is left to settle here is what only a
    /// consumer can know: the name the user's own page will have, which namespaces belong to
    /// the Toolkit's sample app rather than to a package the user can install, and whether
    /// the markup still refers to event handlers the code we serve does not declare.</para>
    ///
    /// <para>The removals upstream now performs are still attempted, and are expected to
    /// find nothing. They cost one failed match each on text that is already clean, and they
    /// are what keeps a change on the other side of the index from reaching a user's
    /// clipboard before anyone notices. The <c>#if</c> folding that preceded the index is
    /// <em>not</em> kept on that basis: it was a line-by-line parser rather than one match,
    /// and the corpus guards already fail on a directive that survives.</para>
    ///
    /// <para>What is rewritten here is environment, never substance: identifiers, build-time
    /// metadata, and namespaces that name the Toolkit's own sample project. The sample still
    /// teaches exactly what upstream wrote it to teach.</para>
    ///
    /// <para><see cref="GalleryProvider.NormalizeForPaste"/> is the same job for Gallery.</para>
    ///
    /// <para>Internal for direct testing: the corpus guards in <c>EmbeddedSnapshotTests</c>
    /// only prove the baked data is clean, which passes vacuously if upstream stops shipping
    /// the thing being rewritten.</para>
    /// </summary>
    internal static void NormalizeForPaste(Scenario scenario)
    {
        if (!string.IsNullOrEmpty(scenario.CSharp))
        {
            var csharp = scenario.CSharp;

            // Captured before anything is removed, so folding a branch or stripping an
            // attribute can't take the declaration this name is read from with it.
            var sampleClass = SampleClassRegex().Match(csharp) is { Success: true } m
                ? m.Groups[1].Value
                : SampleConstructorRegex().Match(csharp) is { Success: true } c
                    ? c.Groups[1].Value
                    : null;

            csharp = LicenseBlockRegex().Replace(csharp, "");
            csharp = LicenseFoundationRegex().Replace(csharp, "");
            csharp = LicenseSeeAlsoRegex().Replace(csharp, "");

            csharp = ToolkitSampleAttributeRegex().Replace(csharp, "");
            csharp = SuppressMessageAttributeRegex().Replace(csharp, "");
            csharp = ToolkitNamespaceRegex().Replace(csharp, "namespace YourApp;\n\n");

            if (!string.IsNullOrEmpty(sampleClass))
            {
                csharp = Regex.Replace(csharp, $@"\b{Regex.Escape(sampleClass)}\b", "YourPage");
            }

            csharp = OptionConverterHelperRegex().Replace(csharp, "");
            csharp = ReplaceToolkitAssets(csharp);
            csharp = BlankLineRunRegex().Replace(csharp, "\n\n");

            scenario.CSharp = string.IsNullOrWhiteSpace(csharp) ? null : csharp.Trim();
        }

        if (!string.IsNullOrEmpty(scenario.Xaml))
        {
            var xaml = ToolkitSampleAppXmlnsRegex().Replace(scenario.Xaml!, "using:YourApp");
            xaml = ReplaceToolkitAssets(xaml);

            // Strip handlers against the C# settled above, so an attribute survives only
            // when the code we actually serve declares its method. Scenarios split out of a
            // multi-instance sample carry no code-behind at all, so for those this drops
            // every bare-method handler — which is correct: none of them are backed.
            scenario.Xaml = ControlSnippetText.StripUnbackedEventHandlers(xaml, scenario.CSharp);
        }

        // Rendered as the "Setup:" line, so a sample-app namespace here is worse than one
        // buried in the markup: it reads as an instruction to add a mapping that cannot
        // resolve, pointing at a namespace no shipping package contains. The emitted C# is
        // already rewritten to `namespace YourApp;`, so the prefix now names the place the
        // user has to supply the type — which is where it was always going to have to live.
        for (var i = 0; i < scenario.XmlnsImports.Length; i++)
        {
            scenario.XmlnsImports[i] = ToolkitSampleAppXmlnsRegex().Replace(scenario.XmlnsImports[i], "using:YourApp");
        }
    }

}
