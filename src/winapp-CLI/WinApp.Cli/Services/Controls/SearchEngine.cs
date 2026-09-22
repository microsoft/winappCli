// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal sealed class SearchEngine
{
    private readonly Scenario[] _scenarios;
    private readonly CorePattern[] _corePatterns;
    private readonly Dictionary<string, string[]> _enrichmentTags;
    private readonly Dictionary<string, string[]> _curatedKeywords;
    private readonly Dictionary<string, List<Scenario>> _scenariosByControl;
    private readonly string[] _uniqueControls;
    private readonly string[] _knownSources;

    public SearchEngine(
        Scenario[] scenarios,
        CorePattern[] corePatterns,
        Dictionary<string, string[]> enrichmentTags,
        Dictionary<string, string[]>? curatedKeywords = null)
    {
        _scenarios = scenarios;
        _corePatterns = corePatterns;
        _enrichmentTags = enrichmentTags;
        _curatedKeywords = curatedKeywords ?? new();

        _scenariosByControl = new();
        foreach (var s in scenarios)
        {
            // Use composite key (source + controlId) to keep Toolkit and Gallery
            // controls with the same controlId (e.g., "colorpicker") separate.
            var key = $"{s.Source}:{s.ControlId}";
            if (!_scenariosByControl.TryGetValue(key, out var list))
            {
                list = new List<Scenario>();
                _scenariosByControl[key] = list;
            }
            list.Add(s);
        }
        _uniqueControls = _scenariosByControl.Keys.ToArray();
        _knownSources = scenarios.Select(s => s.Source)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// True when this engine actually holds data for <paramref name="source"/> — i.e. the
    /// provider loaded at least one scenario, or (for the pseudo-source <c>"core"</c>) the
    /// embedded core patterns are present. Lets callers tell "this source loaded but has no
    /// match for the query" apart from "this source never loaded" (a failed/cold fetch that
    /// another warm source is masking), so a filtered <c>--source</c>/<c>--id</c> request
    /// against an unloaded source surfaces the friendly "connect and run once" error instead
    /// of a false "no match".
    /// </summary>
    public bool HasSource(string source)
    {
        if (string.Equals(source, "core", StringComparison.OrdinalIgnoreCase))
        {
            return _corePatterns.Length > 0;
        }
        return _knownSources.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    public record SearchResult(string Id, string Scenario, string Type, double Score);

    /// <summary>One entry inside a control group (a single scenario row).</summary>
    public record ScenarioRow(string Id, string Header, string? Description = null);

    /// <summary>
    /// Grouped search result. For core patterns, <c>Scenarios</c> holds one row.
    /// For gallery/toolkit controls, it holds up to <c>maxScenariosPerControl</c> rows
    /// — letting the caller decide which scenario fits best.
    /// </summary>
    public record GroupedResult(
        string Source,            // "core" / "gallery" / "toolkit"
        string ControlName,       // empty for core
        int TotalScenarios,
        List<ScenarioRow> Scenarios,
        double Score,
        string? ControlDescription = null,  // one-line concept summary (toolkit/gallery)
        bool HasNameBoost = false);         // matched via name/id/compound/substring (vs. only via headers/tags)

    // ─── Intent classification ───────────────────────────────────────────
    private static readonly string[] ToolkitIntent =
        ["toolkit", "communitytoolkit", "community"];

    /// <summary>
    /// Maps platform-intent query keywords → the core pattern id that should be boosted.
    /// Keys are lowercased single tokens that appear in the user's query (after Preprocess).
    /// We use this to nudge the *specific* curated pattern, not every core pattern.
    /// </summary>
    private static readonly Dictionary<string, string> PlatformKeywordToPatternId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["share"]        = "share-contract-desktop",
        ["jumplist"]     = "jumplist-recent-files",
        ["taskbar"]      = "jumplist-recent-files",
        ["tray"]         = "system-tray-minimize",
        ["systemtray"]   = "system-tray-minimize",
        ["drag"]         = "drag-drop-files",
        ["drop"]         = "drag-drop-files",
        ["dragdrop"]     = "drag-drop-files",
        ["picker"]       = "file-picker-desktop",
        ["filepicker"]   = "file-picker-desktop",
        ["folderpicker"] = "file-picker-desktop",
    };

    private static string CompactQuery(string q) =>
        System.Text.RegularExpressions.Regex.Replace(q.ToLowerInvariant(), @"[^a-z0-9]", "");

    /// <summary>
    /// Split a CamelCase or PascalCase identifier into space-separated words.
    /// "TokenizingTextBox" → "Tokenizing Text Box" (then BM25.Tokenize lowercases).
    /// "ColorPicker" → "Color Picker". Used to build a tokenized variant of control
    /// names for BM25 so multi-word queries can match the *parts* of a name, not
    /// only the name as a single opaque token.
    /// </summary>
    private static string SplitCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Two-layer search: find controls first, then pick best scenario.</summary>
    public List<SearchResult> Search(string query, int maxResults = 5)
    {
        // Phrase preprocessing: append merged tokens (e.g. "data grid" → keeps "data", "grid", adds "datagrid")
        var preprocessed = Synonyms.Preprocess(query);
        var queryWords = BM25.Tokenize(preprocessed);
        if (queryWords.Length == 0) return [];
        var queryCompact = CompactQuery(query);   // e.g., "Color Picker Button" → "colorpickerbutton"

        // Expand query with synonyms (datagrid → listview, modal → contentdialog, etc.)
        var expandedWords = Synonyms.Expand(queryWords);

        // Detect query intents
        bool wantsToolkit = queryWords.Any(w => ToolkitIntent.Contains(w));
        // Targeted platform-intent: only boost the SPECIFIC core pattern keyed by the query token.
        // (Avoids the old "any platform keyword boosts ALL core patterns" noise.)
        var platformBoostIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in queryWords)
        {
            if (PlatformKeywordToPatternId.TryGetValue(w, out var pid))
                platformBoostIds.Add(pid);
        }

        // Build docs for core patterns
        var coreDocs = _corePatterns.Select(p => BM25.BuildDoc(
            (string.Join(" ", p.Tags), 3.0),
            (p.Scenario, 2.0),
            (p.Description, 1.0),
            (p.Id, 1.0)
        )).ToArray();

        // Build docs for gallery controls
        var controlDocs = _uniqueControls.Select(key =>
        {
            var scenarios = _scenariosByControl[key];
            var controlId = scenarios[0].ControlId;
            var controlName = scenarios[0].ControlName;
            // `key` is already "{source}:{controlId}" — match the composite-key
            // dictionaries Program.cs builds so gallery↔toolkit collisions
            // (colorpicker, wrappanel) don't bleed tags/keywords across sources.
            var enrichTags = _enrichmentTags.TryGetValue(key, out var tags) ? tags : [];
            var keywords = _curatedKeywords.TryGetValue(key, out var kws) ? kws : [];
            var nameSplit = SplitCamelCase(controlName);
            return BM25.BuildDoc(
                (controlName, 3.0),
                (controlId, 3.0),
                (nameSplit, 2.5),
                (string.Join(" ", keywords), 5.0),
                (string.Join(" ", enrichTags), 3.0),
                (scenarios[0].HeaderText, 1.5)
            );
        }).ToArray();

        var allDocs = coreDocs.Concat(controlDocs).ToArray();
        var corpus = BM25.BuildCorpus(allDocs);

        var results = new List<SearchResult>();

        // Score core patterns + targeted platform-intent boost
        for (int i = 0; i < _corePatterns.Length; i++)
        {
            var s = BM25.Score(coreDocs[i], expandedWords, corpus);
            // Only boost the core pattern(s) the query actually points at.
            if (platformBoostIds.Contains(_corePatterns[i].Id)) s *= 1.6;
            if (s > 0) results.Add(new(_corePatterns[i].Id, _corePatterns[i].Scenario, "core", s));
        }

        // Find the LONGEST control name (compact form) that the query contains, so for
        // queries like "color picker button" we boost ColorPickerButton (17 chars) and
        // explicitly NOT ColorPicker (11 chars). Both technically match the compact form,
        // but the longer one is what the user actually asked for.
        string? longestCompactMatch = null;
        for (int i = 0; i < _uniqueControls.Length; i++)
        {
            var compactName = CompactQuery(_scenariosByControl[_uniqueControls[i]][0].ControlName);
            if (compactName.Length >= 8 && queryCompact.Contains(compactName))
            {
                if (longestCompactMatch == null || compactName.Length > longestCompactMatch.Length)
                    longestCompactMatch = compactName;
            }
        }

        // Score gallery/toolkit controls, then pick best scenario
        for (int i = 0; i < _uniqueControls.Length; i++)
        {
            var s = BM25.Score(controlDocs[i], expandedWords, corpus);
            var scenarios = _scenariosByControl[_uniqueControls[i]];
            var first = scenarios[0];
            var controlName = first.ControlName;
            var controlLower = controlName.ToLowerInvariant();
            var controlCompact = CompactQuery(controlName);

            // Whole-word match on control name (existing boost). Matched against the
            // synonym-expanded words, not just the typed ones — see SearchGrouped.
            if (controlLower.Length > 2 && expandedWords.Any(w => w == controlLower))
                s *= 2.0;

            // Compound-name match — but only the LONGEST match wins big.
            // Shorter prefixes that happen to be contained get a small boost; the
            // most-specific match gets the strong one.
            if (controlCompact.Length >= 8 && queryCompact.Contains(controlCompact))
            {
                if (controlCompact == longestCompactMatch) s *= 4.0;   // strongest: most specific
                else                                       s *= 1.3;   // mild: matched but not most specific
            }

            // Source intent boost
            if (wantsToolkit && first.Source == "toolkit") s *= 1.6;

            if (s <= 0) continue;

            var bestScenario = PickBestScenario(scenarios, expandedWords);
            // Demote scenarios with placeholder/generic header (Basic usage, Example N, "This is the Header")
            if (IsGenericHeader(bestScenario.HeaderText)) s *= 0.85;

            var prefix = $"{bestScenario.Source}-";
            results.Add(new($"{prefix}{bestScenario.Id}", ControlHeader(bestScenario.ControlName, bestScenario.HeaderText), bestScenario.Source, s));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results.Take(maxResults).ToList();
    }

    /// <summary>
    /// Grouped search: returns up to <paramref name="maxControls"/> matched controls,
    /// each with all its scenarios (capped at <paramref name="maxScenariosPerControl"/>)
    /// ordered by per-scenario relevance. Lets callers (and the AI) pick which
    /// scenario actually fits without the engine having to commit to one.
    /// </summary>
    public List<GroupedResult> SearchGrouped(
        string query, int maxControls = 3, int maxScenariosPerControl = 3,
        bool applyFloor = true, string? sourceFilter = null)
    {
        var preprocessed = Synonyms.Preprocess(query);
        var queryWords = BM25.Tokenize(preprocessed);
        if (queryWords.Length == 0) return [];
        var queryCompact = CompactQuery(query);
        var expandedWords = Synonyms.Expand(queryWords);

        // Coverage gate inputs: original query tokens (NOT preprocessed/expanded),
        // so synonyms inflating N to 7 don't cause false rejections.
        // Distinct so repeated tokens don't double-count.
        var rawQueryTokens = BM25.Tokenize(query).Distinct().ToArray();
        // Only enforce the gate for queries with >= 3 user tokens. With 1-2 tokens,
        // BM25's own ranking is already adequate and adding a half-coverage rule
        // would reject normal short queries like "tab close" → TabView (hits=1/2).
        bool applyCoverageGate = rawQueryTokens.Length >= 3;
        int coverageMin = (rawQueryTokens.Length + 1) / 2;  // ceil(N/2)

        bool wantsToolkit = queryWords.Any(w => ToolkitIntent.Contains(w));
        var platformBoostIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in queryWords)
        {
            if (PlatformKeywordToPatternId.TryGetValue(w, out var pid))
                platformBoostIds.Add(pid);
        }

        var coreDocs = _corePatterns.Select(p => BM25.BuildDoc(
            (string.Join(" ", p.Tags), 3.0),
            (p.Scenario, 2.0),
            (p.Description, 1.0),
            (p.Id, 1.0)
        )).ToArray();

        // Pre-collect enrichment tags per control so the scoring loop can re-use them
        // for the platform-keyword reverse-demotion check below. `key` is composite
        // ("{source}:{controlId}") to keep gallery + toolkit colliding controlIds
        // separate.
        var controlEnrichTags = _uniqueControls.Select(key =>
        {
            return _enrichmentTags.TryGetValue(key, out var tags) ? tags : Array.Empty<string>();
        }).ToArray();

        // Author-curated keywords (toolkit md frontmatter). Empty for gallery
        // controls; used both as a high-weight BM25 field AND added to the
        // tag substring check so a query word that appears only in keywords
        // still counts as a "real" match for the platform-keyword guard.
        var controlKeywords = _uniqueControls.Select(key =>
        {
            return _curatedKeywords.TryGetValue(key, out var kws) ? kws : Array.Empty<string>();
        }).ToArray();

        var controlDocs = _uniqueControls.Select((key, idx) =>
        {
            var scenarios = _scenariosByControl[key];
            var controlName = scenarios[0].ControlName;
            var controlId = scenarios[0].ControlId;
            var enrichTags = controlEnrichTags[idx];
            var keywords = controlKeywords[idx];
            // All scenario headers concatenated, controlName stripped to avoid
            // double-counting against the dedicated controlName field. No "primary"
            // bias: every scenario contributes equally; BM25's TF saturation handles repeats.
            string CleanHeader(string h)
            {
                var stripped = h.Replace(controlName, "", StringComparison.OrdinalIgnoreCase);
                return string.Join(" ", StopWords.FilterTagList(BM25.Tokenize(stripped)));
            }
            var allHeaders = string.Join(" ", scenarios.Select(s => CleanHeader(s.HeaderText)));
            // CamelCase-split control name: "TokenizingTextBox" → "tokenizing text box"
            // so a query like "tokenizing tag input" can match the *name* directly,
            // not only via tags. Without this, BM25 sees "tokenizingtextbox" as one
            // opaque token; "tokenizing" never matches and good answers tie with noise.
            var nameSplit = SplitCamelCase(controlName);
            return BM25.BuildDoc(
                (controlName,                  3.0),
                (controlId,                    3.0),
                (nameSplit,                    2.5),
                (string.Join(" ", keywords),   5.0),
                (string.Join(" ", enrichTags), 3.0),
                (allHeaders,                   0.8)
            );
        }).ToArray();

        var allDocs = coreDocs.Concat(controlDocs).ToArray();
        var corpus = BM25.BuildCorpus(allDocs);

        var groups = new List<GroupedResult>();

        // Core patterns — always one row each.
        for (int i = 0; i < _corePatterns.Length; i++)
        {
            var s = BM25.Score(coreDocs[i], expandedWords, corpus);
            if (platformBoostIds.Contains(_corePatterns[i].Id)) s *= 1.6;
            if (applyCoverageGate && BM25.CountHits(coreDocs[i], rawQueryTokens) < coverageMin) s = 0;
            if (s <= 0) continue;
            groups.Add(new GroupedResult(
                Source: "core",
                ControlName: "",
                TotalScenarios: 1,
                Scenarios: [new ScenarioRow(_corePatterns[i].Id, _corePatterns[i].Scenario)],
                Score: s));
        }

        // Find the longest compact control name contained in the query (same as Search).
        string? longestCompactMatch = null;
        for (int i = 0; i < _uniqueControls.Length; i++)
        {
            var compactName = CompactQuery(_scenariosByControl[_uniqueControls[i]][0].ControlName);
            if (compactName.Length >= 8 && queryCompact.Contains(compactName))
            {
                if (longestCompactMatch == null || compactName.Length > longestCompactMatch.Length)
                    longestCompactMatch = compactName;
            }
        }

        // Single-name auto-tighten:when the query is a single token that matches a
        // control name exactly (e.g. "combobox", "togglesswitch"), the user already
        // knows what they want — don't pad the result list with weak siblings.
        // Show ONE control with more of its scenarios instead.
        if (queryWords.Length == 1 && longestCompactMatch != null && longestCompactMatch == queryCompact)
        {
            maxControls = 1;
            maxScenariosPerControl = Math.Max(maxScenariosPerControl, 5);
        }

        // Platform-keyword reverse demotion targets: any control whose compact name
        // contains a platform-intent keyword in the query (e.g. "picker") but whose
        // name doesn't satisfy any *other* (non-keyword) query word should be demoted —
        // it's matching only on the platform noun, which already has a curated core
        // pattern that wins.
        var platformKeywordsInQuery = queryWords
            .Where(w => PlatformKeywordToPatternId.ContainsKey(w))
            .ToArray();
        var nonPlatformQueryWords = queryWords
            .Where(w => !PlatformKeywordToPatternId.ContainsKey(w))
            .ToArray();

        for (int i = 0; i < _uniqueControls.Length; i++)
        {
            var s = BM25.Score(controlDocs[i], expandedWords, corpus);
            var scenarios = _scenariosByControl[_uniqueControls[i]];
            var first = scenarios[0];
            var controlName = first.ControlName;
            var controlLower = controlName.ToLowerInvariant();
            var controlCompact = CompactQuery(controlName);
            var enrichTags = controlEnrichTags[i];
            var keywords = controlKeywords[i];

            // Substring boost: a long query word fully contained in the control's
            // compact name is strong evidence of intent ("tokenizing" inside
            // "tokenizingtextbox", "advanced" inside "advancedcollectionview").
            // Skip platform keywords — they have their own targeted boost on the
            // curated core pattern; substring-matching them on every *Picker /
            // *Tray control would re-amplify exactly the noise we just demoted.
            bool hasNameBoost = false;
            foreach (var qw in queryWords)
            {
                if (qw.Length >= 6
                    && !PlatformKeywordToPatternId.ContainsKey(qw)
                    && controlCompact.Contains(qw))
                {
                    s *= 2.5;
                    hasNameBoost = true;
                    break;  // one substring boost per control
                }
            }

            // Exact control-name match. Checked against the synonym-expanded words rather
            // than only the typed ones: the whole point of Synonyms.Map is that "snackbar",
            // "modal" or "swipeable" names a WinUI control as surely as typing "InfoBar",
            // "ContentDialog" or "SwipeControl" does, so those queries should earn the
            // coverage-gate bypass below too. Without this, an intent word that maps to a
            // control still loses to a control that merely shares the supporting nouns
            // ("swipeable list rows" → ListView, because SwipeControl's tags carry neither
            // "list" nor "rows"). Expansion only ever adds control names, so this widens
            // what counts as naming a control without loosening the gate itself.
            if (controlLower.Length > 2 && expandedWords.Any(w => w == controlLower))
            {
                s *= 2.0;
                hasNameBoost = true;
            }

            if (controlCompact.Length >= 8 && queryCompact.Contains(controlCompact))
            {
                if (controlCompact == longestCompactMatch) s *= 4.0;
                else                                       s *= 1.3;
                hasNameBoost = true;
            }

            if (wantsToolkit && first.Source == "toolkit") s *= 1.6;

            // Reverse demotion: control name contains a platform keyword from the query
            // (e.g. DatePicker matched on "picker"), but no non-keyword query word
            // independently matches the control name or its tags. The targeted core
            // pattern already covers this intent — we don't want to also surface every
            // *Picker control just because they share the noun.
            if (platformKeywordsInQuery.Length > 0
                && platformKeywordsInQuery.Any(kw => controlLower.Contains(kw)))
            {
                bool hasOtherMatch = nonPlatformQueryWords.Any(qw =>
                    controlLower.Contains(qw)
                    || enrichTags.Any(t => t.Contains(qw, StringComparison.OrdinalIgnoreCase))
                    || keywords.Any(k => k.Contains(qw, StringComparison.OrdinalIgnoreCase)));
                if (!hasOtherMatch) s *= 0.3;
            }

            // Coverage gate: when the user typed >=3 distinct words and this control
            // matched fewer than half of them, suppress it. Skipped when hasNameBoost
            // fires (the user named the control directly), since a single strong
            // intent signal can override missing supporting tokens.
            if (applyCoverageGate
                && !hasNameBoost
                && BM25.CountHits(controlDocs[i], rawQueryTokens) < coverageMin)
            {
                s = 0;
            }

            if (s <= 0) continue;

            // Score each scenario individually so we can present them in relevance order.
            var scenDocs = scenarios.Select(sc => BM25.BuildDoc(
                (sc.HeaderText, 2.0),
                (sc.ControlName, 1.0)
            )).ToArray();
            var scenCorpus = BM25.BuildCorpus(scenDocs);

            var ranked = scenarios
                .Select((sc, idx) =>
                {
                    var score = BM25.Score(scenDocs[idx], expandedWords, scenCorpus);
                    // Tie-breaker nudge: when nothing in the query specifically matches a
                    // wordier scenario (e.g. "settings page" vs. SettingsExpander's
                    // ItemsSource demo), prefer the canonical "Basic usage" / "A basic …"
                    // entry. Small additive boost so it only flips near-ties.
                    if (IsBasicHeader(sc.HeaderText)) score += 0.1;
                    return (sc, score);
                })
                // Stable order: by score desc, then by ID for determinism.
                .OrderByDescending(t => t.score)
                .ThenBy(t => t.sc.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Demote whole control if every scenario has a generic header.
            if (ranked.All(r => IsGenericHeader(r.sc.HeaderText))) s *= 0.85;

            var prefix = $"{first.Source}-";
            // Drop scenarios with zero BM25 score: they have no query-token hits at
            // all, so they're pure noise pulled in only because the parent control
            // ranked into top-N. Fall back to top-1 if that empties the list, so
            // the control still shows a representative scenario.
            var withSignal = ranked.Where(r => r.score > 0).Take(maxScenariosPerControl).ToList();
            var rows = (withSignal.Count > 0 ? withSignal : ranked.Take(1))
                .Select(r => new ScenarioRow($"{prefix}{r.sc.Id}", r.sc.HeaderText, r.sc.Description))
                .ToList();

            // Control-level summary: prefer explicit ControlDescription (toolkit frontmatter).
            string? controlDesc = scenarios
                .Select(sc => sc.ControlDescription)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d));
            // Fallback: when every scenario shares the same Description (Gallery, where
            // Description is per-control), promote it so per-row rendering can dedupe.
            if (controlDesc == null)
            {
                var firstDesc = scenarios.FirstOrDefault()?.Description;
                if (!string.IsNullOrEmpty(firstDesc) && scenarios.All(sc => sc.Description == firstDesc))
                    controlDesc = firstDesc;
            }

            groups.Add(new GroupedResult(
                Source: first.Source,
                ControlName: controlName,
                TotalScenarios: scenarios.Count,
                Scenarios: rows,
                Score: s,
                ControlDescription: controlDesc,
                HasNameBoost: hasNameBoost));
        }

        groups.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Source filter applied BEFORE the relevance floor + Take(maxControls).
        // If callers post-filtered after Take, valid hits could be silently dropped
        // when a non-matching source ranked above them (e.g. `--source toolkit` on
        // "colorpicker" — gallery's ColorPicker scores higher, would consume the
        // only slot, then the post-filter would return empty).
        if (sourceFilter != null)
        {
            groups = groups
                .Where(g => string.Equals(g.Source, sourceFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Relevance floor (applied to RUNNERS-UP only — top-1 always survives):
        //   Runner-up needs score ≥ 70% of top to be shown.
        //
        // Replaces an earlier two-tier (15%/40%) floor that let weak name-substring
        // hits leak through (e.g. "find text" → RichTextBlock + TextBlock + TextBox,
        // "image crop" → ImageCropper + Image). With 70% we keep only runners-up
        // that are genuinely close to top-1, e.g. "settings page" → SettingsCard +
        // SettingsExpander or "tabbed documents" → TabView + TabbedCommandBar.
        if (applyFloor && groups.Count > 1)
        {
            var topScore = groups[0].Score;
            var runnerUpFloor = topScore * 0.70;
            groups = groups.Where((g, i) => i == 0 || g.Score >= runnerUpFloor).ToList();
        }

        return groups.Take(maxControls).ToList();
    }

    /// <summary>True if a scenario header is a placeholder (Basic usage, Example N, "This is X").</summary>
    private static bool IsGenericHeader(string h)
    {
        if (string.IsNullOrWhiteSpace(h)) return true;
        var t = h.Trim();
        if (t.Equals("Basic usage", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^Example\s*\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^Sample\s*\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        if (t.StartsWith("This is the ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True if the header is the canonical "first example" of a control. Used as a
    /// per-scenario tie-breaker — when nothing in the query specifically picks a
    /// wordier scenario, the Basic/Simple variant should win.
    /// </summary>
    private static bool IsBasicHeader(string h)
    {
        if (string.IsNullOrWhiteSpace(h)) return false;
        var t = h.Trim();
        if (t.Equals("Basic usage", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Basic ",   StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Simple ",  StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("A basic ",  StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("A simple ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private Scenario PickBestScenario(List<Scenario> scenarios, string[] queryWords)
    {
        if (scenarios.Count == 1) return scenarios[0];

        var scenDocs = scenarios.Select(s => BM25.BuildDoc(
            (s.HeaderText, 2.0),
            (s.ControlName, 1.0)
        )).ToArray();
        var corpus = BM25.BuildCorpus(scenDocs);

        int bestIdx = 0;
        double bestScore = 0;
        for (int i = 0; i < scenarios.Count; i++)
        {
            var s = BM25.Score(scenDocs[i], queryWords, corpus);
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }
        return scenarios[bestIdx];
    }

    public (string formatted, bool found, string? canonicalId) GetPattern(string id)
    {
        // Check core patterns (ids are case-insensitive — humans and agents copy them
        // by hand, so "File-Picker-Desktop" must resolve the same as "file-picker-desktop").
        var core = _corePatterns.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (core != null) return (FormatCorePattern(core), true, core.Id);

        // Strip a known "{source}-" prefix and remember which source the caller asked
        // for, so we don't return a Gallery scenario for a "toolkit-..." id (or
        // vice-versa) when the bare ids happen to collide.
        string? expectedSource = null;
        var bareId = id;
        foreach (var src in _knownSources)
        {
            var p = src + "-";
            if (id.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                expectedSource = src;
                bareId = id[p.Length..];
                break;
            }
        }

        bool MatchesSource(Scenario s) => expectedSource == null || s.Source == expectedSource;

        // Exact match on the new {controlId}-{N} scheme (case-insensitive).
        var scenario = _scenarios.FirstOrDefault(s => string.Equals(s.Id, bareId, StringComparison.OrdinalIgnoreCase) && MatchesSource(s));

        // Fallback: caller passed a bare control id (e.g. "gallery-gridview")
        // → return the lowest-numbered scenario for that control. Parses the
        // trailing -{N} as an integer so e.g. "-2" sorts before "-10" (which
        // a lexicographic sort would invert). Ids without a parseable suffix
        // fall to the end and break ties via OrdinalIgnoreCase.
        if (scenario == null)
        {
            scenario = _scenarios
                .Where(s => string.Equals(s.ControlId, bareId, StringComparison.OrdinalIgnoreCase) && MatchesSource(s))
                .OrderBy(s => ParseTrailingNumber(s.Id))
                .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (scenario != null) return (FormatScenario(scenario), true, $"{scenario.Source}-{scenario.Id}");
        return ($"Pattern '{id}' not found.", false, null);
    }

    /// <summary>Parse the integer after the last <c>-</c> in <paramref name="id"/>,
    /// returning <see cref="int.MaxValue"/> if no parseable suffix is present so
    /// such ids sort to the end.</summary>
    private static int ParseTrailingNumber(string id)
    {
        int dash = id.LastIndexOf('-');
        if (dash < 0 || dash == id.Length - 1) return int.MaxValue;
        return int.TryParse(id.AsSpan(dash + 1), out var n) ? n : int.MaxValue;
    }

    public IEnumerable<(string id, string scenario)> ListAll()
    {
        foreach (var p in _corePatterns)
            yield return (p.Id, p.Scenario);

        // Show ALL scenarios grouped by (source, control) so multi-scenario controls are
        // discoverable AND collisions like ColorPicker (Gallery vs Toolkit) stay separate.
        var byControl = _scenarios
            .GroupBy(s => $"{s.Source}:{s.ControlId}")
            .OrderBy(g => g.First().Source)         // gallery first, toolkit after
            .ThenBy(g => g.First().ControlName);

        foreach (var group in byControl)
        {
            var first = group.First();
            var prefix = $"{first.Source}-";
            foreach (var s in group)
                yield return ($"{prefix}{s.Id}", ControlHeader(s.ControlName, s.HeaderText));
        }
    }

    private static string FormatCorePattern(CorePattern p)
    {
        // Note: blank lines between sections are intentionally omitted — the Copilot CLI
        // tool-result capture collapses runs of newlines, which would glue "**XAML:**"
        // onto the previous block's content. Single newlines (one per logical line)
        // survive intact.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {p.Scenario}");
        sb.AppendLine(p.Description);
        if (p.Prerequisites.Length > 0)
        {
            sb.AppendLine("**Prerequisites:**");
            foreach (var pre in p.Prerequisites) sb.AppendLine($"- {pre}");
        }
        if (p.Xaml != null)
        {
            sb.AppendLine("**XAML:**");
            AppendFencedCode(sb, "xml", NormalizeIndent(p.Xaml));
        }
        sb.AppendLine("**C#:**");
        AppendFencedCode(sb, "csharp", NormalizeIndent(p.CSharp));
        if (p.Notes.Length > 0)
        {
            sb.AppendLine("**Important:**");
            foreach (var n in p.Notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    /// <summary>Render "ControlName: HeaderText", or just "ControlName" when HeaderText is
    /// empty — a few legacy inline samples have no header source, and a bare ": " reads poorly.</summary>
    private static string ControlHeader(string controlName, string headerText)
        => string.IsNullOrWhiteSpace(headerText) ? controlName : $"{controlName}: {headerText}";

    /// <summary>
    /// Append a fenced code block whose fence is longer than any run of backticks inside
    /// <paramref name="code"/>. Snippets come from remote corpora, and a sample containing
    /// a line of three backticks would otherwise close the block early — everything after
    /// it then renders as top-level prose, letting an upstream sample forge tool-authored
    /// guidance (an "**Important:**" section is indistinguishable from the one find-ui's own
    /// pitfall notes emit) in the result an agent reads. Both the console output and
    /// the <c>--json</c> content field are built here, so this is the one place to size it.
    /// CommonMark allows any opening fence of three or more backticks and requires a closing
    /// fence to be at least as long, so out-running the longest interior run makes early
    /// termination impossible without discarding a snippet that legitimately shows markdown.
    /// </summary>
    private static void AppendFencedCode(System.Text.StringBuilder sb, string language, string code)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(code) + 1));
        sb.Append(fence).AppendLine(language);
        sb.AppendLine(code);
        sb.AppendLine(fence);
    }

    /// <summary>Length of the longest consecutive backtick run in <paramref name="s"/>.</summary>
    private static int LongestBacktickRun(string s)
    {
        int longest = 0, run = 0;
        foreach (var c in s)
        {
            if (c == '`')
            {
                run++;
                if (run > longest) longest = run;
            }
            else
            {
                run = 0;
            }
        }
        return longest;
    }

    private static string FormatScenario(Scenario s)
    {
        // See note on FormatCorePattern: no blank-line separators (runtime collapses them).
        var sb = new System.Text.StringBuilder();
        var sourceTag = s.Source switch
        {
            "toolkit" => " [CommunityToolkit]",
            "reactor" => " [Reactor]",
            _ => ""
        };
        sb.AppendLine($"## {ControlHeader(s.ControlName, s.HeaderText)}{sourceTag}");

        // Reactor: surface the (uniform) NuGet package. Any control-level `using`
        // directives are already folded into the C# snippet, and the shared
        // Microsoft.UI.Reactor api namespace is deliberately NOT emitted as a
        // **Namespace:** line — all reactor controls share it, so it'd be pure noise.
        if (s.Source == "reactor")
        {
            if (!string.IsNullOrEmpty(s.NuGetPackage))
                sb.AppendLine($"**Setup:** NuGet `{s.NuGetPackage}`");
        }
        else
        {
            // Prerequisites — single compact line. Gallery samples carry xmlns imports
            // too, and withholding them leaves markup using an undeclared prefix.
            if (!string.IsNullOrEmpty(s.NuGetPackage) || s.XmlnsImports.Length > 0)
            {
                var parts = new List<string>(1 + s.XmlnsImports.Length);
                if (!string.IsNullOrEmpty(s.NuGetPackage))
                    parts.Add($"NuGet `{s.NuGetPackage}`");
                foreach (var ns in s.XmlnsImports)
                    parts.Add($"`{ns}`");
                sb.AppendLine($"**Setup:** {string.Join(" · ", parts)}");
            }

            // Gallery non-default namespace hint — agents miss `using Microsoft.Windows.Notifications`
            // and similar long-tail imports. Skip the dominant Microsoft.UI.Xaml.Controls (auto-imported
            // in default templates) so 79/107 controls stay quiet. This is independent of the Setup
            // line above: a sample can need both an xmlns declaration and a C# using.
            if (s.Source == "gallery"
                && !string.IsNullOrEmpty(s.ApiNamespace)
                && s.ApiNamespace != "Microsoft.UI.Xaml.Controls")
            {
                sb.AppendLine($"**Namespace:** `{s.ApiNamespace}`");
            }
        }

        if (s.Xaml != null)
        {
            sb.AppendLine("**XAML:**");
            AppendFencedCode(sb, "xml", NormalizeIndent(s.Xaml));
        }
        if (s.CSharp != null && !IsBoilerplatePageWrapper(s.CSharp))
        {
            sb.AppendLine("**C#:**");
            AppendFencedCode(sb, "csharp", NormalizeIndent(s.CSharp));
        }
        var notesPayload = Notes.Get(s.ControlName);
        if (notesPayload.Pitfalls.Length > 0)
        {
            sb.AppendLine("**Important:**");
            foreach (var n in notesPayload.Pitfalls) sb.AppendLine($"- {n}");
        }
        if (notesPayload.FamilyName != null)
        {
            sb.AppendLine($"**Family ({notesPayload.FamilyName}):** {notesPayload.FamilyGuide}");
        }

        // Related controls (from WinUI Gallery's ControlInfoData.json)
        if (s.RelatedControls.Length > 0)
        {
            sb.AppendLine($"**See also:** {string.Join(", ", s.RelatedControls)}");
        }

        // Note: Docs links from ControlInfoData.json are intentionally not emitted —
        // benchmark logs show agents never fetch them, so the bytes/tokens are pure
        // overhead. The data is still kept on Scenario.Docs for potential future use.

        return sb.ToString();
    }

    /// <summary>
    /// Re-maps the distinct leading-whitespace levels in a code block to a canonical
    /// 0/2/4/6/... scale, preserving relative nesting structure.
    /// Gallery samples are sliced from deeply-nested original XAML, so attribute lines
    /// often start at column 30+. This collapses those huge prefixes (e.g. 36 → 4) while
    /// keeping each child line strictly indented relative to its parent.
    /// For already-clean code (e.g. C# at 0/4/8/12) this slightly tightens to 0/2/4/6.
    /// </summary>
    private static string NormalizeIndent(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;
        var lines = code.Split('\n');
        var distinct = new SortedSet<int>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            var indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            distinct.Add(indent);
        }
        if (distinct.Count == 0) return code;

        var map = new Dictionary<int, int>(distinct.Count);
        int rank = 0;
        foreach (var d in distinct)
        {
            map[d] = rank * 2;
            rank++;
        }

        var sb = new System.Text.StringBuilder(code.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                if (i > 0) sb.Append('\n');
                continue;
            }
            var indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            if (i > 0) sb.Append('\n');
            sb.Append(' ', map[indent]);
            sb.Append(line, indent, line.Length - indent);
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the C# block is just an empty page wrapper (constructor that only calls
    /// InitializeComponent — no other members or methods). Suppressing these saves ~140
    /// chars per scenario without losing any signal.
    /// </summary>
    private static bool IsBoilerplatePageWrapper(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return true;
        if (!cs.Contains("InitializeComponent")) return false;
        // Strip whitespace; if the only method body is `InitializeComponent();` the wrapper
        // is empty. Heuristic: count occurrences of `(` that introduce a callable other
        // than the ctor + InitializeComponent — none means boilerplate.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(cs, @"\s+", " ");
        // Pattern: ... class X : Page { public X() { this.InitializeComponent(); } }
        return System.Text.RegularExpressions.Regex.IsMatch(
            collapsed,
            @"class\s+\w+\s*:\s*Page\s*\{\s*public\s+\w+\s*\(\s*\)\s*\{\s*(?:this\.)?InitializeComponent\(\)\s*;\s*\}\s*\}\s*$");
    }
}
