# WinUI 3 App Performance Diagnostics

[简体中文](performance-diagnostics.zh-CN.md)

The first capability in winapp performance diagnostics is a target-scoped recording for WinUI 3 apps.

## Problem

WinUI 3 app performance evidence is split across launch tools, UI Automation, process metrics, crash diagnostics, WPR, PresentMon, and managed or native profilers. These tools do not normally share app identity, target processes and windows, user interactions, or a common recording timeline.

## Proposed experience

```powershell
winapp perf record .\src\MyApp\MyApp.csproj
winapp perf record .\publish
winapp perf record .\publish\MyUnpackagedApp.exe
```

The positional `target` identifies the WinUI 3 app to launch and record. Project and build-output-directory targets use the same detection as `winapp run`; `.` remains a shortcut for an app resolved from the current directory. A bare executable target is a new best-effort mode for directly launchable unpackaged apps. A packaged binary requires registered package/AUMID or manifest context and cannot be launched by treating its EXE as unpackaged. Winapp limits recording to the evidenced app processes and windows. Other app frameworks are outside this design.

The developer uses the app normally or reproduces a suspected issue. During the session, winapp records:

- Startup milestones.
- Target-scoped semantic interactions.
- Window response-probe failures and recovery.
- CPU, memory, I/O, thread, handle, and applicable GDI/USER trends.
- Process exits and crash evidence.
- Optional external traces.

Press Enter or Ctrl+C to finish. Winapp writes one evidence bundle and a factual summary, then identifies the appropriate specialist tool for deeper analysis. A bounded duration or target exit can also end the recording.

```powershell
# Core command
winapp perf record .\src\MyApp\MyApp.csproj

# Optional deep collectors
winapp perf record . --with-wpr
winapp perf record . --with-dotnet-trace
winapp perf record . --with-rendering

# Later commands
winapp perf analyze .\capture.winappperf
winapp perf open .\capture.winappperf --with wpa
```

`record` creates the bundle. `analyze` reads winapp-owned timeline data and emits a deterministic factual summary without interpreting ETL. `open` launches an appropriate external viewer for a retained artifact and never changes the recording.

An attach form may later reuse the existing app-target vocabulary:

```powershell
winapp perf record --app 8420
```

Launch mode can cover startup because recording begins before activation. Attach mode records only events observed after attachment and must report startup coverage as unavailable.

Default recording is unprivileged and low overhead. Deep collectors are explicit:

| Collector | Default | Requirement and cost | Output |
|---|---|---|---|
| Winapp process, window, UIA, and resource observers | On | No administrator requirement; achieved cadence and probe uncertainty are reported | `manifest.json`, `timeline.ndjson` |
| WPR | Off | Requires an elevated terminal for the required system/XAML profiles; higher storage and collection overhead | Original `system.etl` for WPA |
| `dotnet-trace` | Off | Managed WinUI 3 apps only; runtime and sampling overhead | Original `.nettrace` and optional Speedscope conversion |
| PresentMon/WPR rendering | Off | Tool and capability preflight; process-scoped unless stronger attribution is proven | Original CSV or ETL |
| Screenshot or video | Off | Measurement-affecting CPU, GPU, and I/O overhead | Visual evidence from a separate reproduction |

## Representative output

One recording produces one timeline and a bounded factual result:

```text
Performance recording: partial
Target: Contoso.App (package Contoso.App_123), process set: 8420, 9012

Startup
  0.000 s activation requested
  0.184 s first process
  0.912 s first owned top-level window
  1.108 s first visible window
  1.763 s first successful response probe [250 ms cadence]

Interactions
  12.401 s Button "Load Data" invoked [uia-observed]

Responsiveness
  12.750-14.500 s response probes failed; then recovered [250 ms boundary resolution]

Resources
  CPU peak 87%; private commit 412 -> 563 MB; handles 381 -> 404

Collectors
  WPR unavailable: run from an elevated terminal to collect system.etl

Result
  The observed interaction preceded the failed response-probe interval and CPU peak. This records correlation, not a source-level cause.

Evidence
  .\capture.winappperf
  Next: rerun from an elevated terminal with --with-wpr to collect an ETL for WPA
```

The session reports what was requested, what ran, and the strongest result the retained evidence supports. `completed`, `partial`, `attached-late`, `unavailable`, `failed`, and `cancelled` describe collection coverage rather than diagnosis. Events use a monotonic session timeline calibrated to UTC; external artifacts retain their own clock domain when reliable alignment is not possible.

## Existing foundation

- `run`: build, package/register, and launch packaged or unpackaged WinUI 3 apps.
- `ui`: target windows and controls, perform actions, and wait for UI state.
- `ui screenshot` and `ui record`: visual evidence.
- Debug output, crash dumps, ClrMD, DbgEng, and XAML triage: failure evidence.

These produce useful individual results but do not continuously observe and correlate app behavior in one recording.

The existing debug-output path attaches a native debugger and is intentionally not part of default performance recording because debugger attachment changes timing, conflicts with other debuggers, and can affect target lifetime.

## What winapp perf adds

The genuinely new user-visible capabilities are:

- Continuous resource time series.
- Startup milestones.
- Target-scoped UIA interaction recording.
- Window response-probe failures and recovery.
- Process, window, and package ownership.
- Correlation on one monotonic timeline.
- A partial-failure-safe evidence bundle and factual summary.
- Optional external collector lifecycle and handoff.

Existing features are reused as inputs to the recording rather than rebuilt.

Recording starts before launch, remains active during use, and finalizes retained evidence when the user stops it, its timeout expires, the target exits, or a collector partially fails.

## Recording pipeline

| Stage and function | What the user gets in the output | Existing/reuse | Adjustments required | Net-new work | External tool |
|---|---|---|---|---|---|
| Session, startup, and target ownership | Activation, first process, first top-level window, visible, first successful response probe, exit, package/process/HWND scope | `run`, package and AUMID launch, process and window discovery | Handle bare unpackaged EXEs and distinguish a new launch from single-instance redirection to a pre-existing process | Session ID, QPC/QPF clock, UTC calibration, evidenced ownership graph, startup milestones | None |
| Interaction observation | `winapp ui` operations and target UIA events by default; optional manual input correlation only after privacy and overhead validation | `ui` targeting, actions, waits, UI Automation | Reuse UI semantics and isolate every cross-process UIA call with timeout and failure status | UIA event timeline; optional asynchronous input observer and redaction | UI Automation |
| Window/UI state and responsiveness | Per-HWND lifecycle and response-probe intervals with cadence, threshold, and boundary uncertainty | UI targeting and window discovery | Observe rather than assume app-ready state; avoid blocking UIA work against a hung target | Isolated window-state probe and correlation with interactions | Win32 window APIs, UI Automation |
| Baseline resources | CPU, memory, I/O, thread, handle, and applicable GDI/USER samples, trends, and peaks | Process/package context | Record achieved cadence and scope rather than treating one PID as the app | Low-overhead sampler and resource time series | None |
| Exit and crash evidence | Exit disposition and already-available dump/crash artifacts without attaching a debugger by default | Dump, ClrMD, DbgEng, XAML triage | Keep intrusive debugger capture separate and associate artifacts only with the evidenced process set | Partial-result-safe finalization and factual failure correlation | ClrMD, DbgEng |
| Optional deep collectors | Original traces, collector status, clock metadata, and recommended viewer | Launch and target ownership | Preflight elevation/capabilities, use uniquely owned sessions, retain on soft stop, enforce storage limits, and preserve rather than reinterpret output | Adapter lifecycle, loss/quota reporting, and handoff records | WPR; `dotnet-trace` and `dotnet-counters` for managed WinUI 3 apps |
| Optional rendering enrichment | Process-scoped presentation evidence associated with the same session; visual evidence only when requested | Process targeting, Windows Graphics Capture, screenshots/recording | Do not claim per-HWND PresentMon attribution unless independently proven; keep visual-capture overhead separate | Presentation-to-process association and collector metadata | PresentMon, WPR |
| Correlation, summary, and bundle finalization | One readable bundle, unified timeline, factual summary, coverage and next-tool guidance | Existing artifact writers | Summarize only winapp-owned facts; retain external traces unchanged and explicitly report unavailable, late, lossy, partial, or cancelled work | Versioned manifest, typed timeline events, bounded deterministic rules | WPA or other specialist viewer |

Winapp owns session lifecycle, status, correlation, and handoff. Specialist tools own deep trace analysis. Winapp preserves original external-tool output, never silently claims missing coverage, does not stop unrelated machine-wide sessions, and does not terminate a pre-existing or ambiguously owned app instance.

## External-tool integration boundary

Winapp implements the common recording and correlation layer. For runtime, system, or GPU internals, it reuses existing tools instead of rebuilding their collection or analysis engines. The table distinguishes existing reuse, adapters this project must develop, and tools that winapp only recommends or opens for the user.

| Category | Tools | What winapp does |
|---|---|---|
| Existing reuse | `dotnet` CLI; relevant Windows SDK build tools; ClrMD; DbgEng; Windows Graphics Capture; UI Automation | Current code already integrates these. Performance recording connects their applicable output and capabilities to the session and artifact model. |
| New adapters | WPR; PresentMon; `dotnet-trace` and `dotnet-counters` for managed WinUI 3 apps | This project implements availability/elevation checks, uniquely owned start/soft-stop, target and configuration, quotas and event-loss reporting, partial-failure handling, artifact preservation, and manifest status. It does not parse ETL into deep conclusions. |
| Recommend or open only | WPA; PerfView; PIX; GPUView; WinDbg/TTD; ProcMon; Visual Studio Profiler; Application Verifier/GFlags | Winapp does not implement their collectors or analyzers; it prints the appropriate artifact or command, or optionally launches an installed tool. |

## Recording tracks

One recording combines several evidence tracks on the same monotonic session timeline. Each track retains its own coverage and collector status, so an unavailable optional collector does not invalidate evidence captured by the other tracks.

### Startup detail

The default recording starts before activation and records activation requested, first process, first owned top-level window, first visible window, first successful response probe, and target exit. Probe cadence and boundary uncertainty are part of the result.

Cold and warm startup are controlled test conditions, not facts that default user-mode recording can always discover. A result labels them only when the scenario declares the condition or a deep trace provides supporting page-fault and storage evidence. Results with different or unknown conditions are not treated as directly comparable.

Opt-in WPR and WinUI XAML ETW preserve evidence that WPA can use to divide the same startup interval into more detailed phases:

| Area | Detail available in the retained trace |
|---|---|
| Process and activation | Activation request, process creation, initial thread activity, and owned window creation |
| Modules and storage | Module first-load timestamps, order and count; image reads, file I/O, hard faults, and loader-related CPU stacks |
| Managed runtime | For managed WinUI 3 apps: CLR initialization, assembly and module loads, JIT activity, and startup GC events |
| WinUI initialization | WinUI thread initialization, graphics-device creation, and other framework initialization events exposed by the trace |
| Initial UI | Navigation, XAML frame activity, layout passes such as `UpdateLayout`, and control-template work visible in WinUI events or stacks |
| First frame | First observed XAML frame and presentation activity; this does not by itself prove application-defined readiness |
| Deferred work | Module loads, CPU, I/O, resource growth, and UI updates that continue after the first visible or responsive window |

Module loading is not reported as a fabricated single duration for each DLL. A module-load event reliably establishes when a module first appeared, while its cost may be distributed across dependency resolution, image reads, hard faults, initialization code, assembly loading, and JIT. Winapp preserves the ETL and its coverage metadata; WPA owns module, stack, and critical-path analysis.

Application method boundaries such as the exact start and end of `App` construction, `OnLaunched`, or application-defined readiness are reported only when explicit trace events or optional application markers support them. Default output uses the first visible window and first successful response probe rather than claiming a first interactive frame.

### Interaction recording and privacy

Default interaction recording uses two evidence sources that do not require desktop-wide input interception:

- Operations performed by `winapp ui`, for which the target control, UIA pattern, and requested action are already known.
- Semantic UIA events from the target window tree, such as invoke, focus, selection, toggle, and relevant property changes.

These events share the session timeline with window and resource data and can produce facts such as `Button "Load Data" invoked`, `TextBox "Search" focused`, `ToggleSwitch "Dark Mode" toggled`, and `ListItem "Document 1" selected`.

Passive manual input correlation is a separate opt-in capability that must pass a privacy and overhead proof before shipping. If enabled, its input callback immediately records only time, input type, and pointer location after a foreground/target-window check. UIA hit-testing and property access happen asynchronously on an isolated worker with strict timeouts; they never block the input callback. Relative coordinates exist only as a fallback for this optional mode.

| Evidence level | Meaning | Default |
|---|---|---|
| `winapp-controlled` | Winapp UI Automation performed the operation; target, pattern, and requested result are known. | Yes |
| `uia-observed` | A semantic UIA event was observed within the target window tree without recording raw input. | Yes |
| `input-correlated` | Opt-in manual input was associated asynchronously with a UIA element or confirming event. | No |
| `window-only` | Opt-in manual input could only be associated with the target HWND and window-relative coordinates. | No |

Default recording does not observe raw keyboard input. UIA focus and safe semantic state changes may still show that a control was used, but entered text, passwords, tokens, document content, and character counts are not collected. Any future manual-input mode must fail closed when target ownership, foreground focus, observer health, integrity level, or secure-desktop state is uncertain.

### Responsiveness and window state

Winapp tracks the owned top-level windows throughout the recording, including creation, visibility, activation, focus and relevant UIA state changes. A versioned response probe records failed-probe intervals and later recovery together with its cadence, timeout, threshold, and boundary uncertainty.

A failed-probe interval is evidence that the window did not service the selected probe in time; it is not an exact OS-defined hung boundary or a source-level diagnosis. UIA work runs on isolated threads with timeouts so an unresponsive target cannot block recording. If the app has multiple windows, each interval identifies the affected HWND rather than treating the entire process set as uniformly hung.

For deeper analysis, winapp records the HWND-to-thread mapping and relevant time interval in the bundle. WPR can supply sampled native/system CPU stacks and thread scheduling data; `dotnet-trace` can add managed sampled stacks and runtime events. Winapp preserves those traces and opens them in WPA, PerfView, or Speedscope rather than claiming to have performed the trace analysis itself.

| Trace view | What specialist analysis can establish without app markers |
|---|---|
| Running | CPU-consuming call paths on the UI thread, aggregated into a flame-graph-style stack profile |
| Ready | Time when the UI thread could run but was not scheduled, with competing CPU activity and scheduling context |
| Waiting | The stack on which the UI thread entered a wait, the wait interval and reason, and related wake-up, lock, or I/O activity when the trace contains sufficient evidence |
| Slow WinUI frame | WinUI frame and layout intervals correlated with the CPU or wait stacks occurring inside the same time range |

Stack widths and reported hot-path times are sampled CPU or thread-time weights, not exact timings for every method invocation. Operations with explicit ETW start/stop events, such as supported WinUI frame activities, can have measured durations. Arbitrary method-level wall-clock duration requires profiler instrumentation or application markers and is not inferred from samples.

Async continuations, cross-thread work, and cross-process calls can break the apparent call chain. Specialist analysis or a future bounded analyzer may connect only edges supported by runtime transfer events, scheduling evidence, shared activity identifiers, or application markers. Winapp does not fabricate a continuous business call chain across missing evidence.

### Interaction-to-feedback latency

When both endpoints are observable, winapp correlates an interaction with the next UIA state change, successful response probe, XAML frame, or presentation. This provides measurements such as input-to-state-change and input-to-next-presentation without claiming that the first subsequent event was necessarily caused by the interaction.

`winapp-controlled` operations provide the strongest start boundary. A passive UIA event can provide an observed semantic boundary but not the physical input timestamp. Presentation latency is available only when a rendering collector is active and remains process-scoped for multi-window processes unless stronger attribution is proven.

### Resource time series

Throughout the recording, winapp samples CPU, memory, I/O, thread, handle, and applicable GDI/USER metrics for the owned process set. Each sample uses the session's monotonic timeline, allowing the values to be plotted as resource curves and aligned with startup milestones, interactions, and failed response-probe intervals.

Raw samples retain per-process values and an application-level aggregate so helper processes are visible without requiring the user to interpret each process separately. The manifest records the requested and achieved sampling cadence. The factual summary extracts bounded results such as peaks, growth, and sustained high-usage intervals without replacing the original time series.

Optional traces can add finer data without changing the default sampler:

| Area | Optional detail |
|---|---|
| Thread activity | Per-thread CPU and Running/Ready/Waiting intervals |
| Managed memory | GC heap size, allocation rate, GC count and pause duration, and large-object-heap activity |
| Managed runtime | JIT, contention, exceptions, thread-pool and assembly-loader events |
| Storage | File-level reads/writes, latency, hard faults, and related stacks; paths are treated as sensitive data |
| Network | Process-attributed connection and transfer evidence when available; endpoints and payload-related metadata require explicit privacy handling |

The default-recording MVP produces the data required for visualization, but does not require winapp to provide a built-in chart interface.

### Exit and crash evidence

Recording finalization preserves whether the app exited normally, returned a nonzero exit code, crashed, was terminated, or was still running when collection stopped. Existing dumps and WinUI/XAML failure evidence are associated only with the evidenced process set and retain their original artifacts.

The summary can correlate a crash or exit with the immediately preceding interaction, failed response-probe interval, resource state, or trace coverage. It does not claim that temporal proximity proves the root cause, and a collector failure produces a partial recording rather than discarding evidence already captured.

Default recording does not attach `DebugActiveProcess`. Intrusive debug-output or first-chance-exception capture must be a separate measurement-affecting mode that warns about debugger conflicts and target-lifetime behavior.

### Optional rendering diagnostics

For investigations of stutter, dropped presentation, or display latency, winapp can optionally enable PresentMon or WPR to collect presentation events and frame-time series for the target process set. Rendering data uses the same session timeline as interactions, resource activity, and failed response probes. It supports frame-time distributions, long-frame intervals, periods with no observed presentation, and interaction-to-next-presentation timing when the start boundary is known.

Winapp associates presentation data with the evidenced process set and preserves the original trace. PresentMon does not by itself prove which HWND owns a swap chain in a multi-window process, so results remain process-scoped unless a separate mapping has been validated. Detailed frame-pipeline, GPU-scheduling, and driver analysis remains the responsibility of specialist tools such as WPA, PIX, or GPUView.

Rendering measurement is different from screenshot or video capture. PresentMon and WPR observe presentation behavior; visual capture shows what appeared on screen but adds CPU, GPU, and I/O overhead. Visual evidence is therefore off by default and should be collected in a separate reproduction when needed.

## Observation vs instrumentation

The baseline recording requires no app changes. It can observe:

- `winapp ui` operations and semantic interactions exposed through target-scoped UI Automation events.
- Process and window lifecycle, response-probe failures and recovery, and UIA state changes.
- CPU, memory, I/O, thread, handle, and applicable GDI/USER data.
- Exits, existing crash evidence, and optional externally collected runtime, system, or rendering traces.

Some facts cannot be recovered reliably from outside the app and require explicit app markers or spans:

- The business operation represented by an interaction when it is not exposed by the UI, such as `RefreshCustomerCache`.
- The exact start, completion, cancellation, or failure of background and asynchronous work that causes no observable UI transition.
- Internal phase boundaries such as database query, network wait, parsing, model inference, cache update, or data binding.
- A correlation ID that connects one user action across tasks, threads, processes, services, or retries.
- An application-defined ready or completed state when no window, UIA property, or other external signal represents it.
- Domain measurements such as items processed, records loaded, cache hit status, or operation result.

External tools may collect raw network activity, call stacks, GC events, scheduling, or presentation data without app instrumentation. However, assigning that data to a specific business operation still requires an observable external boundary or an app-provided correlation marker.

When source is available, a future agent skill can inspect the WinUI 3 project and, with the user's approval, add optional markers or spans around selected business operations. Winapp remains responsible for recording and correlating those events; the skill only assists with source changes and configuration.

When only a built executable is available, winapp cannot add markers. The same external recording still provides startup, UIA interaction, window responsiveness, resource, exit, and existing crash evidence; optional collectors can add WinUI, CPU-stack, thread-wait, and rendering traces. Symbol quality controls how precisely stacks are named: without matching symbols, native frames may resolve only to a module and address. Missing business names and async correlation are reported as coverage limits rather than treated as collection failure.

App instrumentation is an optional enhancement, not a baseline requirement. Its contract must add internal facts without making application changes necessary for ordinary or binary-only recording.

## Artifact example

```text
capture.winappperf\
  manifest.json                 # session, ownership, summary, clocks, versions, coverage and sensitivity
  timeline.ndjson               # typed startup, interaction, resource, window and exit events
  crashes\                      # optional existing dumps and retained crash evidence
  profiles\managed.speedscope.json # optional converted managed stack profile
  traces\system.etl             # optional original WPR output
  traces\managed.nettrace       # optional original dotnet-trace output
  traces\presentmon.csv         # optional original rendering output
  visual\recording.mp4          # optional separate visual evidence; measurement-affecting
```

The manifest records target and ownership evidence, the factual summary, requested and achieved cadence, probe uncertainty, collector/tool versions, artifact coverage, clock alignment method/error, quotas and event loss, sensitivity metadata, collector status, and recommended viewer. `timeline.ndjson` is one monotonic append-only stream with an event `type`, avoiding cross-file temporal merging.

`analyze` applies bounded deterministic rules only to the manifest and timeline facts owned by winapp. It does not parse ETL or diagnose a source-level root cause. `open` selects the retained external artifact and launches its specialist viewer.

## Delivery stages

| Stage | Deliverable |
|---|---|
| 1. Default recording MVP | From a non-elevated terminal, record evidenced process/window ownership, external startup milestones, controlled/UIA-observed interactions, response-probe intervals, resource trends, exit status, a unified timeline, and a factual summary. |
| 2. Deep diagnostic data | Add explicit WPR and .NET adapters with elevation/capability preflight, unique session ownership, soft stop, quotas, loss reporting, original artifact preservation, and specialist-tool handoff. |
| 3. Repeatable scenarios and comparison | Reuse `winapp ui` selectors, actions, and waits for CI/agent scenarios, with warmup, repetition, and comparison based on explicitly defined metrics. |
| 4. Rendering diagnostics | Add process-scoped PresentMon/WPR data, frame distributions and interaction-to-next-presentation timing; capture visual evidence in a separate run when needed. |
| 5. Optional app semantics | Add an optional marker contract and an agent skill that can assist source-available WinUI 3 apps without making application changes a recording requirement. |

## Risks and validation questions

| Risk or unknown | Delivery impact | Required validation |
|---|---|---|
| App, process, and window ownership | A packaged single-instance app can redirect activation to a pre-existing process; helper processes and PID/HWND reuse can make attribution ambiguous. | Test packaged/unpackaged, new/pre-existing single-instance, multi-process and multi-window WinUI 3 apps; classify redirection as `attached-late` and never terminate ambiguous ownership. |
| Manual input observation and privacy | Desktop-wide hooks see events before filtering and may be blocked or flagged as keylogging behavior. | Keep manual input off by default; prove that unrelated keys never enter queues, logs, dumps or artifacts and that observer loss, elevation and secure desktop fail closed. |
| Responsiveness fidelity | Probe cadence and timeout can miss short stalls or overstate exact hung boundaries; blocking UIA calls can worsen a stall. | Test controlled 100 ms, 1 s, 4 s and 6 s UI-thread stalls, report uncertainty, and isolate UIA calls with timeouts. |
| WPR privilege and ownership | Required WPR profiles need elevation; unnamed stop/cancel can discard evidence or affect another machine-wide session. | Use unique instance names, elevation preflight, soft stop that retains evidence, and crash recovery that cleans only winapp-owned sessions. |
| Storage and trace loss | ETL, dumps and video can exhaust disk; lost events can make derived conclusions invalid. | Define duration/byte quotas, free-space checks, loss counters, truncation status and conclusion suppression for materially incomplete intervals. |
| Symbols and sensitive artifacts | ETL, dumps, paths, endpoints, command lines and private symbols can disclose user or proprietary information; mismatched symbols misname stacks. | Keep private symbols local by default, record binary identity/symbol status, label sensitive artifacts and define a sanitized export path. |
| Version and architecture coverage | WPR profiles, WinUI events, PresentMon availability and payloads vary by Windows, WPT and architecture. | Declare and test the supported Windows/WPT/.NET/PresentMon and x64/ARM64 matrix; discover capabilities and mark unsupported tracks unavailable. |
| Measurement overhead | Sampling, tracing, debugger attachment and visual capture can alter the behavior being measured. | Measure each collector separately, publish achieved cadence/overhead metadata, and keep debugger/video modes outside default recording. |
| Application-internal semantics | Without application markers, winapp cannot name or delimit hidden business operations or fully connect async work. | Keep markers optional; source-available apps may use the agent skill, while binary-only recordings report the semantic coverage limit. |
