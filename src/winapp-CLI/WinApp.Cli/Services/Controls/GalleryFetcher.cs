// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static partial class GalleryFetcher
{
    private const string ControlInfoUrl =
        "https://raw.githubusercontent.com/microsoft/WinUI-Gallery/main/WinUIGallery/SampleSupport/Data/ControlInfoData.json";
    // Per-control sample pages AND their co-located SampleDefinition .txt bundles both live
    // under Samples/{UniqueId}/. Upstream retired Samples/ControlPages/ and Samples/SampleCode/.
    private const string SamplesBase =
        "https://raw.githubusercontent.com/microsoft/WinUI-Gallery/main/WinUIGallery/Samples/";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winui3-gallery-cli/1.0" } },
        Timeout = TimeSpan.FromSeconds(30)
    };

    [GeneratedRegex(@"<controls:ControlExample\b[^>]*?HeaderText=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ControlExampleHeaderRegex();

    /// <summary>First XML comment in a XAML snippet — used as a fallback header when
    /// the upstream ControlExample omits HeaderText (common for Accessibility samples).</summary>
    [GeneratedRegex(@"<!--\s*([\s\S]*?)\s*-->")]
    private static partial Regex FirstXmlCommentRegex();

    /// <summary>New-format SampleDefinition attribute pointing at the co-located .txt bundle
    /// (e.g. <c>SampleDefinition="Button\ButtonSimple.txt"</c>, relative to <c>Samples/</c>).</summary>
    [GeneratedRegex(@"SampleDefinition=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SampleDefinitionRegex();

    /// <summary>A section-marker line inside a SampleDefinition .txt bundle:
    /// <c>--- header</c>, <c>--- xaml</c>, or <c>--- c#</c> (case-insensitive, CRLF-tolerant).</summary>
    [GeneratedRegex(@"^\s*---\s*(header|xaml|c#)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SampleSectionRegex();

    [GeneratedRegex(@"\$\([^)]+\)")]
    private static partial Regex SubstitutionRegex();

    [GeneratedRegex(@"ms-appx:///Assets/SampleMedia/[^""'\s]+")]
    private static partial Regex SampleMediaRegex();

    [GeneratedRegex(@"x:Class=""WinUIGallery\.[^""]+""")]
    private static partial Regex GalleryClassRegex();

    [GeneratedRegex(@"typeof\(SamplePage\d+\)")]
    private static partial Regex SamplePageTypeRegex();

    [GeneratedRegex(@"SamplePage\d+")]
    private static partial Regex SamplePageNameRegex();

    /// <summary>Fetch fresh scenarios + tags from GitHub.</summary>
    /// <remarks>
    /// Which samples exist, and what each one demonstrates, is upstream's to decide. winapp
    /// adds no scenarios of its own to this corpus and applies no per-sample rewrites of
    /// upstream's implementation, because everything here is presented to users as Gallery's
    /// (<c>Scenario.Source</c> becomes the <c>[gallery]</c> tag and the <c>gallery-</c> id
    /// prefix in <c>find-ui</c> output). Content winapp considers missing or misleading
    /// belongs upstream in WinUI-Gallery, or in <see cref="Notes"/> as attributed guidance —
    /// not silently merged into their data under their name. See #703.
    ///
    /// This is not a claim that snippets are byte-identical to upstream's files. Extraction
    /// is uniform and mechanical, and deliberately lossy: content is cleaned
    /// (<see cref="CleanGalleryContent"/>), C# is compressed, both languages are truncated,
    /// and event handlers with no emitted code-behind are stripped so the snippet compiles
    /// on paste. Those transforms apply to every sample by the same rule; what is gone is
    /// the bespoke, sample-specific editorializing.
    /// </remarks>
    internal static async Task<(Scenario[] scenarios, Dictionary<string, string[]> tags)> FetchAsync(CancellationToken cancellationToken = default)
        => await FetchFromGitHub(cancellationToken);

    private static async Task<(Scenario[], Dictionary<string, string[]>)> FetchFromGitHub(CancellationToken cancellationToken)
    {
        // Step 1: Fetch ControlInfoData.json — list of controls + Subtitle/Description/RelatedControls/Docs
        var infoJson = await ControlsHttpHelper.GetStringCappedAsync(Http, ControlInfoUrl, cancellationToken);
        using var doc = JsonDocument.Parse(infoJson);
        var groups = doc.RootElement.GetProperty("Groups");

        var controlPages = new List<(string uniqueId, string title)>();
        var subtitles = new Dictionary<string, string>();        // controlId → "Title Subtitle Description" (tag-source text)
        var controlSubtitles = new Dictionary<string, string>(); // controlId → Subtitle alone (display-friendly one-liner)
        var apiNamespaces = new Dictionary<string, string>();    // controlId → "Microsoft.Windows.Notifications" etc.
        var relatedControls = new Dictionary<string, string[]>(); // controlId → ["Pivot","NavigationView",...]
        var docs = new Dictionary<string, DocLink[]>();           // controlId → [{Title,Uri},...]

        foreach (var group in groups.EnumerateArray())
        {
            // Upstream retired the per-group `Folder` subpath (IsSpecialSection items used to
            // live under Samples/{Folder}/). Every control page is now uniformly at
            // Samples/{UniqueId}/{UniqueId}Page.xaml, so we no longer read Folder.
            if (!group.TryGetProperty("Items", out var items)) continue;
            foreach (var item in items.EnumerateArray())
            {
                var uniqueId = item.GetProperty("UniqueId").GetString() ?? "";
                var title = item.GetProperty("Title").GetString() ?? "";
                controlPages.Add((uniqueId, title));

                var cid = uniqueId.ToLowerInvariant();
                string subtitle = "";
                if (item.TryGetProperty("Subtitle", out var sub)) subtitle = sub.GetString() ?? "";
                string description = "";
                if (item.TryGetProperty("Description", out var desc)) description = desc.GetString() ?? "";
                subtitles[cid] = $"{title} {subtitle} {description}".Trim();
                if (!string.IsNullOrWhiteSpace(subtitle)) controlSubtitles[cid] = subtitle;

                if (item.TryGetProperty("ApiNamespace", out var apiNs))
                {
                    var ns = apiNs.GetString();
                    if (!string.IsNullOrWhiteSpace(ns)) apiNamespaces[cid] = ns!;
                }

                if (item.TryGetProperty("RelatedControls", out var rel) && rel.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var r in rel.EnumerateArray())
                    {
                        var v = r.GetString();
                        if (!string.IsNullOrEmpty(v)) list.Add(v!);
                    }
                    if (list.Count > 0) relatedControls[cid] = list.ToArray();
                }

                if (item.TryGetProperty("Docs", out var dList) && dList.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<DocLink>();
                    foreach (var d in dList.EnumerateArray())
                    {
                        var t = d.TryGetProperty("Title", out var tProp) ? (tProp.GetString() ?? "") : "";
                        var u = d.TryGetProperty("Uri",   out var uProp) ? (uProp.GetString() ?? "") : "";
                        if (!string.IsNullOrEmpty(u)) list.Add(new DocLink { Title = t, Uri = u });
                    }
                    if (list.Count > 0) docs[cid] = list.ToArray();
                }
            }
        }

        // Step 2: Fetch each control page and parse ControlExample blocks
        var allScenarios = new List<Scenario>();
        var fetchTasks = new List<Task<List<Scenario>>>();
        using var semaphore = new SemaphoreSlim(10);
        foreach (var (uniqueId, title) in controlPages)
        {
            fetchTasks.Add(FetchControlPageAsync(
                uniqueId, title, controlSubtitles.GetValueOrDefault(uniqueId.ToLowerInvariant()), semaphore, cancellationToken));
        }
        var results = await Task.WhenAll(fetchTasks);
        foreach (var batch in results)
            allScenarios.AddRange(batch);

        // Stamp ControlInfoData metadata onto every scenario of that control.
        // ControlDescription comes from Subtitle (median 68 chars / max 129) — the longer
        // Description (median 144 / max 448) was retired during search-output compression.
        // Subtitle is small enough to surface in search list without bloating tokens.
        foreach (var s in allScenarios)
        {
            if (controlSubtitles.TryGetValue(s.ControlId, out var sub)) s.ControlDescription = sub;
            if (apiNamespaces.TryGetValue(s.ControlId, out var ns)) s.ApiNamespace = ns;
            if (relatedControls.TryGetValue(s.ControlId, out var r)) s.RelatedControls = r;
            if (docs.TryGetValue(s.ControlId, out var dl))           s.Docs = dl;
        }

        // Step 3: Build tags. For each control, prefer hand-curated embedded tags;
        // otherwise auto-derive from Title + Subtitle. Always strip stop words.
        var embeddedTags = DataLoader.LoadGalleryTags();
        var allTags = new Dictionary<string, string[]>();
        foreach (var (controlId, text) in subtitles)
        {
            if (embeddedTags.TryGetValue(controlId, out var manual))
            {
                allTags[controlId] = FilterStopWords(manual);
            }
            else
            {
                allTags[controlId] = ExtractTagsFromText(controlId, text);
            }
        }
        // Also include any embedded tags for controls not in ControlInfoData (jumplist-* etc.)
        foreach (var (k, v) in embeddedTags)
        {
            if (!allTags.ContainsKey(k)) allTags[k] = FilterStopWords(v);
        }

        return (allScenarios.ToArray(), allTags);
    }

    private static string[] FilterStopWords(string[] tags)
    {
        return StopWords.FilterTagList(tags);
    }

    private static string[] ExtractTagsFromText(string controlId, string text)
    {
        var tags = new List<string> { controlId };
        var seen = new HashSet<string> { controlId };
        // Split CamelCase/PascalCase into separate words too
        text = Regex.Replace(text, @"(?<=[a-z])(?=[A-Z])", " ");
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z]{3,}"))
        {
            var w = m.Value;
            if (StopWords.IsTagNoise(w)) continue;
            if (seen.Add(w)) tags.Add(w);
            if (tags.Count >= 12) break;
        }
        return tags.ToArray();
    }

    private static Dictionary<string, string[]> CleanTags(Dictionary<string, string[]> tags)
    {
        var result = new Dictionary<string, string[]>(tags.Count);
        foreach (var (k, v) in tags) result[k] = FilterStopWords(v);
        return result;
    }

    private static async Task<List<Scenario>> FetchControlPageAsync(
        string uniqueId, string title, string? controlSubtitle, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        var scenarios = new List<Scenario>();
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            // Pages are uniform: Samples/{UniqueId}/{UniqueId}Page.xaml.
            var url = $"{SamplesBase}{uniqueId}/{uniqueId}Page.xaml";

            var xamlContent = await ControlsHttpHelper.TryGetStringCappedAsync(Http, url, cancellationToken);
            if (xamlContent is null) return scenarios;

            var controlId = uniqueId.ToLowerInvariant();
            int scenarioIndex = 0;

            foreach (var (rawHeader, sampleDef, block) in ExtractControlExampleBlocks(xamlContent))
            {
                string headerText;
                string? xaml;
                string? csharp;

                if (!string.IsNullOrEmpty(sampleDef))
                {
                    // New format: header + xaml + c# all live in the co-located .txt bundle.
                    (headerText, xaml, csharp) = await FetchSampleDefinition(sampleDef, cancellationToken);
                }
                else
                {
                    // Legacy inline format — still used by a handful of Accessibility pages
                    // (AccessibilityKeyboard/ScreenReader): <ControlExample.Xaml/.CSharp> blocks
                    // with no SampleDefinition/HeaderText. Header falls back to the sample's own
                    // first XML comment — never the page-level copyright banner, which lives
                    // outside every ControlExample block and so is never inside `block`/`xaml`.
                    xaml = ExtractInlineCode(block, "Xaml");
                    csharp = ExtractInlineCode(block, "CSharp");
                    headerText = rawHeader;
                    if (string.IsNullOrEmpty(headerText) && xaml != null)
                        headerText = DeriveHeaderFromComment(xaml);
                    // A few legacy inline a11y samples have no leading comment either, which
                    // would leave HeaderText empty and render as "{ControlName}: " (trailing
                    // colon). Fall back to the control's ControlInfoData Subtitle one-liner.
                    // New-format samples never reach here — they always carry a --- header.
                    if (string.IsNullOrEmpty(headerText) && !string.IsNullOrWhiteSpace(controlSubtitle))
                        headerText = controlSubtitle!.Trim();
                }

                if (xaml != null) xaml = ControlSnippetText.TruncateXaml(xaml, MaxXamlChars);
                if (csharp != null) csharp = ControlSnippetText.TruncateCode(csharp, MaxCSharpChars, "// NOTE: snippet truncated — refer to full sample for additional code");

                // Gallery keeps each sample's XAML in its .txt bundle but its event
                // handlers in the shared page code-behind we don't fetch (see #703/#704).
                // Strip any handler the emitted C# doesn't define so the snippet compiles
                // as pasted; handlers that ARE backed (e.g. TabView's) are kept.
                if (xaml != null) xaml = ControlSnippetText.StripUnbackedEventHandlers(xaml, csharp);

                if (string.IsNullOrWhiteSpace(xaml)) xaml = null;
                if (string.IsNullOrWhiteSpace(csharp)) csharp = null;
                if (csharp == null && xaml == null) continue;

                scenarioIndex++;
                // Scenario IDs use a simple {controlId}-{N} format (1-indexed). Stable
                // within a single fetched cache; rebuilt fresh on each cache refresh.
                var scenarioId = $"{controlId}-{scenarioIndex}";

                scenarios.Add(new Scenario
                {
                    Id = scenarioId,
                    ControlId = controlId,
                    ControlName = title,
                    HeaderText = headerText,
                    Xaml = xaml,
                    CSharp = csharp,
                    Source = "gallery",
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* skip this control */ }
        finally { semaphore.Release(); }

        return scenarios;
    }

    /// <summary>
    /// Fetch and parse a new-format SampleDefinition .txt bundle. Splits it into the
    /// "--- header" / "--- xaml" / "--- c#" sections and returns cleaned xaml/c# ready for
    /// truncation. XAML keeps the existing $(...) → "..." flattening (stray placeholders there
    /// are cosmetic). A c# section containing $(...) live-substitution tokens is dropped, because
    /// flattening them yields non-compileable code (e.g. `new Vector3(..., ..., ...)`) — the same
    /// "no misleading C#" rule the inline extractor already applied.
    /// </summary>
    private static async Task<(string header, string? xaml, string? csharp)> FetchSampleDefinition(string sampleDef, CancellationToken cancellationToken)
    {
        var url = SamplesBase + sampleDef.Replace('\\', '/');
        var content = await ControlsHttpHelper.TryGetStringCappedAsync(Http, url, cancellationToken);
        if (content is null) return ("", null, null);

        var (header, rawXaml, rawCsharp) = SplitSampleSections(content);

        string? xaml = null;
        if (!string.IsNullOrWhiteSpace(rawXaml))
        {
            xaml = CleanGalleryContent(rawXaml.Trim());
            if (string.IsNullOrWhiteSpace(xaml)) xaml = null;
        }

        string? csharp = null;
        if (!string.IsNullOrWhiteSpace(rawCsharp) && !rawCsharp.Contains("$("))
        {
            csharp = CompressCSharp(CleanGalleryContent(rawCsharp.Trim()));
            if (string.IsNullOrWhiteSpace(csharp)) csharp = null;
        }

        return (header, xaml, csharp);
    }

    /// <summary>Split a SampleDefinition .txt bundle into its header/xaml/c# sections on the
    /// "--- name" marker lines. Any content before the first marker is ignored.</summary>
    private static (string header, string? xaml, string? csharp) SplitSampleSections(string content)
    {
        string header = "";
        string? xaml = null, csharp = null;
        string? current = null;
        var sb = new System.Text.StringBuilder();

        void Flush()
        {
            if (current == null) return;
            var text = sb.ToString().Trim('\r', '\n');
            switch (current)
            {
                case "header": header = text.Trim(); break;
                case "xaml": xaml = text; break;
                case "c#": csharp = text; break;
            }
            sb.Clear();
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = SampleSectionRegex().Match(line);
            if (m.Success)
            {
                Flush();
                current = m.Groups[1].Value.ToLowerInvariant();
                continue;
            }
            if (current != null) sb.Append(rawLine).Append('\n');
        }
        Flush();
        return (header, xaml, csharp);
    }

    private const int MaxXamlChars = 2000;
    private const int MaxCSharpChars = 2500;

    /// <summary>
    /// Find all top-level &lt;controls:ControlExample&gt; blocks via stack-aware tag matching,
    /// handling nested ScrollViewer/StackPanel inside ControlExample.Example.
    /// </summary>
    private static IEnumerable<(string headerText, string sampleDefinition, string block)> ExtractControlExampleBlocks(string xaml)
    {
        const string OpenTag = "<controls:ControlExample";
        const string CloseTag = "</controls:ControlExample>";
        int searchStart = 0;
        while (true)
        {
            int openIdx = xaml.IndexOf(OpenTag, searchStart, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0) yield break;

            // Make sure this isn't ControlExample.Xaml or ControlExample.Example etc.
            int afterPrefix = openIdx + OpenTag.Length;
            if (afterPrefix < xaml.Length && (xaml[afterPrefix] == '.' || char.IsLetterOrDigit(xaml[afterPrefix])))
            {
                searchStart = openIdx + OpenTag.Length;
                continue;
            }

            // Find end of opening tag '>'
            int openTagEnd = xaml.IndexOf('>', afterPrefix);
            if (openTagEnd < 0) yield break;

            // Self-closing? Skip.
            if (xaml[openTagEnd - 1] == '/')
            {
                searchStart = openTagEnd + 1;
                continue;
            }

            // Extract header text (legacy) + SampleDefinition (new format) from opening-tag attributes
            string openingTag = xaml.Substring(openIdx, openTagEnd - openIdx + 1);
            var headerMatch = ControlExampleHeaderRegex().Match(openingTag);
            string headerText = headerMatch.Success ? headerMatch.Groups[1].Value : "";
            var defMatch = SampleDefinitionRegex().Match(openingTag);
            string sampleDefinition = defMatch.Success ? defMatch.Groups[1].Value : "";

            // Walk forward, balancing <controls:ControlExample> tags
            int depth = 1;
            int pos = openTagEnd + 1;
            int blockEnd = -1;
            while (pos < xaml.Length)
            {
                int nextOpen = xaml.IndexOf(OpenTag, pos, StringComparison.OrdinalIgnoreCase);
                int nextClose = xaml.IndexOf(CloseTag, pos, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0) break;
                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    int after = nextOpen + OpenTag.Length;
                    // Skip ControlExample.Xaml/.CSharp/.Example sub-properties (they don't open a new block)
                    if (after < xaml.Length && xaml[after] != '.' && !char.IsLetterOrDigit(xaml[after]))
                    {
                        int gt = xaml.IndexOf('>', after);
                        if (gt > 0 && xaml[gt - 1] != '/') depth++;
                        pos = gt > 0 ? gt + 1 : nextOpen + OpenTag.Length;
                        continue;
                    }
                    pos = after;
                }
                else
                {
                    depth--;
                    if (depth == 0)
                    {
                        blockEnd = nextClose + CloseTag.Length;
                        break;
                    }
                    pos = nextClose + CloseTag.Length;
                }
            }

            if (blockEnd < 0) yield break;
            yield return (headerText, sampleDefinition, xaml.Substring(openIdx, blockEnd - openIdx));
            searchStart = blockEnd;
        }
    }

    /// <summary>Pull a usable header from the first XML comment in a sample's XAML.
    /// Collapses whitespace, strips trailing punctuation, truncates at sentence boundary.
    /// Returns "" when no comment is found or the comment is too short to be a real header.</summary>
    private static string DeriveHeaderFromComment(string xaml)
    {
        var m = FirstXmlCommentRegex().Match(xaml);
        if (!m.Success) return "";

        var raw = m.Groups[1].Value;
        // Collapse all whitespace runs (newlines, tabs, multi-space) to single space.
        var collapsed = Regex.Replace(raw, @"\s+", " ").Trim();
        if (collapsed.Length < 4) return "";

        // Strip stray decoration sometimes used as section dividers (****, ====, ----)
        collapsed = collapsed.Trim('*', '=', '-', ' ');
        if (collapsed.Length < 4) return "";

        // Prefer the first sentence so very long explanatory comments stay short.
        const int MaxHeaderLen = 120;
        int firstStop = collapsed.IndexOfAny(new[] { '.', '!', '?', '\n' });
        if (firstStop > 0 && firstStop < MaxHeaderLen) collapsed = collapsed.Substring(0, firstStop);
        if (collapsed.Length > MaxHeaderLen) collapsed = collapsed.Substring(0, MaxHeaderLen).TrimEnd() + "…";

        return collapsed.Trim();
    }

    private static string? ExtractInlineCode(string block, string tagName)
    {
        var pattern = $@"<controls:ControlExample\.{tagName}>\s*<x:String[^>]*>([\s\S]*?)</x:String>\s*</controls:ControlExample\.{tagName}>";
        var match = Regex.Match(block, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var code = UnescapeXml(match.Groups[1].Value).Trim();
        if (code.Contains("$("))
        {
            // C# inline templates with $(VarName) substitutions are bound to live UI
            // controls. Replacing with "..." produces literals like `Title = "..."`
            // and `Resize(new SizeInt32(..., ...))` that mislead agents into compiling
            // them. The code-behind extractor is the right path for C#; if it failed
            // (no .xaml.cs or no event/x:Bind/x:Name seeds), surface no csharp at all.
            if (tagName == "CSharp") return null;
            // For XAML, the placeholder substitution is generally cosmetic (color, size)
            // and the surrounding markup is still useful — keep the existing behavior.
            code = SubstitutionRegex().Replace(code, "...");
        }
        code = CleanGalleryContent(code);
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    /// <summary>Strip Gallery-internal namespaces/types and demo-only layout attributes so the
    /// snippet pastes into a user's app. Internal for direct testing, mirroring
    /// <see cref="ToolkitFetcher.CleanXaml"/>.</summary>
    internal static string CleanGalleryContent(string code)
    {
        code = SampleMediaRegex().Replace(code, "ms-appx:///Assets/YourImage.png");
        code = GalleryClassRegex().Replace(code, @"x:Class=""YourApp.YourPage""");
        code = SamplePageTypeRegex().Replace(code, "typeof(YourPage)");
        code = SamplePageNameRegex().Replace(code, "YourPage");
        code = Regex.Replace(code, @"using WinUIGallery[^;\n]*;?", "// adapt namespace to your app");
        code = Regex.Replace(code, @"using AppUIBasics[^;\n]*;?", "// adapt namespace to your app");
        code = Regex.Replace(code, @"namespace WinUIGallery[^{;\n]*;?", "namespace YourApp;");
        code = Regex.Replace(code, @"namespace AppUIBasics[^{;\n]*;?", "namespace YourApp;");
        code = Regex.Replace(code, @".*NavigationHelper.*\n?", "");

        // Demo `Loaded="X_Loaded"` handlers must go before the line filter below: unlike
        // the fixed sizes and negative margins, this one usually sits INLINE inside a tag
        // ("<TabView TabCloseRequested=... Loaded="TabView_Loaded" />"), where the filter's
        // markup guard would keep it. The Gallery's code-behind extractor never surfaces
        // the *_Loaded method, so leaving the attribute emits XAML referencing a handler
        // the snippet doesn't define — which fails to compile when pasted. Consuming the
        // leading whitespace also removes the line break when it does sit on its own line.
        code = Regex.Replace(code, @"\s+Loaded=""[^""]*_Loaded""", "");

        // Clean demo-specific layout attributes (fixed sizes, negative margins).
        // Only drop an attribute that sits on its OWN continuation line (no '<' or '>'), so we
        // never delete a line that also carries the tag's closing '>' or other markup — which
        // would corrupt multi-line open tags in the new SampleDefinition format
        // (e.g. `Width="300" Margin="12" Height="68">`).
        var lines = code.Split('\n').Where(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.IndexOf('<') >= 0 || trimmed.IndexOf('>') >= 0) return true;
            if (Regex.IsMatch(trimmed, @"^(Min|Max)?(Height|Width)=""\d")) return false;
            if (Regex.IsMatch(trimmed, @"^Margin=""-")) return false;
            if (Regex.IsMatch(trimmed, @"^SelectedIndex=""\d+""")) return false;
            return true;
        });
        code = string.Join('\n', lines);

        // Clean substitution placeholders: replace known $(...) or "..." with defaults.
        // Tokens in attribute and element-content position are normalized first — the
        // generic flattening below produces invalid markup for both, see
        // NormalizeMarkupSubstitutions.
        code = NormalizeMarkupSubstitutions(code);
        code = Regex.Replace(code, @"IsOpen=""(\$\(IsOpen\)|\.\.\.?)""", @"IsOpen=""True""");
        code = Regex.Replace(code, @"Severity=""(\$\(Severity\)|\.\.\.?)""", @"Severity=""Informational""");
        code = SubstitutionRegex().Replace(code, "...");

        code = Regex.Replace(code, @"\n\s*\n\s*\n", "\n\n");
        return code.Trim();
    }

    /// <summary>
    /// Normalizes the <c>$(Name)</c> substitution tokens the Gallery expands at runtime, by the
    /// position they occupy. Both cases below break if they are flattened to <c>"..."</c> the way
    /// a value-position token is:
    /// <list type="bullet">
    /// <item><description><b>Attribute position</b> — a bare token in an element's attribute list,
    /// e.g. <c>&lt;Button Content="Go" Click="Button_Click" $(IsEnabled)/&gt;</c>. It stands in for
    /// a whole attribute, so it is removed. Flattening yields <c>Click="Button_Click" .../&gt;</c>,
    /// which is not well-formed, so <see cref="ScenarioSanitizer.XamlIsWellFormed"/> discards the
    /// snippet and the control serves a fetchable result with no code in it at all. That hit
    /// Button, ToggleButton, RepeatButton, HyperlinkButton, ProgressRing, CommandBar, AnimatedIcon,
    /// PersonPicture and EasingFunction.</description></item>
    /// <item><description><b>Element content</b> — a token between elements, e.g.
    /// <c>&lt;/AppBarButton&gt;$(MultipleButtonsSecondaryCommands)</c>. Every one upstream ships
    /// injects markup (extra items, a Layout, a DataTemplate) into a property element, never text,
    /// so it becomes a comment. Flattening stays well-formed but assigns the literal string "..."
    /// as a collection's content, which does not compile when pasted.</description></item>
    /// </list>
    /// A token inside an attribute value (<c>Value="$(DeterminateProgressValue)"</c>) is left for
    /// the caller's generic flattening: that one is cosmetic and stays valid.
    /// </summary>
    internal static string NormalizeMarkupSubstitutions(string code)
    {
        if (code.IndexOf("$(", StringComparison.Ordinal) < 0) return code;

        var sb = new StringBuilder(code.Length);
        var inTag = false;
        var quote = '\0';

        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                sb.Append(c);
                continue;
            }

            // Copy comments verbatim: a '>' inside one would otherwise desynchronize the
            // in-tag state and misclassify every token that follows.
            if (c == '<' && string.CompareOrdinal(code, i, "<!--", 0, 4) == 0)
            {
                var end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) end = code.Length - 3;
                sb.Append(code, i, end + 3 - i);
                i = end + 2;
                continue;
            }

            if (inTag && (c == '"' || c == '\'')) { quote = c; sb.Append(c); continue; }
            if (c == '<') { inTag = true; sb.Append(c); continue; }
            if (c == '>') { inTag = false; sb.Append(c); continue; }

            if (c == '$' && i + 1 < code.Length && code[i + 1] == '(')
            {
                var close = code.IndexOf(')', i + 2);
                if (close > 0)
                {
                    i = close;
                    if (inTag)
                    {
                        // Drop the whitespace that separated the token from the previous
                        // attribute too, so the tag doesn't keep a dangling gap before "/>".
                        while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
                    }
                    else
                    {
                        sb.Append("<!-- ... -->");
                    }

                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string UnescapeXml(string s)
    {
        return s.Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&#10;", "\n")
                .Replace("&#13;", "\r");
    }

    /// <summary>Tighten whitespace, drop license header + Gallery-internal helper lines.</summary>
    private static string CompressCSharp(string code)
    {
        // Strip Gallery's UIHelper.* accessibility helper calls (not portable, agent doesn't have it).
        code = Regex.Replace(code, @"^\s*UIHelper\.[^;]+;.*\n?", "", RegexOptions.Multiline);

        // Drop a leading "// C# code-behind" / "// C# Code" label line that some upstream
        // SampleDefinition bundles put at the top of their --- c# section (pure noise; the
        // real class/method code follows). Only the leading marker is removed, not inline
        // explanatory comments that document the sample.
        code = Regex.Replace(code, @"^\s*//\s*C#\s*(code-behind|code)\s*\r?\n", "", RegexOptions.IgnoreCase);

        // Strip #region / #endregion preprocessor directives — they're noise and
        // can produce CS1038 errors when truncation cuts off the matching half.
        code = Regex.Replace(code, @"^\s*#(?:region|endregion)\b.*\n?", "", RegexOptions.Multiline);

        // Drop "// Copyright (c) Microsoft..." + "// Licensed under..." header pair.
        if (code.StartsWith("// Copyright"))
        {
            int nl1 = code.IndexOf('\n');
            if (nl1 > 0)
            {
                int nl2 = code.IndexOf('\n', nl1 + 1);
                if (nl2 > 0 && code.Substring(nl1 + 1, nl2 - nl1 - 1).TrimStart().StartsWith("// Licensed"))
                    code = code.Substring(nl2 + 1).TrimStart();
            }
        }

        // De-indent: turn 4-space indents into 2-space (saves ~12% per line).
        code = Regex.Replace(code, @"(?m)^( {4})+", m => new string(' ', m.Length / 2));

        // Collapse 3+ consecutive newlines into 2.
        code = Regex.Replace(code, @"\n[\t ]*\n[\t ]*\n+", "\n\n");
        return code.Trim();
    }
}

