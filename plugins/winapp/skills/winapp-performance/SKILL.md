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
3. Wait for successful recording readiness. Save the returned capture ID.
4. Mark the beginning, drive a short repeatable scenario with verified UI selectors,
   wait for its visible outcome, then mark the end. Use `winapp perf mark <id> --name <unique-name> --json`.
5. Run `winapp perf stop <id> --json` before closing the app.
6. Start frame triage with
   `winapp perf analyze <directory> --view hotspots --json`; its default threshold
   is 16.67 ms and each row includes dominant direct child operations. Expand a
   returned operation with `--view call --id <call-id> --depth 2 --json`. Use
   `--view calls --family layout --json` when the hotspot evidence points to
   layout. Query `--view gc --sort duration --json` over the same range to find the
   longest collection and suspension intervals, then inspect referenced interval
   IDs. Use element and event views for source and evidence drill-down. Follow
   `nextOffset`; do not dump NDJSON or ETL into context.
7. Report observed cost, supporting event IDs, coverage gaps, and one concrete next
   experiment. Preserve stdout when partial results accompany a nonzero exit code.

## Guardrails

- Never elevate, change tracing ACLs, or modify app code to make capture work.
- Readiness is not decoded coverage. Missing events are inconclusive.
- Use elapsed-time terminology; do not claim CPU/GPU attribution, displayed FPS,
  proven input-to-display latency, or complete startup/visual-tree coverage.
- Trace-local element IDs are not UI Automation selectors.
- Call trees are instrumented operation scopes, not CPU stacks or visual trees.
  Keep exclusive scope time distinct from element self time.
- GC is requested by default but may be unavailable. Check `gcCoverage` separately;
  missing events are not zero pauses. Background GC duration is not suspension time,
  and overlapping runtime suspension is not proof that GC blocked the native UI.
- Keep raw captures local unless the user has reviewed and authorized sharing them.
- Use the UI automation skill to interact with the app and troubleshooting/debug
  output workflows for crashes; neither replaces performance evidence.
