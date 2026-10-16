# Benchmark Improvement Plan: Theme correctness and app identity

> Status: **draft** (2026-10-15). Not implemented.

## Problem Statement

**Observed:** In run906 (270 trials, 3 models × 3 setups × 10 scenarios), GPT-6.1 Sol apps look like real products: a name and tagline, a custom logo, a brand palette and cards. Claude apps (Sonnet, Opus) are stock Fluent templates. Example: GPT bare built "Pulse — A little clarity for your PC" (run906/078); Opus and Sonnet built stock dark task managers (076, 074).

The benchmark barely notices:
- Design is 10 of 100 points, and the judge's design score only moves 6.2 (Sonnet) → 7.1 (Opus) → 7.9 (GPT).
- Nothing checks that colors follow Light/Dark/High Contrast or that the app respects the system theme.
- V5 (no hard-coded colors) only reads page XAML, so hard-coded colors in C# go unnoticed.

**Evidence (static source scan of run906, `run906-theming.csv` / `run906-branding.csv`):**

| Model, setup | Distinct custom colors | Custom colors in Light+Dark+HC theme dictionaries | Sets `RequestedTheme` somewhere¹ | Hard-coded colors in C# (mean) | V5 pass | Custom app icon² | Judge design |
|---|---:|---:|---:|---:|---:|---:|---:|
| GPT, bare | 33 | 20% | 83% | 6.4 | 30% | 17% | 7.7 |
| GPT, published | 14 | 80% | 60% | 2.5 | 83% | 27% | 7.9 |
| GPT, main | 15 | 97% | 57% | 1.8 | 93% | 30% | 7.9 |
| Opus, bare | 1 | 3% | 3% | **9.4** | 60% | 13% | 7.1 |
| Opus, published / main | 1 | 10–17% | 0–3% | 5.3–5.9 | 73–83% | 0–3% | 7.1–7.2 |
| Sonnet, any | <1 | 0–3% | 0% | 2.6–3.1 | 73–83% | 0–27% | 5.9–6.4 |

¹ Includes legitimate theme pickers in settings pages; the check below only flags a theme forced at the app root.
² Icon differs from the template default (162 apps share one default logo). Unpackaged apps without a logo count as "no".

**What it tells us**
- GPT puts real effort into identity. With skills, it moves those colors into proper theme dictionaries (20% → 80–97%) and stops hard-coding them (V5 30% → 83–93%). The skills already teach the *how*, but nothing prompts Claude to do the *what*.
- Hard-coded colors in C# are common in every setup and unmeasured: Opus `bare` averages 9.4 per app, typically chart series and status colors.
- `winapp manifest update-assets <svg>` generates every icon size from one SVG, but only ~23 of 180 skills trials used it (19 GPT, 4 Opus, 0 Sonnet).
- The winui-design skill already says brand color is *expected* ("Overriding `SystemAccentColor` … is how Microsoft's own first-party apps differentiate") and that theme dictionaries must cover Light, Dark **and** HighContrast. There is no analyzer rule for hard-coded colors or forced themes.


## Root Cause

1. **Measurement:** theme handling and identity aren't measured, so improvements to them don't show up in the score.
2. **Agent behavior:** identity is optional in the prompt and the skills. GPT does it by default; Claude does what's asked. Nothing at a higher tier (templates, analyzers, a default workflow step) prompts it.
3. **Tooling gap:** the winappCli analyzer flags many WinUI pitfalls (WUI0001–WUI4103), but not hard-coded color literals or a fixed root theme. Agents only learn about those from skill text.

## Proposed measurement (benchmark side)

### New harness checks (static, deterministic; quality share)

**Q2a. Theme-aware custom colors** (replaces the unimplemented Q2 from the scoring rules)
- `na` when the app defines no custom color resources (it only uses system brushes, which are theme-correct by construction).
- Otherwise **pass** when every `ThemeDictionaries` block has `Light`, `Dark` and `HighContrast` (not `Default`), and every key appears in all three.
- Brand colors defined as plain resources outside `ThemeDictionaries` and used as `{ThemeResource}`/`{StaticResource}` in pages count as **fail**: they don't adapt to Dark or High Contrast.

**Q2b. Follows the system theme**
- **Fail** when the app fixes its theme at the root. That means:
  - `RequestedTheme="Light|Dark"` on `<Application>`;
  - `RequestedTheme = ApplicationTheme.X` in `App` code;
  - a literal `RequestedTheme` on the main window's root element.
- Assignments from a variable or setting (a theme picker) are fine. The scenario can opt out (for example, a deliberately dark media app) with `theme: dark` in its frontmatter.

**V5 (extended). No hard-coded colors in UI code**
- Today: no hex colors in page XAML.
- Add C# literals: `Color.FromArgb`, `ColorHelper.FromArgb`, `Colors.X` other than `Transparent`, and `new SolidColorBrush(<literal>)`.
- Exempt files that define theme resources (`Themes/*`) and up to **3** literals per app for data visualization (chart series), where code is the natural place.
- Open question: is 3 right, or should chart palettes also be theme resources?

**I1. Custom app icon**
- Packaged apps: the manifest's logo images aren't the template default (hash list of known template assets).
- Unpackaged apps: an `ApplicationIcon` that isn't the default.
- `na` for scenarios that say "use the default icon".

**I2. Brand accent as a theme resource**
- **Pass** when the app overrides `SystemAccentColor` (and variants), or defines at least one brand brush in all three theme dictionaries, and uses it from page XAML through `{ThemeResource}`.
- This rewards an identity that's built correctly; it doesn't reward a larger palette.

### Judge: identity becomes explicit
Today the judge scores design 0–10 "relative to an unmodified template". Add a separate **identity 0–10** with anchors:
- **0–2:** template look, default name and icon.
- **5:** a name in the title bar, an accent color, consistent spacing.
- **8–10:** a recognizable product: name/tagline, icon, a brand palette used consistently, a typographic hierarchy, still Fluent (Mica, system controls, readable in both themes).

The judge must also mark identity down when contrast is poor or brand colors fight Fluent conventions. Later, the scorecard's pairwise design comparison (the scoring rules) covers relative polish.

### Not doing: runtime Light/Dark switching
Testing both themes at runtime would mean changing the Windows app theme (`HKCU\...\Personalize\AppsUseLightTheme`). That is a global change, which the harness must avoid, and it would race with concurrent trials. The static checks cover how colors are defined, and the judge sees the app in the machine's theme.

A clean runtime option needs a per-launch theme override from winappCli, for example `winapp run --theme light|dark|contrast`. We'd request that rather than hack it.

### Scoring options (decision needed)

| Option | Requirements | Quality checks | Design | Identity | Notes |
|---|---:|---:|---:|---:|---|
| **A** (minimal) | 60 | 30 (incl. Q2a, Q2b, V5′, I1, I2) | 10 | – | Identity only through I1/I2 in quality, about 2 points each |
| **B** (recommended) | 55 | 30 | 7.5 | 7.5 | Identity becomes a visible part of the score without dwarfing "does it work" |
| **C** | 50 | 30 | 10 | 10 | Strongest pull toward polish; requirements weigh less |

Expected effect on run906 under B (rough, from the tables above):
- GPT gains ~2–3 points vs Claude.
- Skills setups gain ~1–2 points vs `bare` for GPT (theme dictionaries, fewer literals) and ~0 for Claude until the skills prompt identity.

Old runs can be rescored for the static checks (`rescore.ts` re-reads app folders). Identity needs a re-judge (~10–15 credits per trial on the judge model).

## Encouraging it (winappCli side)

| Tier | Change | Where | Why |
|---|---|---|---|
| 1 Tooling | Analyzer rules: **hard-coded color literal** in XAML page attributes (outside resource dictionaries) and in C# UI code; **fixed root `RequestedTheme`** | winappCli analyzer (new WUI rules) | Warnings the agent must address (Q4 already counts WUI warnings); covers every model, not just ones that read the skill |
| 2 Templates | The WinUI templates ship `Themes/Brand.xaml`: `ThemeDictionaries` with Light/Dark/HighContrast, a `SystemAccentColor` override slot and two semantic brand brushes, already merged in `App.xaml`. The template icon becomes an obvious placeholder (e.g. "TODO" glyph) | winui templates (`winapp new`) | Turns "where do brand colors go" into "fill in the slots"; a placeholder icon makes the default look unfinished, not done |
| 3 Skills | winui-design gets a default **identity step**: name + one-line tagline, accent color, SVG logo → `winapp manifest update-assets logo.svg`, title bar with icon and name. The dev workflow lists it before "done" | winui-design, winui-dev-workflow | Lowest reliability alone, but it's the only change that gives Claude the "what"; the tiers above make it cheap and correct |

Order: the benchmark checks first (so we can measure), then the Tier 2 template slot and Tier 3 identity step together (cheap). The Tier 1 analyzer rules are the most work and the most durable.

## Devil's Advocate

- **Redundancy:** V5 already covers page XAML, and V1/V2 cover the title bar and backdrop. Q2a/Q2b and the C# extension are new. I1/I2 overlap slightly with the judge's design score, which is why identity becomes its own judge item instead of folding into design.
- **Agent behavior:** Tier 3 alone will be followed inconsistently. Run906 shows Claude ignores brand guidance the skill already contains. Tiers 1–2 are what make it reliable.
- **Generalization:** the theme checks apply to every WinUI app. Identity applies to "new app" scenarios; improve/convert scenarios should keep the existing app's identity (`identity: keep` in frontmatter → I1/I2 `na`, judge checks consistency instead).
- **Failure modes:**
  - Rewarding identity can produce garish, low-contrast or off-Fluent apps. Mitigated by Q2a/Q2b, V5′ and the judge's contrast and Fluent anchors.
  - The C# literal check can punish legitimate chart palettes; hence the threshold.
  - Static theme checks can't catch a theme dictionary whose Dark colors are unreadable. Only the runtime override (winappCli ask) or the judge can.
- **Cost:** benchmark checks are about half a day plus tests against run906; the judge rubric change is about 1 hour. Template slot plus skill step: about half a day in winappCli. Analyzer rules: 1–2 days in winappCli.

## Prediction Ledger
- **Q2a:** pass rate for GPT with skills ≥ 80% now; for Claude it's mostly `na` (no custom colors) until identity is encouraged. After the template slot + skill step, Claude has custom colors in ≥ 50% of new-app trials, with Q2a ≥ 80%.
- **V5′ (with C#):** Opus `bare` pass rate drops sharply. Skills setups improve once the analyzer rule exists.
- **I1:** custom icon rises from 0–30% to ≥ 60% across models once the skill step and the `update-assets` pointer ship.
- **Disconfirming evidence:**
  - If Claude's identity score doesn't rise after the template + skill change, the guidance isn't being followed and only the analyzer/template tiers will move it.
  - If identity rises but Q2a/V5′ fall, we're rewarding paint, not craft.

## Validation
- Unit tests for each check on fixtures: a theme-correct brand app, a `Default`-key dictionary, a missing HighContrast key, a forced root theme vs a theme picker, C# literals vs a chart palette, a default vs custom icon.
- Run the static checks over all 270 run906 apps and spot-check 10 verdicts against screenshots (e.g. 078 GPT bare, 079 GPT published, 076 Opus, 074 Sonnet).
- Rescore run906 under the chosen option and compare per-model and per-setup deltas with the predictions above.

---
*Plan created using benchmark-plan-review methodology. Post-run retrospective: [after the first run with the new checks]*
