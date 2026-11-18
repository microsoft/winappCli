# Benchmark Improvement Plan: Measure app performance with `winapp perf`

> Status: **proposal with spike results** (2026-10-14). Not implemented.

## Problem Statement

**Observed gap:** The benchmark scores whether an app works (requirements), is built well (quality checks) and looks good (design). It never measures whether the app is **fast**. An app that janks on every page change, keeps the CPU busy while idle, or decodes 2,000 images on the UI thread scores the same as one that doesn't. The skills guide performance topics (x:Bind, virtualization, async loading, Native AOT), but nothing checks whether those choices pay off.
**Sessions affected:** All. No trial has performance data today.
**Frequency:** Unknown. That is the point of Phase 1.
**Impact:** None on scores today. Performance is a release-quality dimension users notice, and `winapp perf` now makes it measurable without elevation.

## Root Cause

There was no unattended, per-app way to collect WinUI UI-thread timings. `winapp perf` (WinApp CLI 0.7.2) adds one: `perf start --app <pid>` attaches a private ETW recorder without elevation, `perf mark` brackets a scripted interaction, and `perf analyze` returns compact JSON. The JSON covers:
- UI-thread activity by category (parsing, layout, render, composition);
- slow frames (`hotspots`) and GC overlap;
- process resources: CPU, private bytes, working set, handles, threads.

## Spike (2026-10-14)

**Setup**
- Four finished run906 apps, rebuilt **Release** and launched with `winapp run -c Release --detach`, 5 repetitions each, while run906 kept the machine busy (concurrency 4).
- Per repetition:
  1. attach `winapp perf start`;
  2. 10 s **idle**;
  3. **navigation**: invoke each `TabItem` (NavigationView/TabView item, up to 6), then the first again;
  4. **scroll**: focus the first list item, then End/Home/End.
- Script: [`perf-spike.ps1`](perf-spike.ps1).

**Results**

| App | Window shown (warm) | Private MB | Working set MB | Handles | Idle CPU ms / 10 s | Nav UI-busy ms | Nav slow frames (≥16.7 ms) | Worst nav frame ms | Scroll UI-busy ms |
|---|---|---|---|---|---|---|---|---|---|
| media-gallery, Opus bare | 1.96–2.35 s | 48.7–49.3 | 146–152 | 972–1030 | 0–113 | 159–188 | 3, 3, 3, 3, 3 | 44–56 | 4–10 (empty list) |
| media-gallery, Opus published | 2.53–4.54 s | 37.3–38.5 | 116–120 | 951–981 | 0–88 | 396–414 | 3–4 | 51–56 | – (no list items) |
| file-explorer, Opus main | 2.48–2.57 s | 61.3–63.3 | 146–155 | 1058–1089 | 0–167 | 14–32 | 0 | – | **43–400** |
| task-manager, Sonnet bare | 1.52–2.14 s | 5.0–5.7 | 21–25 | 235–282 | 0–72 | 0 (no nav items) | 0 | – | – (no list items) |

**What the spike shows**
1. **Memory, handles and threads are very stable** (≤3% spread over 5 runs, even under load) and differ between apps. They are cheap and reliable, but "lower is better" only holds within one framework and deployment choice (AOT, trimming, and self-contained builds move them a lot).
2. **Navigation timings are repeatable** (busy ms spread 2–6%; slow-frame count identical in 4 of 5 runs). Absolute busy time is **not** a quality signal on its own: the published-skills gallery did 2.3× more UI work per navigation because its pages are richer. The user-meaningful number is frame time (jank): the worst frame was 44–56 ms in both galleries, a first-visit page build.
3. **Idle CPU is sampling noise around zero** (1 s samples of 15.6 ms CPU ticks). It is only usable as a busy-loop threshold, e.g. more than 1 s of CPU per 10 s idle.
4. **Scrolling needs controlled data.** The galleries were empty, so there was nothing to scroll. The file explorer lists the real disk, so it varied 10×. Data-heavy scenarios need fixed test data (for example, a folder of 2,000 photos) before scroll and load metrics mean anything.
5. **The generic interaction misses apps without navigation.** Task-manager exposed no tab, navigation or list items, so only idle and resource metrics were collected. Its 5 MB private bytes also look wrong for a WinUI process and need checking: the PID may not be the UI process.
6. **Operational notes**
   - An attach capture is ~58 MB of ETL (unfiltered providers), so the harness keeps only the analysis JSON.
   - `perf analyze` once hit `IO_SharingViolation` on `capture.json` right after `stop`, so analysis needs a short retry.
   - The idle-range `summary` omitted `processResources` on most captures; read them from `--view resources` instead. Markers are QPC values; convert with `readyQpc` and `frequency`.
   - Startup (first frame, XAML parse at launch) needs `--profile-mode elevated`, which shows a UAC prompt, so unattended runs can't record it. Warm launch → window, measured by the harness, ranged 1.5–2.6 s with one 4.5 s outlier.

## Solutions by Tier

### Tier 0: Environment / Harness
A harness **perf stage** after validation. It does a Release launch, an attach capture, fixed idle/navigation/scroll phases with markers, and stores a `perf` block in `results.json`. The scorecard shows perf next to cost and time. This is the only tier that measures anything; the tiers below influence what the agent builds.

### Tier 1: Tooling Enforcement
Not applicable as a first step. A build-time analyzer can't see runtime jank. The existing winappCli analyzer (WUI rules) and Q3 (x:Bind) already cover the static proxies.

### Tier 2: Templates / Scaffolding
Not applicable for measuring. Later, if the data shows common problems (e.g. non-virtualized lists), the template could default to `ItemsRepeater` or virtualized `ListView` patterns.

### Tier 3: Instructions / Skills
Not the fix for measurement. The skills already cover performance, and this plan is what would show whether they work.

## Recommended Solution

**Chosen tier:** 0 (measurement), in three phases, **without changing the headline score until the data justifies it**.

### Phase 1: Report only (next run)
- After validation (the Debug app is closed), launch **Release** with `winapp run <csproj> -c Release --arch <host> -p Platform=<host> --detach --json`, under the trial's cooperative UI workflow, with the package lock held.
- Wait for the window and record **warm launch → window ms**, then settle 2 s.
- `winapp perf start --app <pid> --duration-sec 120 --max-size-mib 1024 --json`, then:
  - `idle` (10 s);
  - `nav`: invoke each `TabItem` up to 6, then the first again; 800 ms apart;
  - `scroll`: focus the first `ListItem`/`TreeItem`/`DataItem`, then End/Home/End;
  - `perf stop`, and close the app.
- Analyze each range with `summary` and `hotspots`, plus `resources` for the idle range. Retry on sharing violations.
- Write to `results.json`:
  ```json
  "perf": {
    "launchToWindowMs": 2349,
    "idle": { "cpuMsPer10s": 74, "privateMB": 49.0, "workingSetMB": 148.4, "handles": 972, "threads": 35 },
    "nav":  { "items": 3, "uiBusyMs": 176, "layoutMs": 0, "parsingMs": 0, "slowFrames": 3, "worstFrameMs": 49.2, "gcPauseMs": 0 },
    "scroll": { "reached": true, "uiBusyMs": 3.9, "slowFrames": 0, "worstFrameMs": 0 },
    "coverage": { "complete": true, "reasons": [] }
  }
  ```
- Delete the ETL after analysis. Keep `capture.json` and the analysis JSON (~50 KB).
- Scorecard: a **Performance** table per setup with medians for warm launch, idle CPU, private MB, nav worst frame, nav slow frames, and the share of trials whose interactions were reached. Paired deltas use the same pairing as scores.
- Cost: about 1.5–2 minutes per trial (Release build ~30 s, capture ~40 s, analysis ~15 s). No model calls.

### Phase 2: Checks (after one Phase 1 run)
Turn into **quality checks** only the metrics that are stable and that users notice, with thresholds set against the template baseline:
- **Q7 Idle CPU:** under 1,000 ms per 10 s idle (catches busy loops and polling timers).
- **Q8 No hard jank in navigation:** no frame over 250 ms (a visible hitch). Frames of 50 ms are normal for a first page visit.

Memory and launch time stay report-only: they depend on the deployment choices the scenario allows. Trials where the interaction reached nothing get `na`, not `pass`.

### Phase 3: Data-heavy perf scripts
Scenarios where performance is part of the task get controlled test data and a `perf_script` in their frontmatter:
- photo-organizer / media-gallery: a 2,000-image folder; open it, scroll to the end, open a photo;
- inventory-manage-app: 10,000 rows; filter and sort;
- file-explorer-shell: a fixed 5,000-file tree.

Only this phase can tell good apps from bad ones on virtualization, async image decoding, and UI-thread I/O, which is where skills guidance should matter most.

### Changes Required (Phase 1)
1. **`runner/perf.ts`** (new): `runPerfStage(csproj, workDir, trialDir, log)`, which handles the Release launch, capture, markers, analysis with retry, ETL cleanup, and returns `PerfResult`; plus pure evaluators with unit tests on recorded analysis JSON.
2. **`runner/benchmark.ts`**: call it after validation in the run and revalidate flows when the trial ran. Skip it when `--no-perf` is passed or `agentConfig.launch_command` is set (non-WinUI). Save `perf` in `results.json`.
3. **`scorecard.ts`**: Performance table and paired deltas.
4. **`headless.ts` / TUI**: `--no-perf` flag.
5. **README**: what is measured, what isn't (startup, GPU, display latency), and the noise caveats.

## Devil's Advocate

### Redundancy Check
Nothing measures runtime performance today. Q3 (x:Bind) and Q4 (warnings) are static proxies. The judge can notice an obviously slow app, but has no timings.

### Agent Behavior Test
Not applicable to Phase 1: it measures, it doesn't instruct. In Phase 2 the agent isn't told about the checks, which only reflect what it built.

### Generalization Test
- **Idle and resources:** apply to every WinUI app.
- **Navigation:** only reaches apps with NavigationView/TabView; it reached 3 of 4 apps in the spike.
- **Scrolling:** only meaningful with controlled data (Phase 3).
- **Other frameworks** (Electron, WPF): process resources still work; the XAML timings don't.

### Failure Mode Analysis
- **Misreading richer UI as slower UI:** busy ms rewards empty pages, so it must never be scored directly. Only frame-time thresholds are proposed as checks.
- **Machine load:** timings in concurrent runs are noisier than this spike's 5-run spread on one app. Paired comparisons and thresholds well above normal (250 ms) limit false failures.
- **Perf stage failures** (capture errors, apps that close) must never fail the trial. They record `coverage.reasons` and continue.
- **Focus:** the Release launch and the interaction take the foreground. They run inside the cooperative UI workflow, like Q0/Q1.
- **Startup is not measured.** An app with a 10 s cold start looks fine. Warm launch → window catches only gross cases.
- **Disk:** without ETL cleanup, 270 trials would add ~15 GB.

### Cost-Benefit
- **Phase 1:** ~1 day to implement and +1.5–2 min per trial, no credits. Benefit: the first performance data for the skills, across every scenario.
- **Phase 2:** ~2 hours once thresholds are chosen.
- **Phase 3:** ~1–2 days for the data sets and scripts. It's the most valuable phase, so it should follow quickly if Phase 1 shows the pipeline works.

## Scenario Playback

```
Session: media-gallery-opus-published-i1 (run906/040), as measured in the spike

WITHOUT this change:
  Validation: 95/100. Nothing records that each first page visit takes a
  ~50 ms frame, or that the gallery has never shown a large folder.
  Impact: unknown performance, no comparison between setups.

WITH Phase 1:
  perf.nav = { items: 3, uiBusyMs: ~405, slowFrames: 4, worstFrameMs: ~53 }
  perf.idle = { cpuMsPer10s: ~50, privateMB: ~38 }
  Scorecard shows published vs bare: −11 MB private, +230 ms nav UI work
  (richer pages), same worst frame. Nothing is scored, but regressions
  become visible.

WITH Phase 3 (2,000-image folder):
  A gallery that decodes thumbnails on the UI thread shows 400+ ms frames
  while scrolling and fails Q8; a virtualized, async gallery passes.
  That is the difference the skills are supposed to make.
```

## Pros, Cons, and Downsides

**Pros**
- First objective performance data. It needs no elevation, no extra model calls, and works on every WinUI app.
- The resource and navigation metrics proved repeatable under real benchmark load.
- It fits the existing pipeline: Release launch like G3, a UI workflow like Q0/Q1, and a scorecard section like cost/time.

**Cons / Downsides**
- +1.5–2 min per trial (~8 h of machine time over 270 trials at concurrency 4; ~2 h wall clock).
- Generic interactions reach only part of the UI; without Phase 3 data, the strongest perf differences (large lists, images) stay invisible.
- No startup measurement unattended.

**Mitigation**
- `--no-perf` for quick runs.
- Phase 3 test data for the data-heavy scenarios.
- Report the share of trials where the interaction reached nothing, so gaps stay visible.

## Prediction Ledger

**Expected:**
- Every successful WinUI trial gets `perf` with `coverage.complete` in ≥90% of trials.
- Idle CPU under 1 s per 10 s in ≥95% of apps.
- Navigation reached in ~60–75% of apps.
- Worst nav frame over 250 ms in under 10%.

**Expected side effects:** +1.5–2 min per trial; a few capture failures recorded as `coverage.reasons`.

**Disconfirming evidence:**
- If the same app measured twice in one run differs by more than 25% in nav worst frame or slow-frame count, timings under concurrent load are too noisy to compare. Fall back to resources and thresholds only.
- If setups never differ in Phase 3, the skills don't influence performance, or the scripts don't exercise it.

## Validation
- Unit tests for the analysis evaluators on recorded `perf analyze` JSON (idle, nav, scroll, partial coverage).
- Run the stage twice on 4 existing apps (the spike apps) and confirm the spread matches the spike.
- One 3-scenario × 3-setup run with Phase 1 on; check `perf` presence, coverage, and the scorecard table.

## Effort
- Phase 1: ~1 day.
- Phase 2: ~2 hours after one run.
- Phase 3: ~1–2 days (test data plus per-scenario scripts).

Phase 1 can start any time; its first data comes from the next run.

---
*Plan created using benchmark-plan-review methodology*
*Post-run retrospective: [to be filled after the first Phase 1 run]*
