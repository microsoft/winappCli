# Microsoft.WindowsAppSDK.Analyzers rules catalog

This document is the single source of truth for every diagnostic the analyzer ships.
**Adding, removing, or changing the severity of a rule requires updating this file in the same PR.**

## Diagnostic ID methodology

IDs use the form **`WUIxxxx`** where the leading digit identifies a category. IDs are
**immutable**: once allocated, a number is never reused, even after a rule is removed.
Removed rules stay listed below with a "removed in vX.Y" note and the reason.

| Range          | Category                           | DiagnosticCategory string |
|----------------|------------------------------------|----------------------------|
| `WUI0001–0999` | UWP → WinUI 3 API compatibility    | `WinUI.Compatibility`     |
| `WUI1000–1999` | Migration suggestions (data-driven) | `WinUI.Migration`         |
| `WUI2000–2999` | Runtime / layout / XAML pitfalls   | `WinUI.Runtime`           |
| `WUI3000–3999` | MVVM / CommunityToolkit patterns   | `WinUI.Mvvm`              |
| `WUI4000–4999` | Interop (WebView2, COM, AI)        | `WinUI.Interop`           |

Subgroups (informal, used to keep adjacent rules clustered):

* `WUI200x` layout/control content · `WUI201x` x:Bind · `WUI202x` accessibility · `WUI203x` code-behind syntax
* `WUI400x` WebView2 · `WUI410x` ONNX Runtime GenAI

## Severity policy

The analyzer's primary audience includes external WinUI 3 developers. Builds must not
fail unexpectedly, so:

* **`Warning` is the ceiling for default severity.** No rule ships at `Error` by default.
* `Info`/`Hidden` is preferred for migration suggestions where false positives are plausible.
* Users opt into build-breaking enforcement per rule via `.editorconfig`:
  ```ini
  [*.cs]
  dotnet_diagnostic.WUI0001.severity = error
  ```

## False-positive guards

The analyzer takes false positives seriously — every guard below is testable.

1. **Semantic over syntactic.** Rules resolve symbols via `SemanticModel` and verify
   the containing namespace before reporting. Identifier-name-only matches (`if (id == "Window")`)
   are banned except for clearly unique tokens (e.g. `EnsureCoreWebView2Async`) and
   require a justification comment in the rule.
2. **Per-rule allowlists.** [`Allowlists.cs`](src/Microsoft.WindowsAppSDK.Analyzers/Allowlists.cs) holds
   declarative carve-outs (e.g. `ConnectedAnimationService.GetForCurrentView()` is
   exempt from WUI0004). New entries require a regression test in
   [`SuppressionTests.cs`](tests/Microsoft.WindowsAppSDK.Analyzers.Tests/Rules/SuppressionTests.cs)
   or rule-specific tests.
3. **Project-context gating.** Migration-only rules (`WUI1xxx`) are gated by
   [`ProjectContext.Detect`](src/Microsoft.WindowsAppSDK.Analyzers/ProjectContext.cs), which classifies
   a compilation as `MigratingFromUwp`, `GreenfieldWinUI`, or `Unknown`. WUI1xxx fires
   only in `MigratingFromUwp` projects. Heuristics:
   * `Package.appxmanifest` AdditionalFile with `xmlns:uap=` → MigratingFromUwp
   * Any `using Windows.UI.Xaml` / `Windows.ApplicationModel.Activation` → MigratingFromUwp
   * Otherwise → GreenfieldWinUI / Unknown (treated as greenfield)
4. **Severity ceiling.** See above. Every diagnostic ships at `Warning` or `Info`, never `Error`.
5. **Suppression must work.** Every shipping rule has a regression test in
   [`SuppressionTests.cs`](tests/Microsoft.WindowsAppSDK.Analyzers.Tests/Rules/SuppressionTests.cs)
   asserting `#pragma warning disable WUIxxxx` silences it. A rule that doesn't honor
   pragma is unshippable.
6. **Real-world corpus.** [`tools/run-corpus.ps1`](tools/run-corpus.ps1) builds a curated
   set of open-source WinUI 3 apps with the analyzer injected and reports every
   diagnostic. Run weekly via [`.github/workflows/corpus.yml`](.github/workflows/corpus.yml);
   any new flag must be triaged before merge.
7. **No telemetry.** The analyzer never phones home and never writes diagnostic logs to
   disk. False-positive triage relies entirely on the corpus suite + user repros.
8. **New-rule checklist** (PR template enforces): proposed ID + range, severity
   justification, Microsoft Learn link, ≥3 false-positive scenarios with tests,
   suppression test, CHANGELOG entry.

## Rules

<a id="wui0001"></a>
### WUI0001 — UWP XAML namespace used
* **Category:** `WinUI.Compatibility` · **Severity:** `Warning`
* **Fires when:** A `using Windows.UI.Xaml...` directive is present.
* **Why:** `Windows.UI.Xaml` is the UWP namespace; WinUI 3 lives under `Microsoft.UI.Xaml`.
* **Microsoft Learn:** [Migrate to Windows App SDK — overview](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/migrate-to-windows-app-sdk-ovw)
* **Suppression:** `#pragma warning disable WUI0001` or `dotnet_diagnostic.WUI0001.severity = none`.

<a id="wui0002"></a>
### WUI0002 — `Window.Current` is UWP-only
* **Category:** `WinUI.Compatibility` · **Severity:** `Warning`
* **Fires when:** `Window.Current` or `Application.Current.Window` is referenced.
* **Why:** Doesn't exist in WinUI 3 desktop. Store the `Window` reference on `App`.
* **Microsoft Learn:** [API mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table)

<a id="wui0003"></a>
### WUI0003 — `CoreDispatcher` / `Dispatcher` is UWP-only
* **Category:** `WinUI.Compatibility` · **Severity:** `Warning` (startup-crash tier for `Dispatcher` member access)
* **Fires when:** Either (a) a symbol resolves to `Windows.UI.Core.CoreDispatcher` (or unresolved `CoreDispatcher` in a type position); or (b) a member is accessed on the inherited `DependencyObject.Dispatcher` / `CoreWindow.Dispatcher` property — e.g. `Dispatcher.HasThreadAccess`, `this.Dispatcher.RunAsync(...)`. Detected by symbol (a `Dispatcher` property typed `CoreDispatcher`) when metadata is present, with a syntactic fallback (target's rightmost name is exactly `Dispatcher`) for the loose-source driver path (raw source) where symbols don't bind.
* **Why:** `DependencyObject.Dispatcher` returns `null` in WinUI 3 desktop apps, so `Dispatcher.*` member access compiles clean but throws `NullReferenceException` at launch (window never appears → run failure). Use `DispatcherQueue.TryEnqueue(...)` / `DispatcherQueue.HasThreadAccess` in WinUI 3.
* **Microsoft Learn:** [API mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table)
* **Known false-positive risk:** medium — the type-name path relies on semantic resolution (early in build, before WinUI references load, we fall back to a syntactic `BaseTypeSyntax`/`TypeSyntax` heuristic). The member-access path's syntactic fallback keys on the target name being exactly `Dispatcher`, so a user-defined property literally named `Dispatcher` (that is *not* the UWP one) would also fire; `DispatcherQueue` never matches. Suppress per-line if a user type/property is named `CoreDispatcher`/`Dispatcher`.

<a id="wui0004"></a>
### WUI0004 — `GetForCurrentView` is UWP-only

`GetForCurrentView()` returns `null` in WinUI 3 desktop apps. Many UWP types that exposed this static factory have a different replacement (`AppWindow.GetFromWindowId`, OS-supplied callbacks, etc.). See the Microsoft Learn windowing migration guide.

**Allowlisted types** (rule does not fire): `ConnectedAnimationService`. See [`Allowlists.cs`](src/Microsoft.WindowsAppSDK.Analyzers/Allowlists.cs) — adding a new entry requires a regression test.

<a id="wui1001"></a>
### WUI1001 — UWP API has Windows App SDK equivalent (data-driven)

Reports any UWP type/member listed in the [Microsoft Learn API mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table) with a documented WinAppSDK replacement. The replacement is included in the diagnostic message.

The full mapping table lives in [`ApiMappings.g.cs`](src/Microsoft.WindowsAppSDK.Analyzers/ApiMappings.g.cs) — adding a new mapping is a data PR (one new line + one test).

**Gating**: only fires in projects detected as **migrating from UWP** (see "Project context detection" below). Greenfield WinUI 3 projects see nothing.

<a id="wui1002"></a>
### WUI1002 — UWP API has no Windows App SDK equivalent (data-driven)

Same source as WUI1001, but for entries where Microsoft documents "Not supported in Windows App SDK" (e.g. `PrintManager`, `DisplayRequest`, `SystemNavigationManager`). These usually require a redesign rather than a port.

<a id="wui1010"></a>
### WUI1010 — Migration feature-area hint (Info)

Informational only. When code uses any namespace listed in the [Microsoft Learn feature mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/feature-mapping-table), this rule emits a one-line link to the relevant migration guide. Default severity is `Info`; opt into `Warning` via `.editorconfig` if you want to track migration progress in CI.

<a id="wui2001"></a>
### WUI2001 — Raw control as `TabView` content (code-behind)
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** `someTabItem.Content = new TextBox/Grid/...()` where the left-hand side resolves to (or is named like) a `TabViewItem`.
* **Why:** `TabView`'s `ContentPresenter` does not propagate stretch alignment; raw controls render at ~50px height. Use `Frame.Navigate(typeof(Page))`.

<a id="wui2002"></a>
### WUI2002 — Raw `TabView` content (cross-file XAML + code-behind)
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** XAML declares `<TabView>` and the matching code-behind assigns a raw control to a tab item's `Content`.

<a id="wui2003"></a>
### WUI2003 — UWP-only XAML control
* **Category:** `WinUI.Compatibility` · **Severity:** `Warning`
* **Fires when:** A XAML file (`AdditionalFiles`, excluding `App.xaml`) declares a control with no direct WinUI 3 equivalent: `Pivot`, `PivotItem`, `Hub`, `HubSection`, or `VirtualizingStackPanel`.
* **Why:** These controls were removed in WinUI 3; XAML that references them will not load. The diagnostic names the replacement path (`Pivot`→`NavigationView`/`TabView`/`SelectorBar`, `Hub`→`NavigationView`/custom layout, `VirtualizingStackPanel`→`ItemsStackPanel`/`ItemsRepeater`).
* **False-positive guards:** Matches by local element name only, so a same-named custom control in a non-UWP namespace is a possible (rare) false positive — suppress per-file via `.editorconfig`. Reported at compilation-end with `WellKnownDiagnosticTags.CompilationEnd`.

<a id="wui2010"></a>
### WUI2010 — Nested `x:Bind` without `FallbackValue`
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** An `{x:Bind A.B.C}` path has 3+ segments and lacks `FallbackValue=`.
* **Why:** Crashes if any segment is `null` at startup.

<a id="wui2011"></a>
### WUI2011 — `x:Bind` without `Mode=`
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** `{x:Bind ...}` has no `Mode=` and is not a command/converter/event-handler binding.
* **Why:** `x:Bind` defaults to `OneTime` — UI never updates after first load.

<a id="wui2012"></a>
### WUI2012 — `Converter={x:Null}` crashes at runtime
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** Any attribute value contains `Converter={x:Null}`.
* **Why:** Throws `Resource Dictionary Key can only be String-typed`. Use an `x:Bind` function instead.

<a id="wui2020"></a>
### WUI2020 — Interactive control missing `AutomationId`
* **Category:** `WinUI.Runtime` · **Severity:** `Info`
* **Fires when:** A control from a known interactive set (Button, TextBox, etc.) lacks `AutomationProperties.AutomationId`.
* **Why:** UI automation targeting becomes unreliable.
* **Severity rationale:** `Info` (not `Warning`) — accessibility hygiene, often noisy in early-stage code.

<a id="wui2030"></a>
### WUI2030 — Attached-property object initializer
* **Category:** `WinUI.Runtime` · **Severity:** `Warning`
* **Fires when:** Object initializer writes a nested `{ ... }` block to a recognized attached-property type name (`AutomationProperties`, `Canvas`, `Grid`, `ToolTipService`, …).
* **Why:** Doesn't compile. Use the static setter, e.g. `AutomationProperties.SetAutomationId(btn, "...")`.

<a id="wui3001"></a>
### WUI3001 — Old field-backed `[ObservableProperty]`
* **Category:** `WinUI.Mvvm` · **Severity:** `Warning`
* **Fires when:** A field has `[ObservableProperty]` (CommunityToolkit.Mvvm 8.2 syntax).
* **Why:** Triggers AOT warning `MVVMTK0045`. Use partial-property syntax (8.3+).

<a id="wui4001"></a>
### WUI4001 — WebView2 used without initialization (single-file)
* **Category:** `WinUI.Interop` · **Severity:** `Warning`
* **Fires when:** A class invokes `NavigateToString`/`Navigate`/`PostWebMessageAs*`/`ExecuteScriptAsync`/`CoreWebView2` without a sibling `EnsureCoreWebView2Async()` call or `CoreWebView2Initialized` handler.

<a id="wui4002"></a>
### WUI4002 — WebView2 in XAML without init in code-behind
* **Category:** `WinUI.Interop` · **Severity:** `Warning`
* **Fires when:** `<WebView2>` is in XAML and the matching `.xaml.cs` has no `EnsureCoreWebView2Async()` / `CoreWebView2Initialized`.

<a id="wui4101"></a>
### WUI4101 — Removed GenAI API: `SetInputSequences`
* **Category:** `WinUI.Interop` · **Severity:** `Warning`
* **Fires when:** Any invocation named `SetInputSequences`.
* **Why:** Removed in OnnxRuntimeGenAI 0.6.0. Use `generator.AppendTokenSequences(sequences)`.

<a id="wui4102"></a>
### WUI4102 — Removed GenAI API: `ComputeLogits`
* **Category:** `WinUI.Interop` · **Severity:** `Warning`
* **Fires when:** Any invocation named `ComputeLogits`.
* **Why:** Removed in OnnxRuntimeGenAI 0.6.0; `GenerateNextToken()` handles logits internally.

<a id="wui4103"></a>
### WUI4103 — Removed GenAI API: `TokenizerStream` constructor
* **Category:** `WinUI.Interop` · **Severity:** `Warning`
* **Fires when:** `new TokenizerStream(...)`. Use `tokenizer.CreateStream()`.

## Migration from legacy IDs (pre-1.0)

The legacy ID range `WUI001..WUI021` from the in-tree `win-dev-skills` analyzer maps as follows:

| Legacy   | Current   | Notes                                  |
|----------|-----------|----------------------------------------|
| WUI001   | WUI2001   | TabViewRawContent (code-behind)        |
| WUI002   | WUI4001   | WebView2NoInit (single-file)           |
| WUI003   | WUI0001   | UwpXamlNamespace                       |
| WUI004   | WUI0002   | WindowCurrent                          |
| WUI005   | WUI0003   | CoreDispatcher                         |
| WUI006   | WUI0004   | GetForCurrentView                      |
| WUI007   | WUI2010   | XBindNestedNoFallback                  |
| WUI008   | WUI3001   | OldMvvmSyntax                          |
| WUI010   | WUI2020   | MissingAutomationId (downgraded → Info) |
| WUI011   | WUI2011   | XBindMissingMode                       |
| WUI012   | WUI2030   | AttachedPropertyInitializer (downgraded → Warning) |
| WUI013   | WUI4101   | GenAiSetInputSequences (downgraded → Warning) |
| WUI014   | WUI4102   | GenAiComputeLogits (downgraded → Warning) |
| WUI015   | WUI4103   | GenAiTokenizerStreamCtor (downgraded → Warning) |
| WUI016   | WUI2012   | NullConverter (downgraded → Warning)   |
| WUI020   | WUI4002   | WebView2NoInit (cross-file)            |
| WUI021   | WUI2002   | TabViewRawContent (cross-file)         |

Legacy IDs `WUI001..WUI021` are **retired** and will not be reused.

## Removed rules

_(none yet)_
