# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Migrated into the `microsoft/winappCli` repo** (from `microsoft/win-dev-skills`)
  and repackaged as **`Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer`** (the assembly
  name stays `Microsoft.WindowsAppSDK.Analyzers`). The package is packed by
  `scripts/package-nuget.ps1` and shares the CLI version. Consumers add it as a
  `PackageReference` to get the diagnostics. **Not yet published** — the first
  nuget.org release is cut by the repo's `rel/v*` pipeline.
- **Graceful self-deactivation contract** — when a consumer (or a future Windows
  App SDK that bundles these analyzers) sets
  `<WindowsAppSDKProvidesWinUIAnalyzer>true</WindowsAppSDKProvidesWinUIAnalyzer>`,
  the package's `.targets` drops its analyzer from `@(Analyzer)` and skips its XAML
  target, so a project referencing both never sees duplicate `WUIxxxx` diagnostics.
- **`WUI1001` / `WUI1002` — Data-driven UWP→WinAppSDK API mapping rules**
  sourced from the [Microsoft Learn API mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table).
  ~30 mappings shipped; adding more is a data PR (one row in `ApiMappings.g.cs` + one test).
- **`WUI1010` — Migration feature-area hints (Info)** sourced from the
  [feature mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/feature-mapping-table).
- **`ProjectContext` detector** — gates `WUI1xxx` to projects classified as
  `MigratingFromUwp` (heuristics: `Package.appxmanifest` AdditionalFile, `Windows.UI.*`
  using directives). Greenfield WinUI 3 projects see no migration noise.
- **`Allowlists.cs`** — declarative per-rule carve-outs replacing inline string literals.
  Now covers `GetForCurrentView`, `Window.Current`, UWP-XAML namespace false friends,
  and the WebView2 containing-type guard.
- **`SuppressionTests.cs`** — pragma-suppression regression test for every shipping rule
  (11 tests). A rule that doesn't honor `#pragma warning disable` will turn this red.

### Changed
- Analyzer references the oldest supported Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.8.0,
  = .NET SDK 8.0.100) so older compilers still load it.
- `UwpApiAnalyzer.GetForCurrentView` heuristic now consults `Allowlists`
  instead of inline `Contains("ConnectedAnimationService")` — same behavior, easier to
  extend, regression-tested.
- **`WUI0003` now also flags `DependencyObject.Dispatcher` member access** (e.g.
  `Dispatcher.HasThreadAccess`, `this.Dispatcher.RunAsync(...)`), not just the literal
  `CoreDispatcher` type name. The inherited `Dispatcher` property returns `null` in WinUI 3
  desktop apps, so such access compiles clean but throws `NullReferenceException` at launch
  (window never appears → run failure) — now surfaced as a **startup-crash** finding.
  Detection is symbol-based (a `Dispatcher` property typed `CoreDispatcher`) with a syntactic
  fallback (target's rightmost name is exactly `Dispatcher`) for the loose-source
  driver path (raw source, no WinUI metadata) where symbols don't bind. `DispatcherQueue` is unaffected.

## [0.1.0-alpha] — 2026-04-20 (pre-migration lineage, `microsoft/win-dev-skills`)

> Historical entries from when the analyzer lived in `win-dev-skills`. It was **not**
> published to nuget.org under any package ID; it shipped there as a committed prebuilt
> DLL. The first public NuGet release happens from `winappCli` (see Unreleased).

### Added
- Initial version, extracted from the `microsoft/win-dev-skills` repository.
- Categorized diagnostic ID methodology (`WUI0xxx` compat / `WUI1xxx` migration /
  `WUI2xxx` runtime / `WUI3xxx` MVVM / `WUI4xxx` interop). See `RULES.md`.
- 17 diagnostics across the 5 categories.
- xUnit + `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` test harness with
  positive / negative / false-positive-guard tests per rule.

### Changed
- **All `Error`-severity rules downgraded to `Warning`** (or `Info` for
  `WUI2020`) to honor the new severity ceiling. Builds will not fail by default.
  Users opt into build-breaking enforcement per-rule via `.editorconfig`.
- Diagnostic categories standardized to the `WinUI.<Category>` form
  (`WinUI.Compatibility`, `WinUI.Runtime`, `WinUI.Mvvm`, `WinUI.Interop`).
- `helpLinkUri` populated for every rule, pointing to the corresponding section
  in `RULES.md`.

### Migration from in-tree `Microsoft.WindowsAppSDK.Analyzers` (legacy IDs `WUI001..WUI021`)
See the migration table in `RULES.md`. Legacy IDs are retired and not reused.
