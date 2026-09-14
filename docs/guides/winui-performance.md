# Find expensive WinUI layout, scrolling work, and GC context

```powershell
winapp run . --profile .\traces\startup --detach --json
```

This launches your project and starts a bounded native WinUI 3 ETW recording.
Use the returned `Profile.CaptureId` with `winapp perf status` or `winapp perf stop`.
The default recording lasts 30 seconds and allows 128 MiB of raw ETL. Override
these with `--profile-duration-sec` (1-300) and `--profile-max-size-mib` (1-1024).
Without `--detach`, `run` continues waiting for the app after recording finishes.
Stopping a recording never closes the app.
Closing the app ends and finalizes its recording immediately, including with
`--detach`; it does not wait for the duration limit. Status reports `completed`
with stop reason `target-exited`. Retained events remain available for analysis.
The final buffered events or loss counters may be unavailable after process exit,
so coverage can still be partial. Stop explicitly before closing the app when
preserving the final moments is important.

Capture attaches as soon as the launched PID is available. It does **not** capture
guaranteed first-instruction startup. Reactivating an existing packaged process is
labeled attachment to an existing app. Readiness can take a few seconds while
the app initializes. If it times out, let the app finish starting and record its
PID with `perf start` instead. `--no-launch` cannot be combined with
`--profile`. Existing `run` restrictions still apply; for example,
`--debug-output` cannot be combined with `--detach` or `--json`. Debugger pauses
perturb elapsed timings when profiling and debug output are used together.

## Record an existing app

```powershell
winapp perf start --app MyWinUIApp --output .\traces\scroll --json
winapp perf mark CAPTURE_ID --name scenario-start --json
# Navigate and scroll in the app, manually or with verified winapp ui selectors.
# Wait for the intended visible scenario to finish before placing the end marker.
winapp perf mark CAPTURE_ID --name scenario-end --json
winapp perf stop CAPTURE_ID --json
```

Replace `CAPTURE_ID` with the returned `id`, and `MyWinUIApp` with your app.
`start` returns after the worker's control channel and provider enablement are
ready, not after proving that useful events have been recorded. The recorder
does not take the foreground or prevent a separate agent from driving the UI.
Use `winapp ui inspect --app MyWinUIApp --interactive --json` to discover selectors;
see [UI automation](../ui-automation.md) for navigation and scrolling commands.

For attach captures, `--app` (or `-a`) accepts a PID, process name (exact or partial),
or window title, using the same matching rules as `winapp ui`:

```powershell
winapp perf start --app MyWinUIApp --output .\traces\layout
winapp perf start --app "My App Window" --output .\traces\interaction
```

Names and titles resolve once to a process. Recording covers that process, not
only the selected window. If a process name is ambiguous, use a PID or a more
specific window title.

The corresponding limit options are `--duration-sec` and
`--max-size-mib`. A recording also stops when the target exits or its size limit
is reached. `status` reports lifecycle state and final errors; `stop` is repeatable.
If a worker disappears, status reports failure rather than success. A later
`stop` attempts cleanup only when the recorded process and ETW session identities
still match. Unknown loss remains unknown after recovery.

The output directory must be empty. Keep it until you no longer need the evidence,
and move captures only after stopping them.

## Ask narrow questions

```powershell
winapp perf analyze .\traces\scroll --json
winapp perf analyze .\traces\scroll --from-marker scenario-start --to-marker scenario-end --json
winapp perf analyze .\traces\scroll --view elements --type ItemsStackPanel --sort self --json
winapp perf analyze .\traces\scroll --view element --id e70 --depth 2 --json
winapp perf analyze .\traces\scroll --view frames --offset 10 --limit 10 --json
winapp perf analyze .\traces\scroll --view hotspots --min-frame-ms 16.67 --json
winapp perf analyze .\traces\scroll --view events --event v123 --json
```

Replace element and event IDs with IDs from your results.

| View | What it returns |
|---|---|
| `summary` | Recorded phases and element lifetimes ranked by exclusive phase time or element self time |
| `elements` | Element rankings; `--type` filters observed type names, and `--sort` accepts `self`, `inclusive`, or `count` |
| `element` | One trace-local element and, with `--depth`, elements observed beneath it during the selected range |
| `frames` | UI-side frame, render-walk and submission intervals, longest first |
| `hotspots` | Complete `Frame` intervals at or above `--min-frame-ms`, longest first, with dominant direct operations and GC overlap |
| `events` | Original event names, descriptors and selected payload fields, in time order |
| `calls` | Instrumented operations, longest first; `--family layout` restricts the operation family |
| `call` | One operation and its execution subtree, selected with `--id`; default depth 2, maximum 4 |
| `gc` | CLR collection lifetimes and runtime suspension episodes, in time order |

All views accept `--from-ms` and `--to-ms`. `--thread` restricts UI operations or
raw events; it does not apply to `gc`, whose boundaries can cross threads.
Milliseconds are relative
to provider readiness; negative times can occur while the provider set is being
enabled. A marker and a millisecond boundary cannot specify the same range end.
The events view additionally accepts `--provider <guid>`, `--element <id>`, and
`--event <exact-name-or-evidence-id>`.

`hotspots` defaults `--min-frame-ms` to `16.67`. Thresholding uses each complete
frame's full elapsed duration, even when the selected range clips the frame;
`clippedOverlapMs` reports only the selected-range overlap. Each row embeds up to
four longest complete direct child operations with their call IDs, families,
elapsed/exclusive durations, and evidence IDs. Use a child ID with
`--view call --id <call-id>` for deeper expansion. These are instrumented direct
children, not CPU stacks, GPU work, or visual-tree descendants.

JSON queries default to 10 rows and at most 16,384 UTF-8 bytes for the whole
response. Use `nextOffset` with the same immutable query to retrieve the next page.
`--limit` allows 1-100 rows; `--max-bytes` allows 4,096-1,048,576 bytes.
`byteBudgetLimited`, row `projected`, and event `omittedFields` indicate bounded
projections. Narrow the query or increase the budget to retrieve more detail.

Usable partial results are written to stdout with a nonzero exit status and a
structured `partial_data` error on stderr. **Do not discard stdout solely because
the exit code is nonzero.** No usable performance evidence is an error, not a
report that the app is fast.

## Expand an expensive operation

```powershell
winapp perf analyze .\traces\scroll --view calls --family layout
winapp perf analyze .\traces\scroll --view call --id c42 --depth 3
winapp perf analyze .\traces\scroll --view call --id c42 --depth 3 --json
```

Replace `c42` with a call ID from your results. Console output indents child
operations and shows elapsed/exclusive timings, source information where recorded,
boundary status, and evidence IDs. This is an execution tree of instrumented
operations, not a sampled CPU stack or the visual tree.

Without explicit time boundaries, `call` selects the operation's observed start
and end. `clippedOverlapMs` is its overlap with the query range; the full elapsed
duration is kept separately. Missing boundaries remain unknown. When expansion
stops at the depth limit, query the indicated child call to continue.

JSON rows include parent IDs, depth, ancestor IDs, child counts and `depthLimited`.
Follow `nextOffset` to page the same tree without losing its context. A frame
containing a long layout can be expanded into the element operations inside it,
rather than treating the entire frame duration as rendering work.

## Check GC overlap

```powershell
winapp perf analyze .\traces\scroll --view gc --from-ms 500 --to-ms 1500
winapp perf analyze .\traces\scroll --view gc --from-marker scenario-start --to-marker scenario-end --json
winapp perf analyze .\traces\scroll --view gc --sort duration --json
winapp perf analyze .\traces\scroll --view gc --id gc7 --json
```

Use a returned GC interval ID in place of `gc7` to inspect its boundaries and evidence.
New captures request informational CLR GC and suspension events automatically.
No extra flag is needed for either `perf start` or `run --profile`. Allocation
sampling, heap dumps, forced collections, and runtime rundown are not requested.

Collection rows show generation, reason, collection type and start-to-end lifetime.
A **background collection's lifetime is not a pause**. Suspension rows keep the
request, fully-suspended, restart-begin and restart-complete boundaries separately.
The outer duration includes suspension and resumption transitions;
`fullySuspendedMs` covers suspend-complete through restart-begin.
Non-GC reasons, such as debugger suspension, are not attributed to GC.
GC rows are chronological unless `--sort duration` is specified. Duration sorting
ranks complete collection and suspension rows by outer elapsed `durationMs`,
longest first; incomplete intervals with unavailable duration are returned last.
`fullySuspendedMs` remains separately reported and is not the sort key.

Call, frame, and hotspot rows include `gcOverlap` when CLR events were observed. Its duration
is the union of complete GC-related suspension intervals overlapping that
operation and the selected range. It is not additional child work, not CPU time,
and not proof that the native UI thread was blocked throughout that interval.
If GC coverage is partial, the observed overlap is a lower bound, not an exact
total. Use the referenced intervals and their event evidence to investigate.

`gcCoverage` distinguishes observed, unsupported, unavailable, not-observed, and
not-recorded evidence. A failed optional GC provider does not discard usable XAML
data; inspect `providerStates` and its error. A GC query with no recorded CLR events
fails explicitly instead of reporting zero pauses. Native apps may emit no CLR
events, and older captures cannot gain GC events that were never recorded.

Disk/file I/O, managed lock contention, JIT/loading, CPU scheduling and GPU activity
are not included. Physical disk/file tracing requires a separate system-tracing
workflow and its permissions; it is not another event source this private logger
can enable. Logical network or file-request duration, where separately recorded,
also does not by itself prove UI blocking.

## Interpret the evidence

- **Inclusive time** includes nested work. **Exclusive time** subtracts the union
  of direct child operations. **Element self time** subtracts nested element
  work without subtracting the same-object override wrapper twice. Both are
  elapsed time, including waits and interruptions, not CPU execution time.
- Mean, maximum and nearest-rank p95 use complete calls wholly inside the selected
  range. Boundary overlaps are clipped and counted separately. Missing or corrupt
  begin/end boundaries have unavailable durations.
- `layoutBusyMsByThread` is the interval union of observed layout work on each
  thread. It is not CPU utilization; summing threads is not global wall time.
- Frame phases are not presented frames or FPS. Input and scrolling events show
  recorded framework work and state, not proven input-to-display latency.
  Temporal proximity alone does not prove causation.
- Independent compositor scrolling may produce little layout activity. Missing
  layout or realization events do not prove smooth scrolling or broken
  virtualization. CPU sampling, kernel waits and GPU attribution are not captured.
- Element IDs and pointers are valid only within this capture. Source, names and
  parent relationships exist only where emitted; they are not a complete visual
  tree or a mapping to UI Automation IDs. Attaching after creation often leaves
  anonymous elements. Re-navigating during capture can improve metadata coverage.
- `coverage` describes the selected range/thread and capture-wide collection
  constraints. Capture-wide decode/boundary counts remain visible separately;
  an error elsewhere is not automatically an error in the selected interval.
  An unfinished operation may still overlap a later range even when its begin
  event is outside it. Optional GC coverage is reported independently of XAML.

## Files, privacy, and troubleshooting

`capture.json` records identity, runtime provenance, limits, markers, actual ETL
filenames, and stop/loss information. The standard `trace.etl` files are the raw
evidence. Windows tools such as WPA or PerfView may need manifests matching the
recorded WinUI runtime to interpret legacy events.

Offline analysis requires a winapp capture directory, not an arbitrary ETL file.
It creates a disposable `analysis` cache with `manifest.json`, `events.ndjson`,
`calls.ndjson`, `elements.ndjson`, and `gc.ndjson`. Unchanged captures reuse the cache without
decoding ETL again; content fingerprints detect changed inputs. If cache integrity
checks fail, remove **only** that capture's `analysis` directory and analyze again.

Recording uses the target PID and native XAML, XAML diagnostics, XAML operational,
and Controls.Perf providers, plus optional CLR GC events. Controls.Debug, kernel
tracing, CPU sampling, and EventPipe are not enabled. Where Windows rejects a
narrower event-ID filter, PID filtering stays enabled and the corresponding
`providerStates` entry has `eventIdFilterApplied: false`.
That fallback can record additional informational GC or XAML diagnostics data
and increase overhead.

Raw ETL and metadata can contain names, source paths, URIs, resource keys, and
messages. Keep captures local and review them before sharing. A filtered query
does not sanitize the raw files.

| Problem | Action |
|---|---|
| No usable events | Verify the real WinUI app PID, record again, and exercise navigation or scrolling while recording |
| Runtime not observed | Confirm the target loads `Microsoft.UI.Xaml.dll`; a launcher, non-WinUI app, or already-exited process is not a supported target |
| Native access error | Use an app running under your own account and inspect the native error; winapp does not elevate or change tracing permissions |
| Unsupported descriptors or payloads | Keep the ETL and runtime version for investigation; unsupported events are not decoded using an unrelated installed manifest |
| Partial tail after app exit | Recording is finalized normally; analyze the retained events. For more reliable final-moment coverage, stop recording before closing the app |
| Loss or file cap | Use a shorter focused scenario; target-resident private buffers may be lost at exit |
| Analysis resource limit | Capture a smaller scenario; the report marks incomplete evidence rather than truncating the raw ETL |
| GC unavailable or not observed | Inspect `providerStates`, `managedRuntimes` and `gcCoverage`; preserve XAML results, and do not conclude that no GC occurred |
