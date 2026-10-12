---
name: winapp-performance
description: Investigate slow WinUI 3 layout, scrolling and virtualization using native ETW captures, timed operation trees and managed GC suspension context. Use bounded queries for runtime performance questions, not crash dumps or UI correctness.
---

## Workflow

1. Read the [WinUI performance guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/winui-performance.md)
   for capture limits, query options, partial-result handling, privacy and interpretation.
2. Use the existing app PID when possible. Start with
   `winapp perf start --app <pid> --output <empty-directory> --json`.
   `--app` also accepts a process name or window title using the same matching
   rules as `winapp ui`. Use the returned `target.pid` for subsequent UI actions.
   For a new launch, use `winapp run <project> --profile <empty-directory> --detach --json`.
   This records after launch, which is enough for navigation, scrolling and other
   post-launch scenarios. For startup investigations only, add `--profile-mode elevated`
   (one UAC prompt for the recorder, which the user must approve) and check
   `Profile.StartupCoverage`; see the guide's
   [startup workflow](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/winui-performance.md#record-startup-layout).
   Do not add startup delays or replace a generated `Main`.
3. Wait for successful recording readiness. Save the returned capture ID.
4. Mark the beginning with `winapp perf mark <id> --name scenario-start --json`,
   drive a short repeatable scenario with verified UI selectors, wait for its
   visible outcome, then run `winapp perf mark <id> --name scenario-end --json`.
5. Run `winapp perf stop <id> --json` before closing the app.
6. Start with
   `winapp perf analyze <directory> --from-marker scenario-start --to-marker scenario-end --json`
   to compare mutually exclusive primary-UI-thread activity, the hottest parsed
   XAML resources, and the detailed operation ranking. Use `--view parsing` over
   the same range when the parsing preview needs full paging. Then query
   `winapp perf analyze <directory> --view hotspots --from-marker scenario-start
   --to-marker scenario-end --json`; its default threshold is 16.67 ms and each
   row includes dominant direct child operations. Expand a
   returned operation with `--view call --id <call-id> --depth 2 --from-marker
   scenario-start --to-marker scenario-end --json`. Use `--view calls --family
   layout` with the same marker range when the hotspot evidence points to layout.
   Query `--view gc --sort duration` over the same marker range to find the longest
   collection and suspension intervals, then inspect referenced interval IDs. Use
   element and event views for source and evidence drill-down. Follow `nextOffset`;
   do not dump NDJSON or ETL into context.
   For overall resource cost, use `--view resources` over the same marker range;
   see the guide's
   [process counters](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/winui-performance.md#compare-process-resource-cost).
   For app-side composition, query `--view calls --family composition` and
   `--view events --event DCompDeviceCommit` over the same marker range; see
   [composition submission](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/winui-performance.md#separate-xaml-work-from-composition-submission).
7. Report observed cost, supporting event IDs, coverage gaps, and one concrete next
   experiment. Preserve stdout when partial results accompany a nonzero exit code.

## Guardrails

- Use `--profile-mode elevated` only for startup, and tell the user to expect a UAC
  prompt. If they decline it, do not retry; fall back to the default attach mode and
  say that early startup may be missing. Otherwise never elevate, change tracing ACLs,
  or modify app code to make capture work.
- Recording is local only. Do not combine `run --profile` with `--on sandbox`;
  see the guide for the supported launch and recording commands.
- Readiness is not decoded coverage. Missing events are inconclusive.
- For WinUI operations, use elapsed-time terminology; do not claim CPU/GPU attribution, displayed FPS,
  proven input-to-display latency, or complete startup/visual-tree coverage.
- `Unclassified` includes idle, waits, and uninstrumented work. Do not describe it
  as app code or CPU use. Off-thread image decode is not UI-thread occupancy.
- Process CPU counters include all target threads but do not attribute functions,
  modules, or waits. Sampled memory/thread maxima can miss brief spikes; missing
  samples are not zero resource use.
- Trace-local element IDs are not UI Automation selectors.
- Composition calls are elapsed API scopes, not GPU work or display latency.
  DComp device commits are notifications, not timed or displayed frames; check
  `directCompositionProvider` before interpreting missing events.
- Call trees are instrumented operation scopes, not CPU stacks or visual trees.
  Keep exclusive scope time distinct from element self time.
- GC is requested by default but may be unavailable. Check `gcCoverage` separately;
  missing events are not zero pauses. Background GC duration is not suspension time,
  and overlapping runtime suspension is not proof that GC blocked the native UI.
- Keep raw captures local unless the user has reviewed and authorized sharing them.
- Use the UI automation skill to interact with the app and troubleshooting/debug
  output workflows for crashes; neither replaces performance evidence.
