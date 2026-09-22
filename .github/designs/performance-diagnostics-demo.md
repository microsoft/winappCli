# WinUI 3 Performance Diagnostics Demo Brief

## Purpose

This brief defines the independent business application and two-minute video
needed to demonstrate Winapp performance diagnostics. It intentionally defines
the user-visible symptom and integration contract without prescribing the
defect, implementation, or diagnosis.

The application must be built by an Agent that is separate from the Agent
developing or validating `winapp perf`. The app-building Agent should receive
this brief, but not the recorder's validation results, internal heuristics, or a
suggested root cause.

## Claim to demonstrate

Do not claim that WinUI 3 has no performance tools. Windows Performance
Analyzer, Visual Studio Profiler, PerfView, and other tools already expose
authoritative low-level evidence.

The demonstrated gap is:

> Windows has powerful profilers, but WinUI 3 developers still have to stitch
> them together manually. There is no WinUI 3-first workflow that turns one
> reproduction into a live signal, an automatic explanation, and reusable
> evidence.

The product message is:

> **Record once. Explain where time and resources went. Keep the evidence.**

## Killer scenario

A WinUI 3 business operation freezes for several seconds and then recovers. The
application does not crash, no error is shown, and the operation eventually
succeeds. The developer starts only with a user report such as:

> "Loading quarterly orders makes the app freeze."

This is the demo's single scenario. It is compelling because the symptom is
obvious to the user but does not identify whether the time was spent on the UI
thread, I/O, garbage collection, synchronization, XAML work, or system
scheduling. The next problem is normally choosing and configuring a profiler;
choosing the wrong evidence can require another reproduction.

Northwind Order Desk makes this visible through one ordinary business action:
the user selects **Load quarterly orders**, the activity clock stops for
approximately three to five seconds, and the application then recovers with the
requested results. Startup, resource, managed, and XAML data support this one
story; they must not be presented as unrelated feature demonstrations.

## Contrast to demonstrate

The comparison is not "diagnosis was impossible before Winapp." WPA, PerfView,
Visual Studio Profiler, and other Windows tools already provide authoritative
analysis. The difference is where the investigation starts and how much work is
required before the evidence becomes actionable:

| Without Winapp | With Winapp |
|---|---|
| Begin by deciding which profiler and providers might capture the unknown cause. | Begin by reproducing the user-visible problem with one command. |
| Learn after the run whether the freeze was actually captured. | See the response failure and recovery while they happen. |
| Configure and correlate process, window, resource, XAML, system, and managed evidence separately. | Receive one generation-safe timeline and a unified report from the same recording. |
| Open expert tools before obtaining even an initial explanation. | Immediately see duration, resource behavior, XAML facts, coverage, and loss status. |
| Hand an Agent a symptom description and disconnected traces. | Hand an Agent a structured report, retained raw evidence, and source for evidence-first correlation. |

The transition the video must make visible is:

> **Without Winapp, a UI freeze is a symptom followed by a tool-selection
> problem. With Winapp, the same reproduction becomes an explained,
> Agent-ready evidence package.**

## Narrative structure

This is a product demo of the `winapp perf record` CLI workflow, not a demo of
the Northwind defect. Northwind is the proof case: it supplies one credible,
ambiguous symptom that lets the command demonstrate its full value.

The video follows four acts:

1. **Introduce the product.** Name Winapp Performance Diagnostics and state its
   promise: one CLI command turns a WinUI 3 reproduction into live feedback, an
   automatic report, Agent-ready context, and retained expert evidence.
2. **Establish the need briefly.** Show the Northwind freeze and the misleading
   first intuitions. Explain that WinUI 3 has powerful tools, but a symptom does
   not tell developers which one to configure or how to correlate the results.
3. **Spend the majority of the video on the CLI.** Show the command launching
   the app, identifying its process and windows, reporting failure and recovery
   live, then producing responsiveness, resources, XAML, coverage, and artifact
   results from the same recording.
4. **Show the workflow continuing beyond the terminal.** Give the structured
   bundle to an Agent for source correlation and retain ETL/nettrace for expert
   analysis. A fixed-run badge is optional; live editing is out of scope.

The CLI and its outputs should occupy most of the screen time. Do not reveal
the defective source before the command has established the evidence, and do
not let the setup of the sample application become the subject of the video.

## Independent application assignment

### Product concept

Build a visually credible packaged WinUI 3 desktop application called
**Northwind Order Desk**. It is an offline order-management application used by
an operations team.

The initial screen should look like a real product rather than a diagnostics
sample. It should contain:

- A title bar and navigation for Overview, Orders, Customers, and Reports.
- An Overview page with a few order and revenue summary cards.
- Recent order activity.
- A visible connected/ready state.
- A subtle continuously moving UI element, such as an activity pulse, clock,
  progress animation, or status animation, so a freeze is visible in a video.
- An Orders workflow with a clear action such as **Load quarterly orders**.

Use generated or repository-owned text and vector assets. The demo must not
depend on network access, credentials, external services, personal data, or
copyrighted media.

### Required observable behavior

1. The application launches to Overview and becomes visibly interactive.
2. The user opens Orders and invokes **Load quarterly orders**.
3. The application stops responding to input for approximately three to five
   seconds. The continuously moving UI element must visibly stop.
4. The application recovers without crashing.
5. A populated, plausible order view appears, for example grouped orders,
   totals, regions, statuses, and customers.
6. Repeating the workflow on the same machine produces substantially the same
   symptom.

The performance problem must come from plausible application code, data
processing, framework use, or UI construction. Do not implement the symptom
with `Thread.Sleep`, a delay whose only purpose is to freeze the UI, an empty
busy loop, deliberate process suspension, or a hard-coded diagnostics event.

The app-building Agent owns the implementation choice. This brief does not
specify whether the cause is CPU, I/O, synchronization, XAML materialization,
layout, allocation, or a combination. It must not add in-app timing,
self-diagnosis, profiler markers, log messages naming the cause, or comments
that give the answer away.

### Engineering constraints

- C# packaged WinUI 3 application.
- Builds and runs as `Release | x64`.
- Fully offline and deterministic.
- Checked-in or deterministically generated test data.
- No dependency on the Performance Diagnostics Lab.
- No dependency on `winapp perf` APIs or implementation assemblies.
- No code copied from the recorder or its validation workloads.
- No automatic launch argument that triggers the defect; the video should show
  the user action.
- The app remains open after recovery so its final state can be shown.
- The repository build must not require administrator privileges.

### App-builder handoff

The app-building Agent should initially provide only:

1. The application directory.
2. The build command.
3. The run command.
4. The exact user interaction that reproduces the freeze.
5. Any prerequisite needed to run it.

It should not provide a root-cause explanation to the diagnostics Agent before
the black-box recording is complete.

## Independence protocol

The purpose of the separation is to avoid designing a recorder around a known
answer or writing an application that merely satisfies the recorder's current
heuristics.

Use these phases:

### Phase 1: Black-box recording

The diagnostics Agent may build and launch the application using the handoff
instructions, but must not inspect the implementation of the Orders workflow.
It records the user action and writes an evidence-only observation based on:

- Live terminal events.
- `report.json`.
- `timeline.ndjson`.
- XAML summary and evidence coverage.
- Resource facts.
- Collector status and loss status.

The observation must separate measured facts from possible explanations.

### Phase 2: Agent-assisted source correlation

After the evidence-only observation is saved, a fresh Agent receives:

- The `.winappperf` bundle.
- The application source.
- The reproduction steps.

It should answer:

```text
Analyze this .winappperf bundle together with the application source.
What happened during the freeze, what evidence supports that conclusion,
which statements are measured facts versus inference, and what is the
smallest fix?
```

The Agent must cite concrete report/timeline fields before citing source
locations. It must not claim that low-level evidence proves a unique cause when
it does not. The app-building Agent may reveal its intended ground truth only
after this analysis has been recorded.

### Phase 3: Ground-truth comparison

Compare the evidence-only observation and Agent-assisted diagnosis with the
app-building Agent's intended cause. Record:

- What the recorder established directly.
- What the source-aware Agent inferred correctly.
- What remained uncertain.
- Whether the proposed fix addresses the actual implementation.

Do not modify the application to make a missed diagnosis easier. A miss is a
product finding.

## Demo recording

### Preparation

- Use a clean demo directory that does not expose a personal user path.
- Build the latest `winapp.exe` and Northwind Order Desk before filming.
- Start an elevated PowerShell before screen recording so UAC is not shown.
- Warm package registration once, then close the app.
- Delete any prior `demo.winappperf`.
- Use a 1440p or larger canvas, 18-20 point terminal text, and 100-125% Windows
  display scaling.
- Disable notifications and unrelated background applications.
- Arrange the app and terminal side by side.

The filmed command should be no more complex than:

```powershell
winapp perf record .\NorthwindOrderDesk.csproj `
  --no-build `
  --with-wpr `
  --duration-sec 25 `
  --output .\demo.winappperf
```

After the terminal reports the first responsive window, the presenter opens
Orders and selects **Load quarterly orders**. The duration should leave enough
time for the failure, recovery, and final state. Stop with Ctrl+C if the app
does not exit on its own.

WPR merge and analysis time may be shortened in editing, but the final output
must come from the same recording. Do not substitute fields from another
bundle or fabricate terminal output.

### Opening slides

Use two simple 16:9 slides around the problem shot. Slide one names the product,
then the real application establishes the symptom before slide two explains the
workflow contrast. The slides must remain sparse enough to read immediately.
After slide two, move directly to the real terminal; do not use slides to
explain the recording results.

The ready-to-record standalone deck is
[`performance-diagnostics-demo-slides.html`](performance-diagnostics-demo-slides.html).
Open it directly in Edge, press `F` for browser fullscreen, and use the left and
right arrow keys or Space to change slides. It is fully offline and uses no
external fonts, scripts, or assets.

#### Slide 1 — Product title (0:00-0:06)

```text
Winapp Performance Diagnostics

One command from reproduction to explanation

Startup stages  |  UI responsiveness  |  XAML analysis  |  Agent-ready evidence

Record once. Explain where time and resources went. Keep the evidence.
```

Show a small terminal-style command as the central visual:

```powershell
winapp perf record .\MyApp.csproj --with-wpr
```

Narration:

> "Hi everyone. Today I’m demoing Winapp Performance Diagnostics."

#### Slide 2 — The workflow contrast (0:18-0:30)

Use two horizontal flows rather than a feature list:

```text
WITHOUT WINAPP
Symptom -> Choose a profiler -> Configure collection -> Reproduce ->
Correlate evidence

WITH WINAPP
Reproduce once -> See it live -> Get the report -> Debug with an Agent
                                      |
                ETL -> WPA | nettrace -> PerfView / Visual Studio
                           JSON -> Agents
```

Narration:

> "Today, investigation starts by choosing, configuring, and correlating
> different expert tools. With Winapp, it starts from the user reproduction:
> see the problem live, get one report, and keep the evidence for Agents and
> deeper analysis."

Design guidance:

- Use the same Winapp accent color on the command, the `WITH WINAPP` flow, and
  the transition into the terminal.
- Keep each flow on one line where possible; use icons only if they remain
  readable at video resolution.
- Do not put screenshots of WPA, PerfView, or Visual Studio on the slide. Their
  names are not needed to establish the contrast, and the project must not
  imply that it replaces them.
- Cut from slide one to the Northwind symptom, then use slide two to frame the
  contrast before cutting from `Reproduce once` to the real terminal command.

## Two-minute storyboard

| Time | Picture | Narration |
|---|---|---|
| 0:00-0:06 — Product title | Slide 1: product name and command | "Hi everyone. Today I’m demoing Winapp Performance Diagnostics." |
| 0:06-0:18 — Show the problem | Northwind loads quarterly orders; its activity clock stops, the window freezes, and then results appear. Labels: `Data access?`, `XAML?`, `Managed code?` | "Here, loading quarterly orders freezes this WinUI 3 app. It recovers without an error, and the symptom does not tell us whether the cause is data access, XAML, or managed code." |
| 0:18-0:30 — Explain the dilemma and contrast | Slide 2: `Without Winapp` tool-selection workflow versus `With Winapp` reproduction-first workflow | "Developers normally have to choose, configure, and correlate multiple expert tools. With Winapp, the investigation starts with one reproduction." |
| 0:30-0:40 — Start recording | Run one command; activation, target process, window, and initial responsive state appear | "Let’s capture it with one command. Winapp identifies the launched process and the responsive target window." |
| 0:40-1:00 — Live evidence | Open Orders and select Load quarterly orders with no explanatory pause; keep the terminal prominent as response failure and recovery appear while the clock stops and resumes | "At the moment the UI freezes, Winapp records a response failure for that window. When it recovers, Winapp closes the same interval, with its timing and resource samples aligned." |
| 1:00-1:27 — Automatic report | Jump past collector finalization; highlight the single recovered `4373.2 ms` failure, one-core peak CPU, 4.4 KiB read, the 281 ms longest XAML frame, complete coverage, and zero ETL loss | "The report turns that live signal into one recovered 4.37-second interval. CPU peaked at about one core, read I/O was only 4.4 kilobytes, and the longest XAML frame was 281 milliseconds—far shorter than the freeze." |
| 1:27-1:35 — Standard evidence | Expand the bundle and overlay the compatibility mapping: `system.etl -> WPA`, `managed.nettrace -> PerfView / Visual Studio`, `report.json -> Agents / automation`, and `timeline.ndjson -> raw Winapp observations` | "Winapp does not replace existing profilers or lock the data into a proprietary trace. It keeps a standard ETL for WPA and an EventPipe nettrace for PerfView or Visual Studio." |
| 1:35-1:55 — Agent-ready debugging | Agent reads `report.json`, the retained evidence, and source; show its one-screen evidence-first conclusion | "The same bundle is also structured for an Agent, which can connect the measured evidence to the smallest relevant source path." |
| 1:55-2:00 — Close | Winapp title and tagline | "Record once. Explain where time and resources went. Keep the evidence." |

Use cuts rather than speeding up terminal text. Keep the final report visible
long enough to read the response failure count, longest observed duration, CPU
behavior, XAML status, and evidence paths.

The opening and command shots must establish the contrast visually, not only in
narration. Do not show Winapp as replacing WPA, PerfView, or Visual Studio; show
it removing the up-front tool-selection and evidence-correlation burden while
retaining artifacts for those tools.

If optional before/after verification is shown, it must use two clearly labeled
recordings. Do not present a source edit alone as proof of improvement, and do
not compare values copied from unrelated runs. Omit this comparison entirely
rather than crowding the core investigation or implying an unmeasured result.

## Agent shot

The Agent response must fit on one screen. Its ideal structure is:

```text
Observed
- The target window stopped responding once and recovered.
- The observed failure interval was <duration>.
- CPU, memory, I/O, and XAML facts were <facts from report>.
- Evidence coverage was <coverage>; ETL loss was <status>.

Source correlation
- <smallest relevant source location and behavior>

Smallest fix
- <one or two changes tied to the observed behavior>
```

Do not show a long conversational transcript, implementation details of the
recorder, or the Agent editing code. The point is that the same evidence bundle
can be consumed immediately by a coding Agent.

## Evidence bundle shot

Expand only the canonical surfaces:

```text
demo.winappperf\
|-- report.json
|-- manifest.json
|-- timeline.ndjson
|-- startup\
|   `-- summary.json
|-- summaries\
|   `-- xaml.json
`-- traces\
    |-- system.etl
    `-- managed.nettrace
```

Explain their roles once:

- `report.json`: concise structured interpretation for people, automation, and
  Agents.
- `timeline.ndjson`: original Winapp observations.
- `system.etl`: system and WinUI/XAML evidence for WPA.
- `managed.nettrace`: managed runtime and sampled-stack evidence for PerfView
  or Visual Studio.

## Acceptance criteria

Before filming, one untouched recording must establish:

- The target becomes responsive before the Orders action.
- At least one response failure and subsequent recovery are reported.
- The observed failure interval is approximately three to five seconds.
- `report.json` contains the responsiveness section.
- Resource and XAML sections report factual values or an explicit unavailable
  state.
- Requested WPR collection succeeds.
- ETL loss inspection reports zero lost buffers and zero lost events.
- Managed EventPipe records when the target supports it.
- The bundle retains `report.json`, `timeline.ndjson`, `system.etl`, and
  `managed.nettrace`.
- The evidence-only observation is written before source inspection.
- The source-aware Agent distinguishes evidence from inference.
- The final video is no longer than 120 seconds.

If the independent app produces a different valid performance signature than
expected, preserve it and adapt the narration to the evidence. Do not alter the
app merely to recreate a previously validated lab result.

## Exclusions

The video should not cover:

- Every validation workload.
- `perf scenario` or `perf compare`.
- Bundle schema history.
- EventPipe provider or WPR profile configuration.
- NativeAOT implementation.
- Tool installation or UAC.
- Managed hotspot parser design.
- Presentation evidence that has not been validated.
- A before-and-after code fix unless it fits without displacing the evidence
  and Agent story.
