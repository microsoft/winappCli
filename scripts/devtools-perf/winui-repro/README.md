# WinUI XAML diagnostics cost: minimal repro

When a WinUI 3 app runs with XAML diagnostics enabled, it gets slower and bigger the longer it runs. Diagnostics are enabled by a diagnostics TAP loaded through `InitializeXamlDiagnosticsEx`, which is what Visual Studio XAML Hot Reload, Live Visual Tree and winapp DevTools all use, or by `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`. The cost is never released while the app runs.

This folder reproduces it with no tooling beyond a ~120-line TAP whose visual-tree callback only counts. It doesn't depend on winapp.

## What's in the folder

| Path | What it is |
|---|---|
| `ReproApp/` | Unpackaged, self-contained WinUI 3 app (Windows App SDK 1.8.260317003, .NET 10, x64). It measures itself and exits, writing a CSV. **Memory phase:** each round creates and removes 40,000 collapsed elements (`Border` + `TextBlock`, created in code, never rendered) and records settled private bytes. **Navigation phase:** each round navigates 10 times to `CheckBoxPage` (120 `CheckBox` elements) and back, recording the median UI-thread CPU time from `Frame.Navigate` to `Loaded`. |
| `ProbeTap/ReproTap.cpp` | A minimal TAP. `SetSite` takes `IVisualTreeService3` and, with initialization data `advise=1`, calls `AdviseVisualTreeChange` with a callback that only counts. With `advise=0` it never subscribes. |
| `ProbeTap/Inject.cpp` | Calls `InitializeXamlDiagnosticsEx` from the target's own `Microsoft.Internal.FrameworkUdk.dll` to load the TAP. |
| `ReproApp.sln` | The same app, for F5 in Visual Studio. |
| `run.ps1` | Builds everything, runs each configuration as a fresh launch, and prints the comparison. |

## Run it

Requirements: Windows 11, .NET 10 SDK, and Visual Studio with the C++ x64 build tools (to compile the TAP). The `vs-*` configurations also need the WinUI workload.

```powershell
.\run.ps1                          # off, env, tap, tap-advise (~15 minutes)
.\run.ps1 -Configs vs-on, vs-off   # under the Visual Studio debugger (see below)
```

| Config | What runs |
|---|---|
| `off` | nothing attached |
| `env` | `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`, no TAP |
| `tap` | ReproTap loaded, **never subscribes** |
| `tap-advise` | ReproTap loaded and subscribed (no-op callback) |
| `vs-on` / `vs-off` | `devenv ReproApp.sln /Command Debug.Start` with Tools → Options → Debugging → XAML Hot Reload on or off |

For `vs-*`, Visual Studio must be closed first. The script sets `debugging.xamlHotReload.enableXamlHotReload` in Visual Studio's `settings.json` and restores the file byte for byte afterwards. Visual Studio takes the foreground; no input is sent.

### Running the Visual Studio comparison by hand

1. Open `ReproApp.sln`, set Tools → Options → Debugging → XAML Hot Reload, and close the dialog.
2. Press F5 and leave the app alone. It starts measuring after 5 s and exits by itself, which ends the debug session.
3. The CSV is `%TEMP%\winui-diag-repro\run-<time>.csv`. Repeat with the option flipped.
4. Compare: `.\run.ps1 -Summarize <csv>, <csv>`.

The first line of each CSV records what was attached: whether `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO` was set, whether a debugger was attached, and which diagnostics modules were loaded. For example, Visual Studio's TAP is `Microsoft.VisualStudio.DesignTools.WinUITap.dll`.

## Results

Cloud PC (Xeon 8370C, 16 cores, no GPU, so WARP rendering), Windows 11 26200, Windows App SDK 1.8.260317003, Visual Studio 18 Enterprise. Two launches per configuration (L1 / L2), 12 rounds per phase. Navigation is the median UI-thread CPU time of one navigation in a round. That covers only the UI thread from `Frame.Navigate` to `Loaded` (parsing, template expansion and layout), not render-thread or composition work. Wall-clock time gives the same result; see the next section.

Both launches ran on a quiet machine: L2 at 23:55–00:19 and L1 at 01:44–02:10. `run.ps1` samples total CPU and any other build, test or app processes every 10 s into `activity.csv` (in `results/launch1`). During L1 the median machine CPU was 18%. The only other processes were this run's own MSBuild nodes while Visual Studio built the app for `vs-on`/`vs-off`. An earlier first launch overlapped another session's CPU load test and was discarded.

| Config | App | Attached (read by the app) | Element churn, 40k (ms) | Retained per created element | Navigation, round 1 (ms) | Navigation, mean of last 4 rounds (ms) | **Navigation slope (ms per round)** |
|---|---|---|---:|---:|---:|---:|---:|
| `off` | unpackaged | – | 716 / 719 | ~0 B | 562 / 562 | 766 / 774 | **16 / 20** (flat; rounds alternate ~500/~1,000) |
| `env` | unpackaged | `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` | 699 / 721 | ~0 B | 641 / 609 | 1,992 / 2,008 | **117 / 146** |
| `tap` | unpackaged | ReproTap, not subscribed | 971 / 980 (+36%) | ~0 B | 609 / 641 | 1,961 / 2,074 | **129 / 140** |
| `tap-advise` | unpackaged | ReproTap, subscribed | 1,159 / 1,146 (+61%) | **226 / 228 B** | 641 / 641 | 2,078 / 2,164 | **140 / 152** |
| `vs-on` | unpackaged | VS debugger, `WinUITap.dll`, env var set | **3,644 / 3,646 (×5.1)** | 137 / 101 B | 703 / 734 | 2,168 / 2,304 | **146 / 159** |
| `vs-off` | unpackaged | VS debugger, **env var still set**, no TAP | 822 / 826 (+15%) | ~0 B | 594 / 672 | 2,023 / 2,144 | **136 / 148** |
| VS F5, Hot Reload off ¹ | **packaged** (MSIX) | VS debugger, env var **not** set, no TAP | 824 | ~0 B | – | – | flat (×1.02 over 9 rounds) |

¹ From the earlier packaged-app harness (`scripts/devtools-perf/retention.ps1`), one launch measured by wall clock on a different page. It's included only as a contrast: F5 activates a packaged app through its package, so the app doesn't inherit Visual Studio's environment.

What the table shows:

1. **Repeated navigation to the same XAML page gets slower over time whenever source info is on**, whether it comes from the env var or a loaded TAP. Without diagnostics the slope is 16–20 ms per round (flat within noise). With diagnostics it's 117–159 ms per round, so after 120 navigations a navigation costs ~3× what it did at the start. The subscription doesn't matter: `tap` and `tap-advise` are within launch-to-launch noise of each other and of `env`.
2. **A visual-tree subscription retains memory per element ever created:** ~230 B for a no-op callback, ~100–140 B under Visual Studio's TAP. Without a subscription it's ~0. Element creation is also slower: +36% with a TAP loaded, +61% subscribed, and ×5 with Visual Studio's TAP.
3. **Visual Studio sets `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` for an unpackaged app even with XAML Hot Reload off** (`vs-off`). That's enough for the progressive slowdown, but there's no retention because no TAP is loaded. A packaged app launched by F5 doesn't inherit the variable, and with Hot Reload off it stays flat (last row).

### Wall clock gives the same result

Each launch also records wall-clock time per navigation, from `Frame.Navigate` to `Loaded` plus the next rendered frame, so render-thread time is included. Three `off`/`tap` pairs on a quiet machine (L1 above, plus `results/wallclock/pair2` and `pair3`; median machine CPU 15–18% with no other build, test or app processes):

| Pair | `off` wall slope (CPU slope) | `off` wall, round 1 → last 4 | `tap` wall slope (CPU slope) | `tap` wall, round 1 → last 4 |
|---|---:|---|---:|---|
| 1 | 14 (16) ms/round | 569 → 767 ms | 130 (129) ms/round | 651 → 1,998 ms |
| 2 | 19 (17) | 555 → 779 | 138 (136) | 648 → 2,057 |
| 3 | 14 (15) | 559 → 762 | 126 (125) | 675 → 2,024 |

Wall-clock and CPU slopes agree within 2 ms per round. Render work adds almost nothing here, and the CPU metric didn't create the result. Compare with `.\run.ps1 -Summarize <csvs> -Metric wall`.
Our larger runs, with more page types and with winapp DevTools, are in the reports linked from https://github.com/microsoft/winappCli/pull/943. In short:
- **The source-info flag** (set by either the env var or a loaded TAP): on pages with many templated controls and theme resources, parsing is 1.3–1.6× slower from the first navigation. Pages with templated controls that use storyboard visual states (CheckBox) get progressively slower: 3.2 s to 10 s per navigation after 100 navigations, flat without diagnostics. Plain panels, theme resources and Buttons don't degrade.
- **Subscribing to visual-tree changes** additionally retains ~250 B per element ever created, and element creation is ~45% slower.
- **Nothing gives it back:** not `UnadviseVisualTreeChange`, not releasing every reference the TAP holds, not resubscribing.

## Leads in microsoft-ui-xaml

- `DebugTool::CreateDiagnostics` (`dxaml/xcp/dxaml/lib/DebugTool.cpp`) sets `RuntimeEnabledFeature::XamlDiagnostics` before the TAP loads. It's cleared only in the `XamlDiagnostics` destructor (`dxaml/xcp/components/xamlDiagnostics/XamlDiagnostics.cpp`), and that object is kept alive.
- `DXamlServices::ShouldStoreSourceInformation()` (`dxaml/xcp/dxaml/lib/DXamlServices.cpp`) returns true for every parse while that flag is on. Otherwise it checks `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO`.
- Diagnostics-only bookkeeping on the source-info paths includes `ResourceGraph::RegisterResourceDependency`, which is pruned only 4 entries per registration (`dxaml/xcp/components/diagnosticsInterop/ResourceGraph.cpp`). Another candidate is `VisualStateManagerDataSource`, which keeps setters "for source info purposes".
- `XamlDiagnostics::UnadviseVisualTreeChange` (`dxaml/xcp/components/xamlDiagnostics/LiveVisualTree.cpp`) only resets the callback, and leaves `m_enabledThreads` and the handle maps in place.

These leads come from reading the public source. The exact structure that grows on every navigation is not confirmed.
