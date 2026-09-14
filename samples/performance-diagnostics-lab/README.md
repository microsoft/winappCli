# WinUI Performance Diagnostics Lab

This packaged WinUI 3 app generates bounded, repeatable behavior for testing
`winapp perf record`. It deliberately does not sample or log its own performance.
The recording tool remains the sole source of timing and resource observations.

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
| `memory-step` | Allocate, touch, hold, and release memory | Private commit rises and may recede |
| `file-io` | Write and read a 64 MB temporary file | Process-attributed I/O increase |
| `normal-exit` | Close the main window | Normal target exit |

The Automation page contains standard WinUI controls with stable Automation IDs
for invoke, toggle, focus, text, and selection event testing.

## Run

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj
```

Run a scenario after the window loads:

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario ui-stall-4000"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario memory-step --memory-mb 128 --duration-ms 3000"
```

Add startup work before the first window, or close after a scenario:

```powershell
winapp run .\PerformanceDiagnosticsLab.csproj --args "--startup-delay-ms 1500"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--startup-cpu-ms 1500"
winapp run .\PerformanceDiagnosticsLab.csproj --args "--scenario baseline --duration-ms 10000 --exit-after"
```

The accepted bounds are 1-60,000 ms for scenario duration, 0-30,000 ms for
startup work, and 1-512 MB for memory allocation.

## Record

```powershell
winapp perf record .\PerformanceDiagnosticsLab.csproj
winapp perf record .\PerformanceDiagnosticsLab.csproj --args "--scenario ui-stall-4000 --exit-after"
```
