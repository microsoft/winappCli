namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Single source of truth for the on-disk cache version. Each provider's cache
/// lives under the managed global <c>.winapp/cache/find-ui/{providerId}</c>
/// directory (gallery / toolkit / reactor). <see cref="CachedProviderBase"/>
/// stamps this string into that provider's <c>schema-version.txt</c> on write
/// and requires an exact match on read; any mismatch forces a cache miss.
///
/// It also pins the corpus baked into the binary: the build-time snapshot baker writes
/// <see cref="Current"/> into the snapshot manifest and <see cref="EmbeddedSnapshot"/>
/// refuses to serve a snapshot stamped with anything else, so a snapshot produced by
/// different extraction logic can never be mixed with live or cached data.
///
/// Bump <see cref="Current"/> whenever ANY cached payload should be discarded:
///   1. Scenario / tag JSON schema changes (new or removed fields)
///   2. Embedded <c>Data/*.json</c> content changes (e.g. new tags added,
///      tag-list contents widened) — bump even if the C# schema is unchanged,
///      otherwise existing caches keep serving the older fallback contents.
///   3. Tag extraction / cleaning logic changes that would alter the cached
///      output for the same input data.
///
/// A bump requires a re-bake in the same change:
/// <c>EmbeddedSnapshotTests.Manifest_Ships_AndMatchesCurrentCacheVersion</c> fails the
/// build when the committed snapshot's version doesn't match, because a mismatch would
/// silently drop the embedded floor and restore the offline outage it exists to fix.
///
/// History:
///   "10" — Notes / Synonyms refactor
///   "11" — Added chip/token/tag entries to tokenizingtextbox in toolkit-tags.json
///   "12" — Added StopWords.TagOnly (text/input/layout/pick/basics/advanced)
///          → tag dicts cleansed; query tokens unchanged.
///   "13" — Toolkit cache now written CLEAN (CleanTagDictionary applied
///          before serialize), matching GalleryFetcher behavior. Old caches
///          contained polluted toolkit tags that were only filtered on read.
///   "14" — Plan A: separate keywords.json cache file; Plan B: HeaderText
///          is now the Sample's Header attribute alone (no " — Description"
///          suffix), Description holds the .md paragraph or XAML Description
///          attribute as a fallback.
///   "15" — Toolkit CleanCSharp now folds platform #if/#else/#endif (keeps
///          WINAPPSDK branch, drops UWP/Uno fallbacks) so emitted samples
///          compile clean against WinAppSDK without the noisy preprocessor.
///   "16" — Toolkit scenario IDs now renumbered in stable sample-path order
///          (was: alphabetical-by-slug, which reshuffled when upstream
///          rewords a Header). Old caches still resolve correctly inside a
///          single process but {controlId}-{N} differs across versions.
///   "17" — WinUI-Gallery moved + reformatted its samples: ControlInfoData.json
///          relocated to SampleSupport/Data/, pages are per-control under
///          Samples/{UniqueId}/, and ControlExample code now lives in
///          "--- header/xaml/c#" SampleDefinition .txt bundles. GalleryFetcher
///          parser rewritten and the embedded Data/gallery-*.json snapshot
///          regenerated from the new format — bump to discard old-format caches.
///   "18" — Legacy inline a11y samples with no leading comment now fall back to
///          the control Subtitle for HeaderText (was empty → "{Control}: "),
///          so the embedded gallery snapshot changed.
///   "19" — Event handlers not backed by emitted code-behind are now stripped from
///          both Gallery and Toolkit XAML (StripUnbackedEventHandlers): the WinUI
///          Gallery keeps sample handlers in shared page code-behind we don't fetch,
///          so ~44 scenarios shipped XAML wired to a missing handler (compile error
///          on paste). Method-aware: backed handlers (e.g. TabView's) are kept.
///          Regenerate so old caches drop the dangling handlers.
///   "20" — GalleryFetcher no longer injects a hand-authored ItemsRepeater photo-grid
///          scenario or rewrites tabview-1's C#; Gallery samples are now served as
///          upstream publishes them. This is rule 3 above: same input, different
///          output. Without the bump an existing cache still matches on "19" and keeps
///          serving the winapp-authored sample under the [gallery] tag indefinitely —
///          a re-bake alone never reaches a user who already has a cache.
///   "21" — GalleryFetcher now drops $(Name) substitution tokens that stand in an
///          element's attribute list instead of flattening them to "..." like a
///          value-position token. Flattening produced `Click="X" .../>`, which fails
///          structural validation, so ten upstream samples (Button, ToggleButton,
///          RepeatButton, HyperlinkButton, ProgressRing x2, CommandBar, AnimatedIcon,
///          PersonPicture, EasingFunction) were served with no XAML at all. Rule 3:
///          same input, different output — an unbumped cache keeps serving the empty
///          scenarios.
///   "22" — Substitution placeholders are now handled on the index path too:
///          SampleIndexParser suppresses an xaml or code block that still carries a
///          $(Name) token rather than serving it as pasteable, and drops a sample left
///          with neither. ScenarioSanitizer enforces the same rule on the way out.
///          GalleryFetcher also stopped flattening a value-position token to "...":
///          156 attributes in the previous bake were typed properties (StrokeThickness,
///          Height, Width, Orientation, SelectionMode, PaneDisplayMode, IsChecked...)
///          where "..." is a compile error on paste, so the attribute is now dropped and
///          the property falls back to its own default. Rule 3 twice over: same input,
///          different output. Without the bump an existing cache still matches on "21"
///          and keeps serving both the placeholder-bearing blocks and the broken
///          attributes that this change exists to remove.
///   "23" — Samples are no longer truncated. Gallery XAML/C# and Toolkit XAML/C# were
///          capped (2000/2500 and 1000/2500 chars) and emitted with a
///          "&lt;!-- ...truncated --&gt;" / "// ...truncated" marker, so longer samples were
///          unbuildable as pasted (issue #716). Toolkit's "extract just the core control
///          element" fallback for oversized XAML is gone with it, so those samples now
///          carry their full surrounding markup. Rule 3: same input, different output —
///          without the bump an existing cache keeps serving the truncated snippets.
///   "24" — Gallery is now read from the sample index WinUI-Gallery publishes
///          (catalog/windows-samples.json) instead of being scraped, and GalleryFetcher
///          is deleted. Upstream decides what a sample is, so the corpus is theirs
///          rather than our reconstruction of it: samples they withhold as unpasteable
///          are absent, and samples our extraction missed appear. Rule 3 — same source
///          repository, different output. Scenario ids are positional over kept samples
///          (as they have been since "16"), so a sample upstream drops renumbers the
///          ones after it on that control's page; an unbumped cache would keep serving
///          the old numbering against the new data. The index publishes Gallery's source
///          verbatim, so GalleryProvider normalizes each sample for pasting the way the
///          scraper used to — Gallery-private symbols rewritten, event handlers with no
///          implementation dropped — which is itself Rule 3.
///
/// Note: adding the embedded snapshot floor did NOT bump this. The cached payload's
/// schema and extraction logic are unchanged, and a bump would have forced every
/// existing user through a several-hundred-request re-fetch for no benefit.
/// </summary>
internal static class CacheVersion
{
    public const string Current = "24";
}
