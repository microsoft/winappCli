---
name: winapp-performance
description: Record and explain objective Windows app startup, responsiveness, CPU, memory, and I/O evidence with winapp. Use when diagnosing slow startup, UI stalls, resource spikes, repeatable performance scenarios, regressions, WPR/WPA traces, or managed EventPipe traces.
---
## Use the smallest useful capture

Start with:

```powershell
winapp perf record <target>
```

Recording is artifact-first: it does not build by default. For project/solution input, winapp
automatically uses the only viable existing output and prints its configuration, architecture, and
path. If several outputs exist, add `--configuration` and/or `--arch`; if none exists, build first or
rerun with `--build` (`Release` by default). `--no-restore` is valid only with `--build`. A direct
`.exe` target is launched exactly as supplied and needs no configuration or architecture. Existing
build-output folders continue to launch directly.

The command prints user-facing startup and responsiveness milestones on a `T+hh:mm:ss.fff` timeline,
then writes one `.winappperf` directory. `T+` is cumulative time since activation; recovery messages
print the observed unresponsive duration directly. Use `--verbose` when raw PID, HWND, UI-thread, and
response-probe details are needed. Read `report.json` first: it is the canonical post-record report
for startup stages, resources, collector/XAML coverage, and retained evidence. Use `timeline.ndjson`
and the referenced raw artifacts when deeper evidence is needed. Stop an unbounded recording with
Ctrl+C, or let it stop when the target exits.

Use the facts to identify which interval or resource needs deeper investigation. Process exit time
and code are lifecycle facts only; direct crash diagnosis to `winapp run <target> --debug-output`
instead. Do not treat a response-probe timeout as proof of a hang or present correlated CPU, I/O,
memory, module, JIT, or XAML activity as the cause.

## Escalate only when the missing evidence requires it

```powershell
# WinUI 3 XAML intervals plus the original ETL for WPA
winapp perf record <target> --with-wpr --duration-sec 10

# Standard recording automatically retains one managed EventPipe trace when CoreCLR is observed
winapp perf record <target>

# Restore/build Release before recording
winapp perf record <project> --build

# Record an exact executable
winapp perf record .\bin\x64\Release\MyApp.exe

# Repeat a controlled UI workflow and compare compatible sets
winapp perf scenario <scenario.json> <target> --output <name>.winappperfset
winapp perf compare <baseline.winappperfset> <candidate.winappperfset> --json
```

Only `--with-wpr` is an explicitly requested collector. Its failure leaves a partial bundle and
returns nonzero. Managed EventPipe is automatic: a native, pre-existing, or unavailable target
reports its factual managed status without failing standard recording. A recorded managed session
contains sampled stacks and `System.Runtime` counters in `traces/managed.nettrace`; winapp does not
interpret this trace, so open it in PerfView or Visual Studio when its raw events are needed.
For `--with-wpr`, read the `xaml` status and `summaries/xaml.json` first. Report initialization,
the longest interesting Frame/UpdateLayout intervals, and the UI thread as observed facts; never
sum nested XAML intervals or call Region of Interest direct framework cost. Automatic XAML
interpretation needs Windows Performance Toolkit 10.1.26100.1 or newer with `perf_xaml.dll`
enabled. If analysis is unavailable, preserve `traces/system.etl` and direct the user to WPA rather
than treating an absent summary as no XAML activity. Check `wpr.lossStatus`,
`lostBufferCount`, and `lostEventCount` before interpreting XAML intervals; detected loss makes
coverage partial, while `not-inspected` and `inspection-failed` mean zero loss was not established.
For complete command behavior and scenario format, use the canonical
[performance diagnostics reference](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#perf-record-and-perf-open).
