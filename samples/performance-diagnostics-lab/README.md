# WinUI Performance Diagnostics Lab

This packaged WinUI 3 app generates bounded, repeatable behavior for testing
`winapp perf record`. It deliberately does not sample or log its own performance.
The recording tool remains the sole source of timing and resource observations.

## Modular startup

The solution contains separate assemblies for the startup pipeline and feature
pages:

```text
PerformanceDiagnosticsLab
  -> PerformanceDiagnosticsLab.Contracts
  -> PerformanceDiagnosticsLab.Startup.Core
  -> PerformanceDiagnosticsLab.Startup.Data
  -> PerformanceDiagnosticsLab.Startup.Search
  -> PerformanceDiagnosticsLab.Page.StartupPayload
  -> PerformanceDiagnosticsLab.Page.Rendering
```

The app loads startup modules by assembly and type name in a deterministic
`Core -> Data -> Search` order. Each module has intentionally named,
non-inlined call-chain boundaries so managed or system profilers can preserve
useful stack topology. StartupPayload is an internal, non-navigable XAML payload
used only to exercise startup materialization. The rendering workload is opened
from the single workload screen and is never preloaded by the startup pipeline.

| Mode | Before first window activation | After first visible window | First feature navigation |
|---|---|---|---|
| `eager` | Core, Data, Search, internal XAML payload | Nothing deferred | Loads Rendering DLL on demand |
| `deferred` | Core | Data, Search, internal XAML payload | Loads Rendering DLL on demand |
| `lazy` | Core | Nothing | Rendering navigation completes startup, then loads Rendering DLL |

Every mode performs the same fixed work: configuration graph construction,
4 MB payload generation and parsing, search indexing, a 750 ms
`KnownStartupHotspot`, and materialization of an internal 500-item XAML payload.
Only the position of that work changes.

## Scenarios

| ID | Behavior | Expected external observation |
|---|---|---|
| `baseline` | Responsive idle period | Stable resources and successful probes |
| `ui-stall-100` | 100 ms UI-thread block | May be shorter than the probe cadence |
| `ui-stall-1000` | 1 second UI-thread block | Short failed-probe interval and recovery |
| `ui-stall-4000` | 4 second UI-thread block | Sustained failed-probe interval and recovery |
| `ui-stall-6000` | 6 second UI-thread block | Long failed-probe interval and recovery |
| `ui-cpu` | Bounded compute work on the UI thread | CPU peak with failed response probes |
| `background-cpu` | Bounded compute work on a worker thread | CPU peak while the window remains responsive |
| `deep-call-stack` | CPU hotspot below ten named, non-inlined calls | Managed profile identifies `KnownDeepCpuHotspot.Execute` as the dominant leaf |
| `memory-step` | Allocate, touch, hold, and release memory | Private commit rises and may recede |
| `file-io` | Write and read a 64 MB temporary file | Process-attributed I/O increase |
| `normal-exit` | Close the main window | Normal target exit |

The rendering workload creates 144 XAML tiles and, on request, changes their size,
position, and opacity on alternating frames for up to 15 seconds. The workload
returns to an idle state automatically and can also be stopped through UI
Automation. It is intentionally isolated from the resource and call-stack
workloads rather than making every workload rendering-heavy.

## Run

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj
```

Run a scenario after the window loads:

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario ui-stall-4000"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario deep-call-stack"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario memory-step --memory-mb 128 --duration-ms 3000"
```

Command-line scenarios wait briefly after the page loads so an external recorder can establish a
responsive baseline. With `--exit-after`, the app also remains open briefly after the workload so
the recorder can observe recovery before the process exits.

Select where the fixed startup pipeline runs, or close after a scenario:

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj --args "--startup-mode eager"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--startup-mode deferred"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--startup-mode lazy"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario baseline --duration-ms 10000 --exit-after"
```

Exit deterministically before startup work creates a window to validate startup-failure
recording. The supplied 32-bit integer is preserved as the process exit code:

```powershell
winapp perf record .\PerformanceDiagnosticsLab.csproj --args "--exit-before-window 23"
```

The accepted bounds are 1-60,000 ms for scenario duration and 1-512 MB for
memory allocation.

## Record

```powershell
winapp perf record .\PerformanceDiagnosticsLab.csproj
winapp perf record .\PerformanceDiagnosticsLab.csproj --args "--scenario ui-stall-4000 --exit-after"
winapp perf record .\PerformanceDiagnosticsLab.csproj --args "--scenario deep-call-stack --duration-ms 10000"
winapp perf record .\PerformanceDiagnosticsLab.csproj --args "--startup-mode eager"
```

Module-load events establish when an assembly first appeared; they do not by
themselves assign all elapsed time to that DLL. Use retained WPR or
the automatic `traces/managed.nettrace` sampled stacks to attribute CPU, loader, I/O, JIT, and wait activity.
