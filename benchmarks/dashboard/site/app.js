/* Renders the benchmark dashboard from window.BENCH_DATA (written by build.mjs). */
(function () {
  'use strict';

  const D = window.BENCH_DATA;
  const $ = (id) => document.getElementById(id);
  const REPO = 'https://github.com/microsoft/winappCli';

  // The three arms, then the tasks debugging configurations (not part of the comparison).
  const ARM_INFO = {
    barebones: {
      label: 'barebones',
      short: 'No winapp, no skills',
      desc: 'The agent gets no winapp CLI and no plugin skills. It works from what the model already knows and the standard Windows and .NET tools on the machine.',
    },
    'cli-only': {
      label: 'cli-only',
      short: 'winapp CLI only',
      desc: 'The winapp CLI is on PATH. No plugin skills are installed. Runs made after #1263 also tell the agent in the prompt that winapp is installed.',
    },
    'cli-plus-skills': {
      label: 'cli-plus-skills',
      short: 'winapp CLI + skills',
      desc: 'The winapp CLI is on PATH and the winapp and WinUI plugins are installed, so the agent can load their skills.',
    },
    'winapp-skills': {
      label: 'winapp-skills',
      short: 'Debugging: winapp plugin only',
      debug: true,
      desc: 'tasks debugging configuration: winapp on PATH and only the winapp plugin installed.',
    },
    'winui-skills': {
      label: 'winui-skills',
      short: 'Debugging: WinUI plugin only',
      debug: true,
      desc: 'tasks debugging configuration: winapp on PATH and only the WinUI plugin installed.',
    },
    'full-suite': {
      label: 'full-suite',
      short: 'Debugging: both plugins + winapp agent',
      debug: true,
      desc: 'tasks debugging configuration: winapp on PATH, both plugins installed, and the agent started as the winapp custom agent.',
    },
  };

  const OUTCOME_INFO = {
    valid: 'The agent finished and the checks ran. Pass or fail is a real result.',
    'app-failed': 'The app built but crashed or did not run.',
    'build-failed': 'No app was produced.',
    'timed-out': 'The agent ran out of time.',
    'harness-error': 'Our tooling failed, not the agent. Excluded from pass rates and rerun.',
    'asked-before-admin': 'The agent safely stopped before an admin step. Not counted as a failure.',
  };

  let state = { release: null, tab: 'headline', scenarioMetric: 'passRate' };

  // ---------- formatting ----------
  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  const NA = '<span class="muted" title="No data">n/a</span>';
  function pct(x) { return x == null ? NA : `${Math.round(x * 100)}%`; }
  function num(x, digits = 1) { return x == null ? NA : x.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits }); }
  function big(x) {
    if (x == null) return NA;
    if (x >= 1e6) return `${(x / 1e6).toFixed(1)}M`;
    if (x >= 1e3) return `${(x / 1e3).toFixed(0)}k`;
    return String(Math.round(x));
  }
  function ci(m) { return m.passCi ? `<span class="ci">95% CI ${Math.round(m.passCi[0] * 100)}–${Math.round(m.passCi[1] * 100)}%</span>` : ''; }
  function frac(a, b) { return b ? `${a}/${b}` : NA; }
  function modelLabel(m) {
    if (m === 'all') return 'All models';
    const parts = m.replace(/^claude-/, '').split('-');
    if (parts[0] === 'gpt') return `GPT-${parts.slice(1).map(cap).join(' ')}`;
    return parts.map(cap).join(' ');
  }
  function cap(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }
  function armLabel(a) { return ARM_INFO[a]?.label ?? a; }
  function armShort(a) { return ARM_INFO[a]?.short ?? a; }
  function shortSha(c) { return c.slice(0, 8); }
  function table(caption, head, body, cls = '') {
    return `<div class="table-scroll"><table class="${cls}"><caption>${caption}</caption><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
  }

  // ---------- charts (inline SVG) ----------
  // groups: [{ label, values: [number|null per series] }], series: [label]
  let chartId = 0;
  function barChart({ title, desc, groups, series, max = 1, format = (v) => `${Math.round(v * 100)}%` }) {
    const id = `chart-${++chartId}`;
    const W = 460, H = 220, left = 40, bottom = 34, top = 10;
    const plotW = W - left - 10, plotH = H - top - bottom;
    const gw = plotW / groups.length;
    const bw = Math.min(28, (gw - 16) / series.length);
    let bars = '';
    groups.forEach((g, gi) => {
      const x0 = left + gi * gw + (gw - bw * series.length) / 2;
      g.values.forEach((v, si) => {
        if (v == null) return;
        const h = (Math.min(v, max) / max) * plotH;
        const x = x0 + si * bw, y = top + plotH - h;
        bars += `<rect class="s${si % 4}" x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${(bw - 3).toFixed(1)}" height="${h.toFixed(1)}"><title>${esc(g.label)}, ${esc(series[si])}: ${esc(format(v))}</title></rect>`;
        bars += `<text x="${(x + (bw - 3) / 2).toFixed(1)}" y="${(y - 3).toFixed(1)}" text-anchor="middle">${esc(format(v))}</text>`;
      });
      bars += `<text x="${(left + gi * gw + gw / 2).toFixed(1)}" y="${H - bottom + 16}" text-anchor="middle">${esc(g.label)}</text>`;
    });
    const ticks = [0, 0.5, 1].map((f) => {
      const y = top + plotH - f * plotH;
      return `<line class="axis" x1="${left}" x2="${W - 10}" y1="${y}" y2="${y}"/><text x="${left - 4}" y="${y + 4}" text-anchor="end">${esc(format(f * max))}</text>`;
    }).join('');
    const legend = series.map((s, i) => `<span style="--c: var(--s${i % 4})">${esc(s)}</span>`).join('');
    return `<figure><figcaption id="${id}-t">${esc(title)}</figcaption>
      <svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-labelledby="${id}-t ${id}-d"><desc id="${id}-d">${esc(desc)}</desc>${ticks}${bars}</svg>
      <div class="legend" aria-hidden="true">${legend}</div></figure>`;
  }

  function lineChart({ title, desc, points, series }) {
    const id = `chart-${++chartId}`;
    const W = 460, H = 200, left = 40, bottom = 30, top = 10;
    const plotW = W - left - 20, plotH = H - top - bottom;
    const x = (i) => left + (points.length === 1 ? plotW / 2 : (i / (points.length - 1)) * plotW);
    const y = (v) => top + plotH - v * plotH;
    let lines = '';
    series.forEach((s, si) => {
      const pts = points.map((p, i) => (p.byModel[s] == null ? null : `${x(i).toFixed(1)},${y(p.byModel[s]).toFixed(1)}`)).filter(Boolean);
      lines += `<polyline class="l${si % 4}" fill="none" stroke-width="2" points="${pts.join(' ')}"/>`;
    });
    const labels = points.map((p, i) => `<text x="${x(i)}" y="${H - bottom + 16}" text-anchor="middle">${esc(p.release)}</text>`).join('');
    const ticks = [0, 0.5, 1].map((f) => `<line class="axis" x1="${left}" x2="${W - 20}" y1="${y(f)}" y2="${y(f)}"/><text x="${left - 4}" y="${y(f) + 4}" text-anchor="end">${f * 100}%</text>`).join('');
    const legend = series.map((s, i) => `<span style="--c: var(--s${i % 4})">${esc(modelLabel(s))}</span>`).join('');
    return `<figure><figcaption id="${id}-t">${esc(title)}</figcaption>
      <svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-labelledby="${id}-t ${id}-d"><desc id="${id}-d">${esc(desc)}</desc>${ticks}${lines}${labels}</svg>
      <div class="legend" aria-hidden="true">${legend}</div></figure>`;
  }

  // ---------- helpers on data ----------
  function current() { return D.releases.find((r) => r.release === state.release) ?? D.releases[0]; }
  function cellRows(b, headline, cols) {
    // One row group per arm: "All models" then one row per model.
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', headline[arm].all], ...b.models.map((m) => [m, headline[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${esc(armLabel(arm))}<br><span class="muted">${esc(armShort(arm))}</span></th>` : '';
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th>${cols.map((c) => `<td>${c.cell(v)}</td>`).join('')}</tr>`;
      });
    }
    return body;
  }

  // ---------- panels ----------
  function renderHeadline(r) {
    const b = r.benchmarks.apps;
    if (!b) return `<h2>Headline</h2><p>This release has no <code>apps</code> results.</p>`;
    const cards = b.arms.map((arm) => {
      const m = b.headline[arm].all;
      return `<div class="card"><h3>${esc(armLabel(arm))}</h3><p class="muted">${esc(ARM_INFO[arm]?.desc ?? '')}</p>
        <dl><dt>Valid runs</dt><dd>${m.valid} of ${m.total}</dd>
        <dt>Pass rate</dt><dd>${pct(m.passRate)} (${m.passed}/${m.valid})</dd>
        <dt>Requirements met</dt><dd>${pct(m.reqRate)}</dd>
        <dt>Quality checks met</dt><dd>${pct(m.qualityRate)}</dd>
        <dt>Credits per app</dt><dd>${num(m.creditsMean)}</dd>
        <dt>Minutes per app</dt><dd>${num(m.minutesMean)}</dd></dl></div>`;
    }).join('');

    const base = b.headline.barebones?.all;
    const sanityNote = base?.valid && base.passRate >= D.rules.sanityPassRate
      ? `<div class="callout warn"><p><strong>The pass rate does not separate the arms here.</strong> Without winapp or skills (<code>barebones</code>), agents already pass ${pct(base.passRate)} of valid runs. A pass needs only the required gates, such as the app building and launching. Compare the arms on requirements met and quality checks instead.</p></div>`
      : '';

    const series = b.models.map(modelLabel);
    const chart = (title, key, desc, opts = {}) => barChart({
      title, desc, series, ...opts,
      groups: b.arms.map((arm) => ({ label: armLabel(arm), values: b.models.map((m) => b.headline[arm].byModel[m][key]) })),
    });
    const maxCredits = Math.max(1, ...b.arms.flatMap((a) => b.models.map((m) => b.headline[a].byModel[m].creditsMean ?? 0)));
    const charts = `<div class="charts">
      ${chart('Requirements met, by arm and model', 'reqRate', 'Share of judge-checked scenario requirements met in valid runs. The table below has the same numbers.')}
      ${chart('Quality checks met, by arm and model', 'qualityRate', 'Share of quality checks met in valid runs. The table below has the same numbers.')}
      ${chart('Credits per app, by arm and model', 'creditsMean', 'Mean AI credits per valid run. The table below has the same numbers.', { max: maxCredits, format: (v) => v.toFixed(0) })}
    </div>`;

    const cols = [
      { h: 'Valid of total', cell: (m) => `${m.valid} of ${m.total}` },
      { h: 'Pass rate', cell: (m) => `${pct(m.passRate)}${ci(m)}` },
      { h: 'Quality checks', cell: (m) => `<span title="${m.qualityMet} of ${m.qualityTotal} checks">${pct(m.qualityRate)}</span>` },
      { h: 'Requirements', cell: (m) => `<span title="${m.reqMet} of ${m.reqTotal} requirements">${pct(m.reqRate)}</span>` },
      { h: 'Design score (0–10)', cell: (m) => num(m.designMean) },
      { h: 'Credits per app', cell: (m) => num(m.creditsMean) },
      { h: 'Minutes per app', cell: (m) => num(m.minutesMean) },
      { h: 'Tokens per app', cell: (m) => big(m.tokensMean) },
    ];
    const head = `<tr><th scope="col" class="text">Arm</th><th scope="col" class="text">Model</th>${cols.map((c) => `<th scope="col">${c.h}</th>`).join('')}</tr>`;

    const t = D.trend.apps;
    const trendHtml = t.length >= 2
      ? lineChart({ title: 'Pass rate with winapp CLI + skills, by release', desc: 'Pass rate on the cli-plus-skills arm for each published release, one line per model.', points: t, series: b.models })
      : `<p class="muted">A release-by-release trend appears here once two releases are published. Development (<code>dev</code>) runs are never part of the trend.</p>`;

    return `<h2>Whole-plugin headline: full app builds</h2>
      <p>In the <code>apps</code> benchmark an agent builds a complete WinUI app from a written brief. This release has ${b.scenarios.length} scenarios, ${b.arms.length} arms and ${b.models.length} models (${b.models.map(modelLabel).join(', ')}), ${b.trialCount} runs in total. Rates and averages count valid runs only; other outcomes are on the Outcomes tab.</p>
      <div class="cards">${cards}</div>
      ${sanityNote}
      ${charts}
      ${table('Results by arm and model (valid runs only)', head, cellRows(b, b.headline, cols))}
      <p class="muted">Pass rate: share of valid runs where every required check passed. Quality checks: harness and judge quality checks, such as accessible names and no hard-coded font sizes. Requirements: the scenario's features, checked by the judge. Design score: the judge's 0–10 rating, shown as detail only. Credits: AI credits Copilot CLI reported.</p>
      <h3>Trend</h3>${trendHtml}`;
  }

  const SCENARIO_METRICS = {
    passRate: { label: 'Pass rate', cell: (m) => (m.valid ? `<span title="${m.passed} of ${m.valid} valid runs passed">${pct(m.passRate)}</span>` : NA) },
    reqRate: { label: 'Requirements met', cell: (m) => `<span title="${m.reqMet} of ${m.reqTotal}">${pct(m.reqRate)}</span>` },
    qualityRate: { label: 'Quality checks met', cell: (m) => `<span title="${m.qualityMet} of ${m.qualityTotal}">${pct(m.qualityRate)}</span>` },
    designMean: { label: 'Design score', cell: (m) => num(m.designMean) },
    creditsMean: { label: 'Credits per run', cell: (m) => num(m.creditsMean) },
    minutesMean: { label: 'Minutes per run', cell: (m) => num(m.minutesMean) },
    valid: { label: 'Valid of total', cell: (m) => `${m.valid}/${m.total}` },
  };

  function scenarioMatrix(b, metricKey, { noteFor, caption, rowHeader = 'Scenario' }) {
    const metric = SCENARIO_METRICS[metricKey];
    const cols = ['all', ...b.models];
    const head = `<tr><th scope="col" rowspan="2" class="text">${rowHeader}</th>${b.arms.map((a) => `<th scope="colgroup" colspan="${cols.length}">${esc(armLabel(a))}</th>`).join('')}<th scope="col" rowspan="2" class="text">Flags</th></tr>
      <tr>${b.arms.map(() => cols.map((m) => `<th scope="col">${esc(m === 'all' ? 'All' : modelLabel(m))}</th>`).join('')).join('')}</tr>`;
    const body = b.perScenario.map((s) => {
      const flagged = new Set(s.flags.filter((f) => f.model).map((f) => `${f.arm}|${f.model}`));
      const cells = b.arms.map((a) => cols.map((m) => {
        const v = m === 'all' ? s.cells[a].all : s.cells[a].byModel[m];
        const mark = flagged.has(`${a}|${m}`) ? ' <span class="flag-mark" aria-label="flagged">!</span>' : '';
        return `<td>${metric.cell(v)}${mark}</td>`;
      }).join('')).join('');
      const notes = noteFor(s);
      return `<tr><th scope="row" class="text">${esc(s.scenario)}</th>${cells}<td class="flags">${notes}</td></tr>`;
    }).join('');
    return table(caption, head, body);
  }

  function flagList(s, sanityText) {
    const items = s.flags.map((f) => (f.kind === 'sanity' ? `<li><span class="tag">${esc(sanityText)}</span> ${esc(f.text)}</li>` : `<li>${esc(f.text)}</li>`));
    return items.length ? `<ul>${items.join('')}</ul>` : '<span class="muted">None</span>';
  }

  function renderScenarios(r) {
    const b = r.benchmarks.apps;
    if (!b) return `<h2>Scenarios</h2><p>This release has no <code>apps</code> results.</p>`;
    const options = Object.entries(SCENARIO_METRICS).map(([k, v]) => `<option value="${k}"${k === state.scenarioMetric ? ' selected' : ''}>${esc(v.label)}</option>`).join('');
    return `<h2>Per scenario: full app builds</h2>
      <p>Each cell is one scenario, arm and model, over valid runs only. Hover a value for the counts behind it. A red <span class="flag-mark">!</span> marks a cell named in the Flags column.</p>
      <div class="inline-control"><label for="scenario-metric">Show</label><select id="scenario-metric">${options}</select></div>
      ${scenarioMatrix(b, state.scenarioMetric, { caption: `${SCENARIO_METRICS[state.scenarioMetric].label} by scenario, arm and model`, noteFor: (s) => flagList(s, 'Sanity check') })}
      <p class="muted">How flags are set: a model is flagged when its pass rate on an arm differs from the other models' by ${Math.round(D.rules.modelSplitPoints * 100)} points or more, or when it uses more than ${D.rules.costMultiple}x the credits it used on the other arms of the same scenario. "Sanity check" means the <code>barebones</code> arm passes ${Math.round(D.rules.sanityPassRate * 100)}% or more of valid runs (#1253). Flags point to where to look; they are not results.</p>`;
  }

  function renderOutcomes(r) {
    let html = `<h2>Outcomes</h2><p>Every run ends with one outcome. Only <code>valid</code> runs count toward pass rates, so the other outcomes are listed here in full. Outcomes are shown as the harness recorded them.</p>
      <dl class="defs">${Object.entries(OUTCOME_INFO).map(([k, v]) => `<dt><code>${esc(k)}</code></dt><dd>${esc(v)}</dd>`).join('')}</dl>`;
    for (const [name, b] of Object.entries(r.benchmarks)) {
      const outs = b.outcomeNames;
      const head = `<tr><th scope="col" class="text">Arm</th><th scope="col" class="text">Model</th>${outs.map((o) => `<th scope="col"><code>${esc(o)}</code></th>`).join('')}<th scope="col">Total</th><th scope="col">Credits spent, all runs</th></tr>`;
      const cols = [...outs.map((o) => ({ cell: (m) => m.outcomes[o] ?? 0 })), { cell: (m) => m.total }, { cell: (m) => num(m.creditsSpent, 0) }];
      html += `<h3><code>${esc(name)}</code></h3>` + table(`Outcomes for ${esc(name)} by arm and model`, head, cellRows(b, b.headline, cols));
      if (b.notValid.length) {
        const rows = b.notValid.map((n) => `<tr><th scope="row" class="text">${esc(n.scenario)}</th><td class="text">${esc(armLabel(n.arm))}</td><td class="text">${esc(modelLabel(n.model))}</td><td>${n.trial}</td><td class="text"><code>${esc(n.outcome)}</code></td><td class="text">${n.error ? esc(n.error) : '<span class="muted">None recorded</span>'}</td><td>${num(n.credits)}</td><td>${num(n.minutes)}</td></tr>`).join('');
        html += table(`Runs that are not valid (${b.notValid.length} of ${b.trialCount})`, '<tr><th scope="col" class="text">Scenario</th><th scope="col" class="text">Arm</th><th scope="col" class="text">Model</th><th scope="col">Run</th><th scope="col" class="text">Outcome</th><th scope="col" class="text">Error</th><th scope="col">Credits</th><th scope="col">Minutes</th></tr>', rows);
      } else {
        html += `<p>All ${b.trialCount} runs are valid.</p>`;
      }
      if (name === 'apps' && b.launchLevelNames.length) {
        const lv = b.launchLevelNames;
        const lhead = `<tr><th scope="col" class="text">Arm</th><th scope="col" class="text">Model</th>${lv.map((l) => `<th scope="col">${l === 'not measured' ? 'Not measured' : `Level ${esc(l)}`}</th>`).join('')}</tr>`;
        const lcols = lv.map((l) => ({ cell: (m) => m.launchLevels[l] ?? 0 }));
        html += `<h3>How far the app launched (<code>apps</code>)</h3>
          <p>Launch level, all runs: 0 did not run, 1 a window exists, 2 controls respond to input, 3 the scenario's main screen appeared. "Not measured" means the app ran but level 2 could not be checked, for example because UI input could not be sent.</p>`
          + table('Launch level by arm and model (all runs)', lhead, cellRows(b, b.headline, lcols));
      }
    }
    return html;
  }

  function usageTable(r, field, caption, nameHeader, withClaim) {
    const benches = Object.entries(r.benchmarks);
    const names = new Set();
    for (const [, b] of benches) for (const a of b.arms) Object.keys(b[field][a].names).forEach((n) => names.add(n));
    if (!names.size) return `<p>No ${field === 'skills' ? 'skills were' : 'winapp commands were'} recorded in this release.</p>`;
    const cols = benches.flatMap(([bn, b]) => b.arms.map((a) => ({ bn, b, a })));
    const total = (n) => cols.reduce((s, c) => s + (c.b[field][c.a].names[n]?.trials ?? 0), 0);
    const sorted = [...names].sort((x, y) => total(y) - total(x) || x.localeCompare(y));
    const head = `<tr><th scope="col" rowspan="2" class="text">${nameHeader}</th>${benches.map(([bn, b]) => `<th scope="colgroup" colspan="${b.arms.length}"><code>${esc(bn)}</code></th>`).join('')}${withClaim ? '<th scope="col" rowspan="2" class="text">Claim</th>' : ''}</tr>
      <tr>${cols.map((c) => `<th scope="col">${esc(armLabel(c.a))}</th>`).join('')}</tr>`;
    const cell = (c, n) => {
      const u = c.b[field][c.a];
      if (!u.recorded) return `<td><span class="muted" title="Not recorded for this arm">not recorded</span></td>`;
      const v = u.names[n];
      if (!v) return `<td><span class="muted">0 of ${u.recorded}</span></td>`;
      return `<td title="${v.uses} loads or calls in total">${v.trials} of ${u.recorded} <span class="ci">${Math.round((v.trials / u.recorded) * 100)}% of runs</span></td>`;
    };
    const body = sorted.map((n) => `<tr><th scope="row" class="text"><code>${esc(n)}</code></th>${cols.map((c) => cell(c, n)).join('')}${withClaim ? '<td class="text"><span class="tag info">Not enough data</span></td>' : ''}</tr>`).join('');
    const coverage = cols.filter((c) => c.b[field][c.a].recorded && c.b[field][c.a].recorded < c.b[field][c.a].trials)
      .map((c) => `<code>${esc(c.bn)}</code> ${esc(armLabel(c.a))}: recorded in ${c.b[field][c.a].recorded} of ${c.b[field][c.a].trials} runs`);
    return table(caption, head, body) + (coverage.length ? `<p class="muted">Partly recorded: ${coverage.join('; ')}. The other runs did not report this, so they are left out of the count.</p>` : '');
  }

  function renderSkills(r) {
    return `<h2>Skills</h2>
      <p>Which plugin skills the agent loaded, and in how many runs (all outcomes). In the three arms, skills can only load on <code>cli-plus-skills</code>; the other arms are shown so that a skill loading where it should not would be visible.</p>
      <div class="callout"><p><strong>Claims are not published yet.</strong> Each skill will get one automatic sentence (Helps, Helps but costs more, No measurable difference, Not enough data) from fixed rules that are still being decided in <a href="${REPO}/issues/1182">#1182</a>. Until those rules are agreed, every skill shows "Not enough data".</p></div>
      ${usageTable(r, 'skills', 'Skills loaded, by benchmark and arm', 'Skill', true)}
      <h3>winapp commands</h3>
      <p>Which <code>winapp</code> commands the agent ran, and in how many runs. A command that appears on <code>barebones</code> means the arm did not fully hide winapp. Runs made before <a href="${REPO}/pull/1263">#1263</a> did not tell <code>cli-only</code> agents that winapp was installed, so they may not have used it.</p>
      ${usageTable(r, 'winappCommands', 'winapp commands run, by benchmark and arm', 'Command', false)}`;
  }

  function renderTasks(r) {
    const b = r.benchmarks.tasks;
    if (!b) return `<h2>Tasks</h2><p>This release has no <code>tasks</code> results.</p>`;
    const sanity = b.sanityScenarios;
    const cols = [
      { h: 'Valid of total', cell: (m) => `${m.valid} of ${m.total}` },
      { h: 'Passed', cell: (m) => frac(m.passed, m.valid) },
      { h: 'Pass rate, all tasks', cell: (m) => `${pct(m.passRate)}${ci(m)}` },
      { h: 'Asked before admin', cell: (m) => m.askedBeforeAdmin },
      { h: 'Credits per run', cell: (m) => num(m.creditsMean) },
      { h: 'Minutes per run', cell: (m) => num(m.minutesMean, 2) },
    ];
    const excl = (arm, model) => (model === 'all' ? b.headlineExcludingSanity[arm].all : b.headlineExcludingSanity[arm].byModel[model]);
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', b.headline[arm].all], ...b.models.map((m) => [m, b.headline[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${esc(armLabel(arm))}<br><span class="muted">${esc(armShort(arm))}</span></th>` : '';
        const e = excl(arm, m);
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th>${cols.map((c) => `<td>${c.cell(v)}</td>`).join('')}<td>${e.valid ? `${pct(e.passRate)} <span class="ci">${e.passed}/${e.valid}</span>` : NA}</td></tr>`;
      });
    }
    const head = `<tr><th scope="col" class="text">Arm</th><th scope="col" class="text">Model</th>${cols.map((c) => `<th scope="col">${c.h}</th>`).join('')}<th scope="col">Headline pass rate (sanity checks left out)</th></tr>`;
    const sanityNote = sanity.length
      ? `<div class="callout warn"><p><strong>Sanity checks: ${sanity.length} of ${b.scenarios.length} tasks.</strong> The <code>barebones</code> arm passes ${Math.round(D.rules.sanityPassRate * 100)}% or more of their valid runs, so they can't show whether winapp or the skills help. They are left out of the headline pass rate (rule from <a href="${REPO}/issues/1253">#1253</a>). The table shows the pass rate both with and without them.</p></div>`
      : '';
    return `<h2>Tasks: small scripted jobs</h2>
      <p>In the <code>tasks</code> benchmark an agent does one packaging, signing or UI job in Windows Sandbox, and a script checks the result. This release has ${b.scenarios.length} task(s), ${b.trialCount} runs.</p>
      ${sanityNote}
      ${table('Tasks results by arm and model', head, body)}
      <h3>Per task</h3>
      ${scenarioMatrix(b, 'passRate', { rowHeader: 'Task', caption: 'Pass rate by task, arm and model (valid runs)', noteFor: (s) => flagList(s, 'Sanity check, not in headline') })}`;
  }

  function renderMethod(r) {
    const commits = r.commits.map((c) => `<a href="${REPO}/commit/${esc(c)}"><code>${esc(shortSha(c))}</code></a>`).join(', ') || NA;
    const rawUrl = `${D.rawDataUrl}/${encodeURIComponent(r.release)}`;
    const benches = Object.entries(r.benchmarks);
    const scoring = benches.map(([n, b]) => `<code>${esc(n)}</code>: ${b.scoringVersions.map(esc).join(', ')}`).join('; ');
    const winappVersions = [...new Set(benches.flatMap(([, b]) => b.winappVersions))];
    const debugArms = [...new Set(benches.flatMap(([, b]) => b.arms))].filter((a) => ARM_INFO[a]?.debug);
    const warnings = D.warnings.length
      ? `<details><summary>${D.warnings.length} data note(s) from the build</summary><ul>${D.warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></details>`
      : '<p>The build found no problems in the data files.</p>';
    return `<h2>How we measured</h2>
      <h3>This release</h3>
      <dl class="facts">
        <dt>Release</dt><dd><code>${esc(r.release)}</code></dd>
        <dt>Commit(s) tested</dt><dd>${commits}</dd>
        <dt>Runs started</dt><dd>${esc(r.firstStartedAt?.slice(0, 10) ?? 'n/a')} to ${esc(r.lastStartedAt?.slice(0, 10) ?? 'n/a')}</dd>
        <dt>Models</dt><dd>${r.models.map(modelLabel).map(esc).join(', ')}</dd>
        <dt>Scoring version</dt><dd>${scoring}</dd>
        <dt>winapp version</dt><dd>${winappVersions.length ? winappVersions.map(esc).join(', ') : 'not recorded'}</dd>
      </dl>
      <h3>The three arms</h3>
      <ul>${Object.values(ARM_INFO).filter((a) => !a.debug).map((a) => `<li><strong>${esc(a.label)}</strong>: ${esc(a.desc)}</li>`).join('')}</ul>
      ${debugArms.length ? `<h3>Debugging configurations</h3><p>These <code>tasks</code> runs are not one of the three arms and are not part of the comparison.</p><ul>${debugArms.map((a) => `<li><strong>${esc(armLabel(a))}</strong>: ${esc(ARM_INFO[a].desc)}</li>`).join('')}</ul>` : ''}
      <h3>What counts</h3>
      <ul>
        <li>Each run is one agent attempt at one scenario, with one model on one arm. The harness writes one small JSON file per run.</li>
        <li>Only <code>valid</code> runs count toward pass rates and averages. Every other outcome is listed on the Outcomes tab, and each headline number says how many runs were valid out of the total.</li>
        <li>A run passes when every <em>required</em> check passes. <em>Improvement</em> checks never fail a run; they are reported as quality checks and requirements met.</li>
        <li>Pass/fail comes from scripts. The judge model's design score is shown as detail and never changes a pass.</li>
        <li>Pass rates show a 95% confidence interval (Wilson). With few runs per cell the interval is wide, so small differences may be noise.</li>
        <li>A scenario that the <code>barebones</code> arm passes in ${Math.round(D.rules.sanityPassRate * 100)}% or more of valid runs is a sanity check: it can't show whether winapp or the skills help (<a href="${REPO}/issues/1253">#1253</a>).</li>
        <li>Credits are the AI credits Copilot CLI reports (1 credit = 1,000,000,000 nano-AIU). No dollar prices are used.</li>
      </ul>
      <h3>Raw data and reruns</h3>
      <p>Raw run files: ${r.release === 'dev'
        ? 'development (<code>dev</code>) runs are not published, so they have no public raw data.'
        : `<a href="${esc(rawUrl)}">${esc(rawUrl)}</a>. Until the <code>benchmark-data</code> branch exists (it is created with the first published release), this link does not work.`} The file format is in <a href="${REPO}/blob/main/benchmarks/RESULTS-FORMAT.md">RESULTS-FORMAT.md</a>, and every number on this page is in <a href="data.json">data.json</a>.</p>
      <p>To rerun: <a href="${REPO}/blob/main/benchmarks/apps/README.md">apps benchmark</a>, <a href="${REPO}/blob/main/benchmarks/tasks/README.md">tasks benchmark</a>.</p>
      <h3>Data notes</h3>${warnings}`;
  }

  // ---------- page ----------
  function render() {
    const r = current();
    state.release = r.release;
    const counts = Object.entries(r.benchmarks).map(([b, v]) => `${v.trialCount} ${b}`).join(', ');
    $('release-meta').textContent = `${r.trialCount} runs (${counts}) · ${r.firstStartedAt?.slice(0, 10) ?? ''} to ${r.lastStartedAt?.slice(0, 10) ?? ''}`;
    $('banner').innerHTML = r.release === 'dev'
      ? `<div class="callout warn"><p><strong>Development run, not a release.</strong> These numbers come from <code>dev</code> runs on the tuning set. They are for building the benchmark and may change.</p></div>`
      : '';
    $('panel-headline').innerHTML = renderHeadline(r);
    $('panel-scenarios').innerHTML = renderScenarios(r);
    $('panel-outcomes').innerHTML = renderOutcomes(r);
    $('panel-skills').innerHTML = renderSkills(r);
    $('panel-tasks').innerHTML = renderTasks(r);
    $('panel-method').innerHTML = renderMethod(r);
    $('footer').innerHTML = `Built ${esc(D.generatedAt.slice(0, 16).replace('T', ' '))} UTC from ${D.releases.length} release folder(s). Numbers: <a href="data.json">data.json</a>.`;
    bindScenarioMetric();
  }

  // The metric selector is redrawn with its panel, so it is bound again after each draw.
  function bindScenarioMetric() {
    const sel = $('scenario-metric');
    if (sel) sel.addEventListener('change', () => { state.scenarioMetric = sel.value; $('panel-scenarios').innerHTML = renderScenarios(current()); bindScenarioMetric(); $('scenario-metric').focus(); });
  }

  function selectTab(name, focus) {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    if (!tabs.some((t) => t.dataset.tab === name)) name = 'headline';
    state.tab = name;
    for (const t of tabs) {
      const on = t.dataset.tab === name;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      $(t.getAttribute('aria-controls')).hidden = !on;
      if (on && focus) t.focus();
    }
  }

  function updateHash() {
    const params = new URLSearchParams({ release: state.release, tab: state.tab });
    history.replaceState(null, '', `#${params}`);
  }

  function init() {
    if (!D || !D.releases?.length) {
      $('main').innerHTML = '<p>No benchmark data was found. Build the site with <code>build.mjs --data &lt;folder&gt;</code>.</p>';
      return;
    }
    const params = new URLSearchParams(location.hash.slice(1));
    state.release = D.releases.some((r) => r.release === params.get('release')) ? params.get('release') : D.releases[0].release;
    const sel = $('release');
    sel.innerHTML = D.releases.map((r) => `<option value="${esc(r.release)}">${esc(r.release === 'dev' ? 'dev (development runs)' : r.release)}</option>`).join('');
    sel.value = state.release;
    sel.addEventListener('change', () => { state.release = sel.value; render(); updateHash(); });

    const tabs = [...document.querySelectorAll('[role="tab"]')];
    tabs.forEach((t, i) => {
      t.addEventListener('click', () => { selectTab(t.dataset.tab); updateHash(); });
      t.addEventListener('keydown', (e) => {
        let j = null;
        if (e.key === 'ArrowRight') j = (i + 1) % tabs.length;
        else if (e.key === 'ArrowLeft') j = (i - 1 + tabs.length) % tabs.length;
        else if (e.key === 'Home') j = 0;
        else if (e.key === 'End') j = tabs.length - 1;
        if (j === null) return;
        e.preventDefault();
        selectTab(tabs[j].dataset.tab, true);
        updateHash();
      });
    });
    render();
    selectTab(params.get('tab') ?? 'headline');
  }

  init();
})();
