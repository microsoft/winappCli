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

Cloud PC (Xeon 8370C, 16 cores, no GPU, so WARP rendering), Windows 11 26200, Windows App SDK 1.8.260317003, Visual Studio 18 Enterprise. Two launches per configuration (L1 / L2), 12 rounds per phase. Navigation is the median UI-thread CPU time of one navigation in a round. It's CPU time rather than wall time, because wall time on this shared VM was dominated by other load.

| Config | Attached (read by the app) | Element churn, 40k (ms) | Retained per created element | Navigation, round 1 (ms) | Navigation, mean of last 4 rounds (ms) | **Navigation slope (ms per round)** |
|---|---|---:|---:|---:|---:|---:|
| `off` | – | 720 / 719 | ~0 B | 562 / 562 | 746 / 774 | **17 / 20** (flat; rounds alternate ~500/~1,000) |
| `env` | `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` | 760 / 721 | ~0 B | 609 / 609 | 2,305 / 2,008 | **139 / 146** |
| `tap` | ReproTap, not subscribed | 1,026 / 980 (+39%) | ~0 B | 656 / 641 | 1,988 / 2,074 | **105 / 140** |
| `tap-advise` | ReproTap, subscribed | 1,165 / 1,146 (+60%) | **229 / 228 B** | 703 / 641 | 2,039 / 2,164 | **131 / 152** |
| `vs-on` | VS debugger, `WinUITap.dll`, env var set | **3,779 / 3,646 (×5.1)** | 157 / 101 B | 719 / 734 | 2,285 / 2,304 | **162 / 159** |
| `vs-off` | VS debugger, **env var still set**, no TAP | 850 / 826 (+16%) | ~0 B | 609 / 672 | 2,016 / 2,144 | **128 / 148** |

What the table shows:

1. **Repeated navigation to the same XAML page gets slower over time whenever source info is on**, whether it comes from the env var or a loaded TAP. Without diagnostics the slope is ~20 ms per round (flat within noise). With diagnostics it's 105–162 ms per round, so after 120 navigations a navigation costs 3–3.5× the CPU it did at the start. The subscription doesn't matter: `tap` and `tap-advise` are within launch-to-launch noise of each other and of `env`.
2. **A visual-tree subscription retains memory per element ever created:** ~230 B for a no-op callback, ~100–160 B under Visual Studio's TAP. Without a subscription it's ~0. Element creation is also slower: +39% with a TAP loaded, +60% subscribed, and ×5 with Visual Studio's TAP.
3. **Visual Studio sets `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` for an unpackaged app even with XAML Hot Reload off** (`vs-off`). That's enough for the progressive slowdown, but there's no retention because no TAP is loaded. In our earlier run with a *packaged* app, F5 activated it through its package, the app didn't inherit the variable, and with Hot Reload off it stayed flat.

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
