# WinUI 3 Performance Diagnostics

[简体中文](performance-diagnostics.zh-CN.md)

[Two-minute demo brief](performance-diagnostics-demo.md)

## Status

This is a discussion proposal. The current feature branch already contains the
basic recorder, `.winappperf` bundle, startup and resource summaries, optional
collector adapters, repeatable scenarios, comparison, typed window-response
results, and controlled-action boundaries. The simplified default capture and
`--full` contracts below are the proposed product direction, not a statement
that every part is implemented.

## Product definition

> **Winapp turns one WinUI 3 performance reproduction into a bounded,
> time-aligned, machine-readable record: it explains common latency,
> responsiveness, smoothness, and resource symptoms, then preserves the
> original evidence for deeper analysis.**

In short:

> **Record once. Explain where time and resources went. Keep the evidence.**

Winapp is not another general-purpose profiler. It is the WinUI 3-specific
entry point and interpretation layer over proven Windows and .NET diagnostics.

## The gap

WinUI 3 already has powerful low-level diagnostics. WPR/WPA, EventPipe,
Visual Studio, PerfView, PresentMon, and Windows process APIs each expose useful
parts of the picture. The missing piece is a WinUI 3-first workflow that turns
one reproduction into a bounded, correlated, and automatically explainable
performance record.

Today the developer must know before recording whether the problem belongs to
CPU, XAML, GC, I/O, scheduling, or presentation. They may reproduce the same
problem several times with different tools, then manually reconcile:

- Windows App SDK activation and the correct process generation.
- The HWND, its owning UI thread, and the relevant child processes.
- Startup, interaction, XAML, runtime, scheduling, and presentation clocks.
- Late attachment, unsupported environments, event loss, and empty tables.

That work requires specialist knowledge and can invalidate a one-time
reproduction. Winapp owns this coordination; the underlying profilers remain
the authorities for their data.

## User problems

The product is scoped to four WinUI 3 performance questions:

| User symptom | What winapp should explain |
|---|---|
| Startup or first-window latency | Where observed time fell across activation, process startup, first window/response, WinUI/XAML initialization, Frame, and layout |
| UI stalls or slow interactions | Whether the target HWND stopped responding and whether its UI thread was running, waiting, ready-but-unscheduled, or executing a long observed phase |
| Poor smoothness | Whether XAML frame/layout work, CPU scheduling, or validated presentation evidence overlaps the affected interval |
| Unexpected resource use | Which target process/thread showed sustained CPU, memory growth, GC, I/O, handle, or other bounded resource activity |

A short recording may report memory growth; it does not diagnose a memory leak.
Throughput, power, long-duration leak analysis, and arbitrary system-wide
profiling are outside the initial product boundary.

## Product promises

### Record once

The user selects a target and scenario, not a collection technology:

```powershell
# Standard-user, process-scoped recording.
winapp perf record .\src\MyApp\MyApp.csproj

# The same workflow plus bounded system-level evidence.
winapp perf record .\src\MyApp\MyApp.csproj --full
```

Both commands launch the app once and publish one `.winappperf` bundle.
`--full` adds privileged system evidence; it does not require a different
analysis workflow or make the default terminal output larger.

`--with-wpr` is the current explicit system-evidence control. Managed EventPipe
capture starts automatically after a newly observed CoreCLR process generation,
without a global diagnostic tool or required duration.

### Explain automatically

The default output is a concise factual explanation, for example:

```text
First responsive window: 2.43 s

Observed WinUI phases:
  XAML initialization        1.44 s
  Longest interesting Frame 137 ms
  Longest UpdateLayout       96 ms

UI thread: 9340
Trace loss: none
Evidence: startup.winappperf
```

Winapp aligns target identity, HWND, UI thread, controlled actions, XAML
intervals, runtime events, resource samples, and available system evidence on
one timeline. It states what was observed, the affected scope, and whether the
evidence was complete.

It does not turn temporal correlation into a root-cause claim. In particular,
XAML activity can establish initialization, Frame, navigation, and layout
intervals, but cannot by itself identify a specific element, Binding, Template,
or application method as the cause.

### Keep the evidence

The same bundle retains the original ETL, EventPipe, presentation, and
winapp-owned timeline artifacts when collected. A developer, Agent, WPA,
PerfView, or another specialist tool can inspect them without another
reproduction.

This is **one bounded recording with two analysis levels**:

1. Level 1 is the automatic factual summary.
2. Level 2 examines deeper evidence already present in the same bundle.

Agent use is optional. Collection, summary generation, and the bundle contract
must work without an Agent.

The output contract has three surfaces:

1. A live terminal view reports user-facing startup and responsiveness
   milestones on a cumulative monotonic `T+hh:mm:ss.fff` timeline. It prints
   durations directly and hides raw PID, HWND, UI-thread, and probe details
   unless `--verbose` is used. Raw `WindowObserved` events remain evidence-only
   in the default view.
2. A concise terminal result and canonical `report.json` explain the completed
   recording using the same typed startup, resource, XAML, and coverage facts.
3. `timeline.ndjson`, ETL, nettrace, and focused summaries retain the underlying
   evidence referenced by the report.

## WinUI 3-specific value

Generic CPU and memory counters are supporting evidence, not the product
identity. Winapp is useful because it can correlate them with WinUI 3 concepts:

- Windows App SDK activation and packaged or unpackaged targets.
- Generation-safe process and process-tree ownership.
- Win32 HWND discovery, visibility, responsiveness, and owning UI thread.
- WinUI/XAML initialization, Frame, navigation, and `UpdateLayout`.
- Managed/native execution, scheduling, and presentation around the same
  user-visible interval.
- Operations performed through `winapp ui`.

A proposed datum belongs in the default product only if it helps explain
startup, responsiveness, smoothness, or resource use for a WinUI 3 target.

## Recording levels

| Capability | Standard | `--full` |
|---|---:|---:|
| Process generation, startup, window, response, exit lifecycle | Yes | Yes |
| Process/thread resources and sampled thread state | Yes | Yes |
| Controlled `winapp ui` action boundaries | Yes | Yes |
| Managed EventPipe when CoreCLR is observed | Yes | Yes |
| System scheduling, waits, and native stacks | No | Yes |
| Detailed Loader and file I/O | No | Yes |
| WinUI/XAML activity | No | Yes |
| Validated process-scoped presentation evidence | No | Yes |
| Original deep artifacts | When applicable | Yes |

Standard recording must work as a standard user and remain process-scoped.
`--full` is an explicit, elevated, bounded system capture. It must preflight
permissions, conflicting machine-wide sessions, free space, required capture
profiles, event-loss support, and presentation-environment support before
launching the target.

The elevated profile is one purpose-built preset. It must not concatenate
`CPU.Verbose`, `FileIO.Verbose`, `XAMLActivity.Verbose`, and
`DotNET.Verbose` wholesale; that combination has already produced an invalid
1.33 GB trace with 535,446 lost events. The current `WinAppPerf.Verbose`
profile keeps the proven XAML providers and adds only sampled CPU,
CSwitch/ReadyThread, process/thread/image, hard-fault, and File I/O events.
Stackwalking is limited to sampled CPU and ReadyThread; managed runtime evidence
remains in the separate process-scoped EventPipe session.

## Evidence and interpretation contract

Every reported fact includes or inherits:

- PID plus process creation time.
- HWND and owning process generation when applicable.
- TID and observed time range when applicable.
- A monotonic QPC timeline aligned to external artifact clocks.
- Collector start/stop, tool/configuration version, coverage, quota, loss, and
  artifact metadata.

Coverage values distinguish `complete`, `attached-after-activation`, `partial`,
`unavailable`, and `not-observed`. An empty result is never treated as proof of
no activity unless collection and analysis coverage are known to be complete.

Specific interpretation limits:

- An HWND timeout means only that the selected message was not serviced within
  the threshold at that sample.
- EventPipe cannot reconstruct CLR work completed before attachment.
- Module appearance establishes order, not module-load cost.
- Sampled stacks can miss short or inlined work.
- XAML durations can overlap or nest and must not be summed.
- XAML Region of Interest is a plugin-derived analysis envelope, not direct
  framework execution cost.
- Exit time and code are lifecycle facts only. Crash diagnosis belongs to
  `winapp run --debug-output`, optionally with symbols.

## XAML capture and analysis

XAML capture and interpretation have separate dependency contracts:

- Capture uses inbox `%WINDIR%\System32\wpr.exe` with a bounded XAML profile.
- Automatic interpretation resolves a compatible installed Windows Performance
  Toolkit, including `wpaexporter.exe`, `perf_xaml.dll`, and enabled plugin
  registration, from trusted locations.
- Winapp ships a versioned `All Xaml Info` `.wpaProfile`, exports the complete
  hierarchy, filters it to the observed target PID, records the target process
  creation time separately, and writes factual XAML intervals into the bundle.
  WPA's exported table does not expose process creation time, so the bounded
  owned capture limits PID-reuse risk but cannot prove generation matching from
  that CSV alone.
- Winapp does not silently modify machine-wide `perfcore.ini`.
- If compatible analysis tooling is unavailable, winapp retains the ETL and
  reports `analysis-unavailable` with the missing prerequisite and action. It
  must not present an empty XAML summary as “no activity.”

The initial contract uses compatible installed WPT, matching the existing
`perf open` dependency pattern. WPT must not be bundled or downloaded on first
use until a supported standalone package and redistribution terms are proven.
If that later becomes viable, acquisition must pin versions, reuse a cache,
verify integrity and Microsoft signatures, and enforce component compatibility.

## Bundle and failure semantics

```text
capture.winappperf\
  manifest.json
  timeline.ndjson
  summaries\
    startup.json
    resources.json
    xaml.json
  traces\
    managed.nettrace
    system.etl
    presentation.csv
```

`manifest.json` records schema/tool versions, target ownership, clocks,
collector coverage, sensitivity, quotas, event loss, and artifact hashes.
`timeline.ndjson` contains typed winapp-owned events. Deep artifacts retain
their native formats. The bundle is published atomically.

| Situation | Result |
|---|---|
| All required capture tracks complete | `completed`, command exit 0 |
| Target exits normally or nonzero while capture completes | `completed`; target outcome recorded separately |
| Required collector starts and fails | Retain `partial`; command exits nonzero |
| Material loss or quota truncation | Retain `partial`; suppress affected conclusions; command exits nonzero |
| CoreCLR is not observed | `completed`, `managed: not-observed` |
| Optional post-capture analyzer is unavailable | Retain raw evidence and report `analysis-unavailable` |
| `--full` capture preflight fails | Do not launch the target or publish a success-shaped bundle |

## Privacy and safety boundary

Normal summaries do not retain UI text/value content, raw keyboard or pointer
input, screenshots/video, file or network contents, connection strings, or raw
exception messages. Controlled operations retain action type and time, not
private arguments.

Full ETL and stack artifacts may contain paths, command lines, symbols, and
activity from other processes. They are marked sensitive, kept local by
default, and excluded from any future sanitized export unless explicitly
included.

Even `--full` excludes:

- Full managed heaps and object-retention graphs.
- Screenshots, video, and passive manual-input capture.
- UIA content values and network content.
- Unbounded traces.
- Required application instrumentation.

Application markers remain an optional source-assisted workflow for business
phase names, hidden asynchronous boundaries, cross-process correlation IDs, or
application-defined readiness that cannot be observed externally.

## Acceptance targets

These are review targets, not validated product constants:

| Target | Standard | `--full` |
|---|---:|---:|
| Default duration | 30 seconds | 30 seconds |
| 30-second lab bundle | <= 25 MB | <= 250 MB |
| Material event loss | Zero | Zero |
| Added median scenario duration | <= 3% | <= 5% |
| Privilege | Standard user | Elevated |

Longer recordings require explicit duration and proportionally checked free
space. No mode waits indefinitely for Enter.

## Validation appendix

The table records direct observations, not merely API or profile availability.

| Track | Proven evidence | Remaining proof |
|---|---|---|
| Process/window/startup | Real bundles retain PID plus start time, activation, owned HWNDs, visible/responsive milestones, terminal CPU/I/O, and exact exit code; exit-before-window retained code 23 | Short activity between samples is unrecoverable |
| HWND response | Controlled stalls produced 29/29 and 28/28 overlapping timeouts; typed outcomes distinguish responsive, timeout 1460, invalid handle 1400, access denied, and other failures | Short stalls can fall between samples; timeout probes reduce cadence |
| Process/thread resources | UI and worker controls each recorded about one core and correctly identified the hot UI or worker TID; idle and contention controls observed Wait and Ready states | Controlled target-overhead comparison remains |
| Controlled actions | Invoke, Toggle, Select, Expand, value application, and mouse dispatch have exact start/end hooks; failed actions retain failed end | Covers winapp-controlled actions, not passive manual input |
| Managed runtime | One 32-second, 1.74 MB session retained deep sampled stacks plus 864 `System.Runtime` counter events across 27 metrics. Standard recording now writes one in-process `managed.nettrace` after a generation-safe CoreCLR attach; a real 4-second capture contained 12,036 samples, 108 counter events, 1,949 rundown events, and zero parser-reported loss | Target-overhead and broader attachment coverage remain; the AOT CLI retains but does not parse nettrace events |
| System CPU/scheduling | Required kernel providers and controlled running/waiting/ready workload truth are proven | Short elevated target-attributed ETL, volume, loss, and stack-resolution proof remain |
| Loader/file I/O | Provider capability and ImageLoad-versus-rundown interpretation are established | Positive/negative target controls, privacy, volume, and loss remain |
| XAML | Three isolated elevated traces had zero lost buffers/events. Eager: ROI 1,591.6380 ms, WXM 1,441.2038 ms, interesting Frame 137.1286 ms, UpdateLayout 95.9532 ms. Deferred: ROI 447.5037 ms, WXM 316.0144 ms, Frame 122.1670 ms, UpdateLayout 92.1059 ms. Exit-before-window had only Create graphics device 20.4084 ms. The production resolver accepted Microsoft-signed WPAExporter 11.7.395.48728, perf_xaml 10.0.26100.8249, and xperf 10.0.26100.8249; the production analyzer reproduced all three WPA summaries exactly and production `tracestats` parsing reported zero loss | Combined-profile overhead/volume/loss and a broader supported-WPT matrix remain; Weight was zero because sampled CPU was intentionally omitted |
| Presentation | PresentMon provenance and input-disabled command shape are proven | Current non-admin run failed access preflight; local/RDP, idle/stress, visibility, multi-window attribution, and clock alignment remain |

## Remaining implementation gates

1. Measure standard recording overhead against the lab workloads.
2. Validate the embedded bounded `WinAppPerf.Verbose` profile under elevation
   for attribution, volume, zero loss, XAML parity, and overhead.
3. Expand XAML compatibility validation beyond the proven WPT 11.7.395.48728 /
   perf_xaml 10.0.26100.8249 pair.
4. Validate presentation evidence across permissions, visibility, local/RDP,
   stress, multi-window, and clock-alignment controls.
5. Complete privacy and interrupted-collection tests.

Open decisions are limited to:

- Final duration, size, and overhead budgets.
- Whether presentation is required by `--full` or becomes a separately named
  capability where its environment contract cannot be guaranteed.
- Supported Windows, WPT, .NET, architecture, local-console, and RDP versions.
- Whether a future supported WPT distribution permits verified
  download-on-first-use; the initial design uses installed WPT.
