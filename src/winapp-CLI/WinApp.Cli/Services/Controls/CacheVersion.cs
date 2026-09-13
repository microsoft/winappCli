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
///
/// Note: adding the embedded snapshot floor did NOT bump this. The cached payload's
/// schema and extraction logic are unchanged, and a bump would have forced every
/// existing user through a several-hundred-request re-fetch for no benefit.
/// </summary>
internal static class CacheVersion
{
    public const string Current = "21";
}
