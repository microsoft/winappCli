/* Renders the benchmark dashboard from window.BENCH_DATA (written by build.mjs). */
(function () {
  'use strict';

  const D = window.BENCH_DATA;
  const $ = (id) => document.getElementById(id);
  const REPO = 'https://github.com/microsoft/winappCli';
  const BASE = D?.rules?.baseArm ?? 'barebones';
  const WITH = D?.rules?.skillsArm ?? 'cli-plus-skills';
  const CLI = 'cli-only';

  const ARM_INFO = {
    [BASE]: {
      label: 'Without our tools',
      desc: 'No winapp CLI and no plugin skills. The agent works from what the model already knows and the standard Windows and .NET tools on the machine.',
    },
    [CLI]: {
      label: 'winapp CLI only',
      desc: 'The winapp CLI is on PATH, with no plugin skills. Runs made after #1263 also tell the agent in the prompt that winapp is installed.',
    },
    [WITH]: {
      label: 'With winapp + skills',
      desc: 'The winapp CLI is on PATH and the winapp and WinUI plugins are installed, so the agent can run winapp and load the skills.',
    },
    'winapp-skills': { label: 'Debugging: winapp plugin only', desc: 'tasks debugging configuration: winapp on PATH and only the winapp plugin.' },
    'winui-skills': { label: 'Debugging: WinUI plugin only', desc: 'tasks debugging configuration: winapp on PATH and only the WinUI plugin.' },
    'full-suite': { label: 'Debugging: both plugins + winapp agent', desc: 'tasks debugging configuration: winapp on PATH, both plugins, started as the winapp custom agent.' },
  };

  // Definitions, shown as tooltips and listed in "How we measured".
  const DEFS = {
    quality: 'Quality checks met: share of quality checks that passed in valid runs, such as accessible names, x:Bind and no hard-coded font sizes. Checked by script, plus a few by the judge model. Packaging checks (p2, p3) are not included; they are shown as Packaged app. The build-warnings check (q4) counts C# warnings only, because the WinUI analyzer comes with the winapp new template and is in few projects without it.',
    packaging: 'Packaged app: share of the packaging checks (p2, p3: the app is packaged as MSIX) that passed in valid runs. The app prompts do not ask for packaging, so it is reported on its own.',
    qualityAll: 'Quality checks met including the packaging checks (p2, p3), in valid runs.',
    requirements: "Requirements met: share of the scenario's requested features that the judge model found working, in valid runs.",
    works: 'Built and launched: share of valid runs where the app built and its window opened. Runs our harness failed are not counted.',
    credits: 'Credits per app: mean AI credits Copilot CLI reported per valid run (1 credit = 1,000,000,000 nano-AIU).',
    minutes: 'Minutes per app: mean wall-clock time per valid run.',
    pass: 'Pass rate: share of valid runs where every required check passed (builds, launches, right framework).',
    design: "Design score: the judge model's 0-10 rating. Detail only; it never changes a pass.",
    claim: 'Each skill will get one automatic sentence (Helps, Helps but costs more, No measurable difference, Not enough data) once the rules in #1182 are agreed. A skill that loads in fewer than half of the runs of the scenarios it targets already shows Activation failed: its results measure the agent without it, so no value claim is made.',
    activation: 'Target runs that loaded it: runs with winapp + skills of the scenarios that name this skill as their target, and how many of them loaded it. Harness errors are left out.',
    valid: 'Valid: the agent finished and the checks ran. Other outcomes (build-failed, harness-error, ...) are left out of rates and averages and listed under Details.',
  };

  const OUTCOME_INFO = {
    valid: 'The agent finished and the checks ran. Pass or fail is a real result.',
    'app-failed': 'The app built but crashed or did not run.',
    'build-failed': 'No app was produced.',
    'timed-out': 'The agent ran out of time.',
    'harness-error': 'Our tooling failed, not the agent. Excluded from pass rates and rerun.',
    'asked-before-admin': 'The agent safely stopped before an admin step. Not counted as a failure.',
  };

  const state = { release: null, tab: 'overview', appMetric: 'qualityRate', series: 'dev', trendModel: 'all', trendMetric: 'qualityRate' };

  // ---------- formatting ----------
  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  const NA = '<span class="muted" title="No data">n/a</span>';
  const pctText = (x) => `${Math.round(x * 100)}%`;
  const pct = (x) => (x == null ? NA : pctText(x));
  const num = (x, d = 1) => (x == null ? NA : x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }));
  function big(x) {
    if (x == null) return NA;
    if (x >= 1e6) return `${(x / 1e6).toFixed(1)}M`;
    if (x >= 1e3) return `${(x / 1e3).toFixed(0)}k`;
    return String(Math.round(x));
  }
  const ci = (m) => (m.passCi ? `<span class="ci">95% CI ${Math.round(m.passCi[0] * 100)}–${Math.round(m.passCi[1] * 100)}%</span>` : '');
  const frac = (a, b) => (b ? `${a}/${b}` : NA);
  function signed(x, fmt) {
    if (x == null) return NA;
    const sign = x > 0 ? '+' : x < 0 ? '−' : '±';
    return `${sign}${fmt(Math.abs(x))}`;
  }
  const diffPts = (x) => signed(x, (v) => `${Math.round(v * 100)} pts`);
  const diffNum = (x, d = 1) => signed(x, (v) => v.toFixed(d));
  const relChange = (w, o) => (w == null || !o ? null : (w - o) / o);
  const diffRel = (w, o) => signed(relChange(w, o), (v) => `${Math.round(v * 100)}%`);
  function cap(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }
  function modelLabel(m) {
    if (m === 'all') return 'All models';
    const parts = m.replace(/^claude-/, '').split('-');
    if (parts[0] === 'gpt') return `GPT-${parts.slice(1).map(cap).join(' ')}`;
    return parts.map(cap).join(' ');
  }
  const armLabel = (a) => ARM_INFO[a]?.label ?? a;
  const armHtml = (a) => `${esc(armLabel(a))}<span class="arm-id">${esc(a)}</span>`;
  const tip = (text, key) => `<span class="tip" title="${esc(DEFS[key])}">${text}</span>`;
  function table(caption, head, body) {
    return `<div class="table-scroll"><table><caption>${caption}</caption><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
  }

  const view = (b) => b;
  const hasBoth = (b) => b && b.arms.includes(BASE) && b.arms.includes(WITH);

  // ---------- chart: one bar per setup, grouped by model ----------
  let chartId = 0;
  const armClass = (a) => (a === BASE ? 'without' : a === WITH ? 'with' : 'cli');
  function groupedBars({ title, desc, arms, groups, max = 1, format = pctText }) {
    const id = `chart-${++chartId}`;
    const W = 640, H = 240, left = 44, bottom = 34, top = 18;
    const plotW = W - left - 10, plotH = H - top - bottom;
    const gw = plotW / groups.length, bw = Math.min(48, (gw - 24) / arms.length);
    let bars = '';
    groups.forEach((g, gi) => {
      const x0 = left + gi * gw + (gw - bw * arms.length) / 2;
      g.values.forEach((v, si) => {
        if (v == null) return;
        const h = (Math.min(v, max) / max) * plotH, x = x0 + si * bw, y = top + plotH - h;
        bars += `<rect class="${armClass(arms[si])}" x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${(bw - 4).toFixed(1)}" height="${h.toFixed(1)}"><title>${esc(g.label)}, ${esc(armLabel(arms[si]))}: ${esc(format(v))}</title></rect>`;
        bars += `<text x="${(x + (bw - 4) / 2).toFixed(1)}" y="${(y - 4).toFixed(1)}" text-anchor="middle" class="val">${esc(format(v))}</text>`;
      });
      bars += `<text x="${(left + gi * gw + gw / 2).toFixed(1)}" y="${H - bottom + 18}" text-anchor="middle">${esc(g.label)}</text>`;
    });
    const ticks = [0, 0.5, 1].map((f) => {
      const y = top + plotH - f * plotH;
      return `<line class="axis" x1="${left}" x2="${W - 10}" y1="${y}" y2="${y}"/><text x="${left - 6}" y="${y + 4}" text-anchor="end">${esc(format(f * max))}</text>`;
    }).join('');
    return `<figure class="chart"><figcaption id="${id}-t">${esc(title)}</figcaption>
      <svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-labelledby="${id}-t ${id}-d"><desc id="${id}-d">${esc(desc)}</desc>${ticks}${bars}</svg>
      <div class="legend" aria-hidden="true">${arms.map((a) => `<span class="${armClass(a)}">${esc(armLabel(a))}</span>`).join('')}</div></figure>`;
  }

  // ---------- shared tables ----------
  // One row group per arm: "All models", then one row per model.
  function armModelRows(b, byArm, cols) {
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', byArm[arm].all], ...b.models.map((m) => [m, byArm[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${armHtml(arm)}</th>` : '';
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th>${cols.map((c) => `<td>${c.cell(v)}</td>`).join('')}</tr>`;
      });
    }
    return body;
  }
  const armModelHead = (cols) => `<tr><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th>${cols.map((c) => `<th scope="col">${c.h}</th>`).join('')}</tr>`;

  function flagList(s, sanityText) {
    const items = s.flags.map((f) => (f.kind === 'sanity' ? `<li><span class="tag">${esc(sanityText)}</span> ${esc(f.text)}</li>` : `<li>${esc(f.text)}</li>`));
    return items.length ? `<ul>${items.join('')}</ul>` : '<span class="muted">None</span>';
  }

  // Scenario x (arm, model) matrix of one metric, for the collapsed per-model breakdowns.
  function perModelMatrix(b, cell, caption, rowHeader, sanityText) {
    const cols = ['all', ...b.models];
    const head = `<tr><th scope="col" rowspan="2" class="text">${rowHeader}</th>${b.arms.map((a) => `<th scope="colgroup" colspan="${cols.length}">${armHtml(a)}</th>`).join('')}<th scope="col" rowspan="2" class="text">Flags</th></tr>
      <tr>${b.arms.map(() => cols.map((m) => `<th scope="col">${esc(m === 'all' ? 'All' : modelLabel(m))}</th>`).join('')).join('')}</tr>`;
    const body = b.perScenario.map((s) => {
      const flagged = new Set(s.flags.filter((f) => f.model).map((f) => `${f.arm}|${f.model}`));
      const cells = b.arms.map((a) => cols.map((m) => {
        const v = m === 'all' ? s.cells[a].all : s.cells[a].byModel[m];
        const mark = flagged.has(`${a}|${m}`) ? ' <span class="flag-mark" aria-label="flagged">!</span>' : '';
        return `<td>${cell(v)}${mark}</td>`;
      }).join('')).join('');
      return `<tr><th scope="row" class="text">${esc(s.scenario)}</th>${cells}<td class="flags">${flagList(s, sanityText)}</td></tr>`;
    }).join('');
    return table(caption, head, body);
  }

  // ---------- Overview ----------
  // Leads with packaging, then the other quality checks, then cost. Generated from the data, no thresholds.
  function verdict(b) {
    const o = b.headline[BASE].all, w = b.headline[WITH].all;
    const parts = [];
    if (o.packagingRate != null && w.packagingRate != null) {
      parts.push(`With winapp and the skills, agents shipped a packaged app ${pctText(w.packagingRate)} of the time instead of ${pctText(o.packagingRate)}.`);
    }
    if (o.qualityRate != null && w.qualityRate != null) {
      const subject = parts.length ? 'Other quality checks' : 'With winapp and the skills, quality checks';
      const a = pctText(o.qualityRate), z = pctText(w.qualityRate);
      parts.push(a === z ? `${subject} stayed at ${a}.` : `${subject} ${w.qualityRate > o.qualityRate ? 'rose' : 'fell'} from ${a} to ${z}.`);
    }
    const rc = relChange(w.creditsMean, o.creditsMean);
    if (rc != null && Math.round(rc * 100) !== 0) {
      parts.push(`They used ${Math.round(Math.abs(rc) * 100)}% ${rc > 0 ? 'more' : 'fewer'} credits per app (${w.creditsMean.toFixed(0)} instead of ${o.creditsMean.toFixed(0)}).`);
    }
    return parts.join(' ');
  }

  function tile(label, defKey, without, withV, change, sub = '', cliV = null) {
    return `<div class="tile" role="group" aria-label="${esc(label)}">
      <div class="tile-label">${tip(esc(label), defKey)}</div>
      <div class="tile-value"><span class="from" title="${esc(armLabel(BASE))}">${without}</span> <span aria-hidden="true">→</span><span class="sr-only"> to </span> <span class="to" title="${esc(armLabel(WITH))}">${withV}</span></div>
      <div class="tile-change">${change}</div>${cliV != null ? `<div class="tile-sub">${esc(armLabel(CLI))}: ${cliV}</div>` : ''}${sub ? `<div class="tile-sub">${sub}</div>` : ''}</div>`;
  }

  function renderOverview(r) {
    const b = view(r.benchmarks.apps);
    if (!hasBoth(b)) return `<h2>Overview</h2><p>This release has no <code>apps</code> runs for both setups, so there is nothing to compare.</p>`;
    const o = b.headline[BASE].all, w = b.headline[WITH].all, e = b.effect.all;
    const c = b.headline[CLI]?.all;
    const mid = (f) => (c ? f(c) : null);
    const allWorked = [o, w, c].filter(Boolean).every((x) => x.worksTotal > 0 && x.worked === x.worksTotal);
    const tiles = [
      tile('Packaged app', 'packaging', pct(o.packagingRate), pct(w.packagingRate), diffPts(e.packagingRate), '', mid((x) => pct(x.packagingRate))),
      tile('Quality checks met', 'quality', pct(o.qualityRate), pct(w.qualityRate), diffPts(e.qualityRate), '', mid((x) => pct(x.qualityRate))),
      tile('Requirements met', 'requirements', pct(o.reqRate), pct(w.reqRate), diffPts(e.reqRate), '', mid((x) => pct(x.reqRate))),
      allWorked ? '' : tile('Built and launched', 'works', pct(o.worksRate), pct(w.worksRate), diffPts(e.worksRate), `${o.worked} of ${o.worksTotal} → ${w.worked} of ${w.worksTotal} valid runs`, mid((x) => pct(x.worksRate))),
      tile('Credits per app', 'credits', num(o.creditsMean, 0), num(w.creditsMean, 0), diffRel(w.creditsMean, o.creditsMean),
        `${tip('Minutes per app', 'minutes')}: ${num(o.minutesMean)} → ${num(w.minutesMean)}`, mid((x) => num(x.creditsMean, 0))),
    ].join('');

    const arms = b.arms.filter((a) => [BASE, CLI, WITH].includes(a));
    const chart = groupedBars({
      title: 'Quality checks met (without packaging), by model',
      desc: `For each model, the share of quality checks met, not counting packaging, in each setup: ${arms.map(armLabel).join(', ')}. The numbers are also in the details table.`,
      arms,
      groups: [{ label: 'All models', values: arms.map((a) => b.headline[a].all.qualityRate) }, ...b.models.map((m) => ({ label: modelLabel(m), values: arms.map((a) => b.headline[a].byModel[m].qualityRate) }))],
    });

    const cols = [
      { h: tip('Valid of total', 'valid'), cell: (m) => `${m.valid} of ${m.total}` },
      { h: tip('Pass rate', 'pass'), cell: (m) => `${pct(m.passRate)}${ci(m)}` },
      { h: tip('Built and launched', 'works'), cell: (m) => pct(m.worksRate) },
      { h: tip('Quality checks', 'quality'), cell: (m) => `<span title="${m.qualityMet} of ${m.qualityTotal}">${pct(m.qualityRate)}</span>` },
      { h: tip('Packaged app', 'packaging'), cell: (m) => `<span title="${m.packagingMet} of ${m.packagingTotal}">${pct(m.packagingRate)}</span>` },
      { h: tip('Quality checks, includes packaging', 'qualityAll'), cell: (m) => `<span title="${m.qualityAllMet} of ${m.qualityAllTotal}">${pct(m.qualityAllRate)}</span>` },
      { h: tip('Requirements', 'requirements'), cell: (m) => `<span title="${m.reqMet} of ${m.reqTotal}">${pct(m.reqRate)}</span>` },
      { h: tip('Design score', 'design'), cell: (m) => num(m.designMean) },
      { h: tip('Credits per app', 'credits'), cell: (m) => num(m.creditsMean) },
      { h: tip('Minutes per app', 'minutes'), cell: (m) => num(m.minutesMean) },
      { h: 'Tokens per app', cell: (m) => big(m.tokensMean) },
    ];
    const effectRows = [['all', b.effect.all], ...b.models.map((m) => [m, b.effect.byModel[m]])].map(([m, d]) =>
      `<tr class="${m === 'all' ? 'all-models' : ''}"><th scope="row" class="text">${esc(modelLabel(m))}</th><td>${diffPts(d.passRate)}</td><td>${diffPts(d.worksRate)}</td><td>${diffPts(d.qualityRate)}</td><td>${diffPts(d.packagingRate)}</td><td>${diffPts(d.qualityAllRate)}</td><td>${diffPts(d.reqRate)}</td><td>${diffNum(d.designMean)}</td><td>${diffNum(d.creditsMean)}</td><td>${diffNum(d.minutesMean)}</td></tr>`).join('');
    const effectHead = '<tr><th scope="col" class="text">Model</th><th scope="col">Pass rate</th><th scope="col">Built and launched</th><th scope="col">Quality checks</th><th scope="col">Packaged app</th><th scope="col">Quality checks, includes packaging</th><th scope="col">Requirements</th><th scope="col">Design score</th><th scope="col">Credits per app</th><th scope="col">Minutes per app</th></tr>';

    return `<h2 class="sr-only">Overview</h2>
      <p class="verdict">${esc(verdict(b))}</p>
      <p class="muted context">${b.scenarios.length} app scenarios, ${b.models.length} models (${b.models.map(modelLabel).join(', ')}), ${b.arms.reduce((s, a) => s + b.headline[a].all.total, 0)} runs. Hover a label for its definition.</p>
      <div class="tiles">${tiles}</div>
      ${allWorked ? '<p class="muted context">Every app built and launched in every setup (valid runs).</p>' : ''}
      ${chart}
      <details class="more"><summary>Show details</summary>
        <p>Pass rate is ${arms.map((a) => `${pct(b.headline[a].all.passRate)} ${esc(armLabel(a).toLowerCase())}`).join(', ')}, so it does not separate the setups; the tiles above use the measures that do.</p>
        ${table('Results by setup and model (valid runs)', armModelHead(cols), armModelRows(b, b.headline, cols))}
        ${table(`Change: ${esc(armLabel(WITH))} minus ${esc(armLabel(BASE))}`, effectHead, effectRows)}
        ${pairedTable(b)}
      </details>`;
  }

  // Per model, quality without packaging: mean over apps of the paired per-app change, with a
  // 95% bootstrap interval over apps. An interval that includes 0 means no clear change.
  function pairedTable(b) {
    const g = b.pairedQualityGap ?? {};
    const rows = b.models.filter((m) => g[m]).map((m) => `<tr><th scope="row" class="text">${esc(modelLabel(m))}</th><td>${diffPts(g[m].mean)}</td><td>${diffPts(g[m].ci[0])} to ${diffPts(g[m].ci[1])}</td><td>${g[m].apps}</td></tr>`).join('');
    if (!rows) return '';
    return table(`Quality checks met (without packaging), change per model, paired by app`, '<tr><th scope="col" class="text">Model</th><th scope="col">Mean change over apps</th><th scope="col">95% interval (bootstrap over apps)</th><th scope="col">Apps</th></tr>', rows);
  }

  // ---------- By app ----------
  const APP_METRICS = {
    qualityRate: { label: 'Quality checks met', def: 'quality', cell: pct, diff: diffPts },
    packagingRate: { label: 'Packaged app', def: 'packaging', cell: pct, diff: diffPts },
    reqRate: { label: 'Requirements met', def: 'requirements', cell: pct, diff: diffPts },
    worksRate: { label: 'Built and launched', def: 'works', cell: pct, diff: diffPts },
    designMean: { label: 'Design score', def: 'design', cell: (x) => num(x), diff: (x) => diffNum(x) },
    creditsMean: { label: 'Credits per app', def: 'credits', cell: (x) => num(x, 0), diff: (x) => diffNum(x, 0) },
    minutesMean: { label: 'Minutes per app', def: 'minutes', cell: (x) => num(x), diff: (x) => diffNum(x) },
  };

  function renderApps(r) {
    const b = view(r.benchmarks.apps);
    if (!hasBoth(b)) return `<h2>By app</h2><p>This release has no <code>apps</code> runs for both setups.</p>`;
    const k = state.appMetric, M = APP_METRICS[k];
    const rows = b.perScenario
      .map((s) => ({ s, o: s.cells[BASE].all[k], c: s.cells[CLI]?.all[k], w: s.cells[WITH].all[k], d: s.effect?.all[k] }))
      .sort((x, y) => (y.d ?? -Infinity) - (x.d ?? -Infinity));
    const hasCli = b.arms.includes(CLI);
    const body = rows.map(({ s, o, c, w, d }) => {
      const notes = [];
      if (s.flags.some((f) => f.kind !== 'sanity')) notes.push(`<span class="flag-mark" title="${esc(s.flags.filter((f) => f.kind !== 'sanity').map((f) => f.text).join('\n'))}">!</span><span class="sr-only"> has flags, see the per-model breakdown</span>`);
      return `<tr><th scope="row" class="text">${esc(s.scenario)} ${notes.join('')}</th><td>${M.cell(o)}</td>${hasCli ? `<td class="cli-col">${M.cell(c)}</td>` : ''}<td>${M.cell(w)}</td><td class="change">${M.diff(d)}</td></tr>`;
    }).join('');
    const head = `<tr><th scope="col" class="text">App</th><th scope="col">${armHtml(BASE)}</th>${hasCli ? `<th scope="col">${armHtml(CLI)}</th>` : ''}<th scope="col">${armHtml(WITH)}</th><th scope="col" title="${esc(armLabel(WITH))} minus ${esc(armLabel(BASE))}">Change</th></tr>`;
    const options = Object.entries(APP_METRICS).map(([key, v]) => `<option value="${key}"${key === k ? ' selected' : ''}>${esc(v.label)}</option>`).join('');
    return `<h2>By app</h2>
      <div class="inline-control"><label for="app-metric">Compare</label><select id="app-metric">${options}</select><span class="muted">${esc(DEFS[M.def])}</span></div>
      ${table(`${esc(M.label)} by app, largest change first (all models)`, head, body)}
      <details class="more"><summary>Per-model breakdown and flags</summary>
        ${perModelMatrix(b, (v) => M.cell(v[k]), `${esc(M.label)} by app, setup and model`, 'App', 'Sanity check')}
      </details>`;
  }

  // ---------- Skills ----------
  function renderSkills(r) {
    const benches = Object.entries(r.benchmarks).map(([bn, b]) => [bn, view(b)]).filter(([, b]) => b.arms.includes(WITH));
    const names = new Set();
    for (const [, b] of benches) Object.keys(b.skills[WITH].names).forEach((n) => names.add(n));
    // Target-skill activation, summed over benchmarks.
    const act = new Map();
    for (const [bn, b] of benches) {
      for (const s of b.activation?.perSkill ?? []) {
        const a = act.get(s.skill) ?? { runs: 0, loaded: 0, scenarios: [] };
        a.runs += s.runs; a.loaded += s.loaded; a.scenarios.push(...s.scenarios.map((sc) => `${bn}: ${sc}`));
        act.set(s.skill, a);
        names.add(s.skill);
      }
    }
    const minRate = D?.rules?.activationMinRate ?? 0.5;
    const failed = (n) => { const a = act.get(n); return !!a && a.runs > 0 && a.loaded / a.runs < minRate; };
    const actCell = (n) => {
      const a = act.get(n);
      if (!a) return '<td class="text"><span class="muted">no target scenario</span></td>';
      return `<td class="text" title="${esc(a.scenarios.join(', '))}">loaded in ${a.loaded} of ${a.runs} target runs</td>`;
    };
    const claimCell = (n) => (failed(n)
      ? '<td class="text"><span class="tag">Activation failed</span></td>'
      : '<td class="text"><span class="tag info">Not enough data</span></td>');
    if (!names.size) return `<h2>Skills</h2>${skillImpact(r)}<p>No skills were recorded in this release.</p>`;
    const share = (b, n) => {
      const u = b.skills[WITH];
      return u.recorded ? (u.names[n]?.trials ?? 0) / u.recorded : null;
    };
    const first = benches[0][1];
    const sorted = [...names].sort((x, y) => (share(first, y) ?? 0) - (share(first, x) ?? 0) || x.localeCompare(y));
    const cell = (b, n) => {
      const u = b.skills[WITH];
      if (!u.recorded) return '<td><span class="muted">not recorded</span></td>';
      const v = u.names[n]?.trials ?? 0, p = v / u.recorded;
      return `<td class="text"><span class="bar" aria-hidden="true"><span style="width:${Math.round(p * 100)}%"></span></span> ${v} of ${u.recorded} <span class="muted">(${pctText(p)})</span></td>`;
    };
    const head = `<tr><th scope="col" class="text">Skill</th>${benches.map(([bn]) => `<th scope="col"><code>${esc(bn)}</code> runs that loaded it</th>`).join('')}<th scope="col" class="text">${tip('Target runs that loaded it', 'activation')}</th><th scope="col" class="text">${tip('Claim', 'claim')}</th></tr>`;
    const body = sorted.map((n) => `<tr><th scope="row" class="text"><code>${esc(n)}</code></th>${benches.map(([, b]) => cell(b, n)).join('')}${actCell(n)}${claimCell(n)}</tr>`).join('');
    const partial = benches.filter(([, b]) => b.skills[WITH].recorded && b.skills[WITH].recorded < b.skills[WITH].trials)
      .map(([bn, b]) => `<code>${esc(bn)}</code> recorded skills in ${b.skills[WITH].recorded} of ${b.skills[WITH].trials} runs`);
    return `<h2>Skills</h2>
      ${skillImpact(r)}
      <h3>Which skills loaded</h3>
      <p>Which skills the agent loaded ${esc(armLabel(WITH).toLowerCase())}, and in how many runs. The other setups have no skills installed, so they are not shown here. Most scenarios name one skill they target; a skill that loads in fewer than half of those runs shows <strong>Activation failed</strong> and gets no value claim, because its results then measure the agent without it.</p>
      ${table(`Skills loaded, ${esc(armLabel(WITH).toLowerCase())}`, head, body)}
      ${partial.length ? `<p class="muted">${partial.join('; ')}.</p>` : ''}`;
  }

  // Setup-level change per quality-check topic (apps, valid runs): barebones vs cli-plus-skills.
  function skillImpact(r) {
    const b = r.benchmarks.apps;
    const topics = (b?.qualityTopics ?? []).filter((q) => q.byArm[BASE] && q.byArm[WITH]);
    if (!topics.length) return '';
    const cell = (v) => `${pct(v.rate)} <span class="ci">${v.passed} of ${v.total}</span>`;
    const change = (d) => {
      if (d == null) return `<td>${NA}</td>`;
      const pts = Math.round(d * 100);
      return pts < 0
        ? `<td class="change regress">${diffPts(d)}<span class="sr-only"> (worse)</span></td>`
        : `<td class="change">${diffPts(d)}</td>`;
    };
    const head = `<tr><th scope="col" class="text">Quality checks about</th><th scope="col">${armHtml(BASE)}</th><th scope="col">${armHtml(WITH)}</th><th scope="col">Change</th><th scope="col" class="text">Related skills</th></tr>`;
    const body = topics.map((q) => `<tr><th scope="row" class="text" title="Check ids: ${esc(q.checks.join(', '))}">${esc(q.topic)}</th><td>${cell(q.byArm[BASE])}</td><td>${cell(q.byArm[WITH])}</td>${change(q.change)}<td class="text">${q.skills.map((s) => `<code class="skill">${esc(s)}</code>`).join(' ')}</td></tr>`).join('');
    return `<h3>What changed with skills</h3>
      ${table(`Quality checks passed in valid <code>apps</code> runs, ${esc(armLabel(BASE).toLowerCase())} vs ${esc(armLabel(WITH).toLowerCase())}`, head, body)}
      ${topics.filter((q) => q.note).map((q) => `<p class="muted"><strong>${esc(q.topic)}:</strong> ${esc(q.note)}</p>`).join('')}
      <p class="muted">Setup-level comparison: shows what the plugin as a whole changed. A skill loading in a trial is not evidence it helped (agents load skills on harder apps), so per-skill impact needs per-skill scenarios (<a href="${REPO}/issues/1206">#1206</a>).</p>`;
  }

  // ---------- Trends ----------
  // Line chart over runs. A step i-1 -> i that is not comparable is not connected, and a
  // dashed vertical marker sits between the two runs, with the change titles on hover.
  function trendChart({ title, desc, labels, subLabels, series, markers, domain, format }) {
    const id = `chart-${++chartId}`;
    const W = 680, H = 260, left = 90, right = 60, top = 16, bottom = 50;
    const plotW = W - left - right, plotH = H - top - bottom;
    const n = labels.length;
    const x = (i) => left + (n === 1 ? plotW / 2 : (i / (n - 1)) * plotW);
    const [lo, hi] = domain;
    const y = (v) => top + plotH - ((v - lo) / (hi - lo)) * plotH;
    const ticks = [lo, (lo + hi) / 2, hi].map((v) => `<line class="axis" x1="${left}" x2="${W - right}" y1="${y(v)}" y2="${y(v)}"/><text x="${left - 6}" y="${y(v) + 4}" text-anchor="end">${esc(format(v))}</text>`).join('');
    const zero = lo < 0 && hi > 0 ? `<line class="zero" x1="${left}" x2="${W - right}" y1="${y(0)}" y2="${y(0)}"/>` : '';
    const marks = markers.map((m) => {
      const mx = (x(m.index - 1) + x(m.index)) / 2;
      return `<g class="marker" tabindex="0" aria-label="Method changed: ${esc(m.title)}"><title>Method changed: ${esc(m.title)}</title><line x1="${mx}" x2="${mx}" y1="${top}" y2="${top + plotH}"/><text x="${mx}" y="${top - 4}" text-anchor="middle">method changed</text></g>`;
    }).join('');
    let lines = '';
    for (const s of series) {
      let seg = [];
      const flush = () => { if (seg.length > 1) lines += `<polyline class="ln ${s.cls}" fill="none" points="${seg.join(' ')}"/>`; seg = []; };
      s.values.forEach((v, i) => {
        if (i > 0 && s.breaks?.[i - 1]) flush();
        if (v == null) { flush(); return; }
        seg.push(`${x(i).toFixed(1)},${y(v).toFixed(1)}`);
        lines += `<circle class="pt ${s.cls}" cx="${x(i).toFixed(1)}" cy="${y(v).toFixed(1)}" r="4"><title>${esc(labels[i])}, ${esc(s.name)}: ${esc(format(v))}${s.n?.[i] ? `, ${esc(s.n[i])}` : ''}</title></circle>`;
      });
      flush();
    }
    const xl = labels.map((l, i) => `<text x="${x(i)}" y="${H - bottom + 18}" text-anchor="middle">${esc(l)}</text><text class="sub" x="${x(i)}" y="${H - bottom + 34}" text-anchor="middle">${esc(subLabels[i] ?? '')}</text>`).join('');
    const legend = series.map((s) => `<span class="${s.cls}">${esc(s.name)}</span>`).join('');
    return `<figure class="chart"><figcaption id="${id}-t">${esc(title)}</figcaption>
      <svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-labelledby="${id}-t ${id}-d"><desc id="${id}-d">${esc(desc)}</desc>${ticks}${zero}${marks}${lines}${xl}</svg>
      <div class="legend" aria-hidden="true">${legend}</div></figure>`;
  }

  const TREND_METRICS = { qualityRate: 'Quality checks met (without packaging)', packagingRate: 'Packaged app', reqRate: 'Requirements met', passRate: 'Pass rate' };

  function renderTrends() {
    const T = D.trends?.[state.series]?.apps;
    const seriesOpts = [['dev', 'Development runs'], ['release', 'Releases']].map(([k, v]) => `<option value="${k}"${k === state.series ? ' selected' : ''}>${v}</option>`).join('');
    const pts = T?.points ?? [];
    const models = [...new Set(pts.flatMap((p) => p.info.models))];
    if (!models.includes(state.trendModel)) state.trendModel = 'all';
    const modelOpts = ['all', ...models].map((m) => `<option value="${esc(m)}"${m === state.trendModel ? ' selected' : ''}>${esc(modelLabel(m))}</option>`).join('');
    const metricOpts = Object.entries(TREND_METRICS).map(([k, v]) => `<option value="${k}"${k === state.trendMetric ? ' selected' : ''}>${v}</option>`).join('');
    const controls = `<div class="inline-control"><label for="trend-series">Show</label><select id="trend-series">${seriesOpts}</select>
      <label for="trend-model">Model</label><select id="trend-model">${modelOpts}</select></div>`;
    const head = `<h2>Trends</h2>${controls}`;
    if (pts.length < 2) {
      const msg = pts.length === 0
        ? (state.series === 'release' ? 'No releases are published yet. The release trend uses releases only, never development runs.' : 'No development runs yet.')
        : 'One run so far; trends appear after the next run.';
      return `${head}<p class="verdict-sm">${msg}</p>${runInfoTable(pts)}${methodChangeList(T)}`;
    }

    const m = state.trendModel;
    const pick = (o) => (m === 'all' ? o.all : o.byModel[m]) ?? {};
    const labels = pts.map((p) => p.name);
    const nText = (p) => {
      const o = pick(p.arms[BASE] ?? { all: {}, byModel: {} }), w = pick(p.arms[WITH] ?? { all: {}, byModel: {} });
      return `n ${o.valid ?? 0} · ${w.valid ?? 0}`;
    };
    const subLabels = pts.map(nText);
    const markers = T.steps.map((s, i) => ({ s, index: i + 1 })).filter(({ s }) => Object.values(s.comparable).some((c) => c === false))
      .map(({ s, index }) => ({ index, title: s.changes.map((c) => c.title).join('; ') }));

    const gapVals = (k) => pts.map((p) => pick(p.gap)[k] ?? null);
    const gq = gapVals('qualityRate'), gr = gapVals('reqRate');
    const all = [...gq, ...gr].filter((v) => v != null);
    const lo = Math.min(0, ...all) - 0.05, hi = Math.max(0, ...all) + 0.05;
    const gapBreaks = T.steps.map((s) => !s.comparable.gap);
    const gapChart = trendChart({
      title: `Gap: ${armLabel(WITH)} minus ${armLabel(BASE)}`,
      desc: 'Percentage-point difference per run for quality checks met and requirements met. The line is broken where the method changed.',
      labels, subLabels, markers, domain: [lo, hi], format: (v) => `${v > 0 ? '+' : ''}${Math.round(v * 100)} pts`,
      series: [
        { name: 'Quality checks met', cls: 'gq', values: gq, breaks: gapBreaks, n: subLabels },
        { name: 'Requirements met', cls: 'gr', values: gr, breaks: gapBreaks, n: subLabels },
      ],
    });

    const k = state.trendMetric;
    const armList = [BASE, CLI, WITH].filter((a) => pts.some((p) => p.arms[a]));
    const rawChart = trendChart({
      title: `${TREND_METRICS[k]}, each setup`,
      desc: `${TREND_METRICS[k]} per run for each setup. A setup's line is broken where the method changed for that setup.`,
      labels, subLabels: pts.map(() => ''), markers, domain: [0, 1], format: pctText,
      series: armList.map((a) => ({
        name: armLabel(a), cls: `s-${armClass(a)}`, breaks: T.steps.map((s) => s.comparable[a] === false),
        values: pts.map((p) => (p.arms[a] ? pick(p.arms[a])[k] ?? null : null)),
        n: pts.map((p) => (p.arms[a] ? `n ${pick(p.arms[a]).valid ?? 0} of ${pick(p.arms[a]).total ?? 0}` : '')),
      })),
    });

    const notes = T.notes.map((nt) => `<div class="callout warn"><p><strong>${esc(armLabel(BASE))} moved: something other than the skills changed (model, machine or harness).</strong> Between ${esc(nameOf(T, nt.from))} and ${esc(nameOf(T, nt.to))}: ${nt.moved.map((x) => `${esc(TREND_METRICS[x.metric])} ${pctText(x.from)} → ${pctText(x.to)}`).join(', ')}.</p></div>`).join('');

    return `${head}
      ${notes}
      ${gapChart}
      <div class="inline-control"><label for="trend-metric">Setups</label><select id="trend-metric">${metricOpts}</select></div>
      ${rawChart}
      ${decayTable(T)}
      ${runInfoTable(pts)}
      ${methodChangeList(T)}`;
  }

  const nameOf = (T, id) => T.points.find((p) => p.id === id)?.name ?? id;

  function decayTable(T) {
    if (!T.decay.length) return '';
    const head = `<tr><th scope="col" class="text">App</th>${T.points.map((p) => `<th scope="col">${esc(p.name)}</th>`).join('')}<th scope="col" class="text">Flag</th></tr>`;
    const body = T.decay.map((d) => `<tr><th scope="row" class="text">${esc(d.scenario)}</th>${d.values.map((v) => (v ? `<td title="Pass rate ${pctText(v.passRate ?? 0)}, ${v.valid} valid of ${v.total}">${pct(v[d.metric])} <span class="ci">pass ${pct(v.passRate)}</span></td>` : `<td>${NA}</td>`)).join('')}<td class="text">${d.rising ? '<span class="tag">Getting easier</span>' : ''}</td></tr>`).join('');
    return `<h3>${esc(armLabel(BASE))}, per app</h3>
      <p class="muted">Quality checks met ${esc(armLabel(BASE).toLowerCase())}. "Getting easier" means it rose more than ${Math.round(D.rules.controlShift * 100)} points across comparable runs, so models may be solving the app without help.</p>
      ${table(`${esc(armLabel(BASE))} quality checks met per app and run`, head, body)}`;
  }

  function runInfoTable(pts) {
    if (!pts.length) return '';
    const commits = (list) => list.map((c) => `<a href="${REPO}/commit/${esc(c)}"><code>${esc(c.slice(0, 8))}</code></a>`).join(', ') || NA;
    const body = pts.map((p) => `<tr><th scope="row" class="text">${esc(p.name)}</th><td class="text">${esc(p.firstStartedAt?.slice(0, 10) ?? '')} to ${esc(p.lastStartedAt?.slice(0, 10) ?? '')}</td><td class="text">${commits(p.info.commits)}</td><td class="text">${p.info.pluginsCommits.length ? commits(p.info.pluginsCommits) : '<span class="muted">not recorded</span>'}</td><td class="text">${p.info.winappVersions.length ? p.info.winappVersions.map(esc).join(', ') : '<span class="muted">not recorded</span>'}</td><td class="text">${p.info.models.map(modelLabel).map(esc).join(', ')}</td><td class="text">${p.info.scoringVersions.map(esc).join(', ')}</td></tr>`).join('');
    const head = '<tr><th scope="col" class="text">Run</th><th scope="col" class="text">Dates</th><th scope="col" class="text">Commit</th><th scope="col" class="text">Plugins commit</th><th scope="col" class="text">winapp version</th><th scope="col" class="text">Models</th><th scope="col" class="text">Scoring version</th></tr>';
    return `<details class="more"><summary>Run info</summary>${table('What each run measured (apps)', head, body)}</details>`;
  }

  function methodChangeList(T) {
    const list = D.methodChanges ?? [];
    if (!list.length) return '';
    const comp = (c) => (c.comparable && typeof c.comparable === 'object'
      ? Object.entries(c.comparable).map(([a, v]) => `${esc(armLabel(a))}: ${v ? 'comparable' : 'not comparable'}`).join(', ')
      : c.comparable ? 'comparable' : 'not comparable');
    const auto = (T?.steps ?? []).flatMap((s) => s.changes).filter((c) => !list.some((x) => x.id === c.id));
    const items = [...list, ...auto].map((c) => `<li><strong>${esc(c.date)} ${esc(c.title)}</strong> (${esc((c.affects ?? []).join(', ') || 'all')}; ${c.comparable === undefined ? 'not comparable' : comp(c)}). ${esc(c.detail ?? '')}</li>`).join('');
    return `<details class="more"><summary>Method changes</summary><ul>${items}</ul></details>`;
  }

  // ---------- Details ----------
  function outcomesSection(r) {
    let html = '<h3>Outcomes</h3>';
    for (const [name, full] of Object.entries(r.benchmarks)) {
      const b = view(full);
      if (!b.arms.length) continue;
      const outs = b.outcomeNames.filter((o) => b.arms.some((a) => b.headline[a].all.outcomes[o]));
      const cols = [...outs.map((o) => ({ h: `<code title="${esc(OUTCOME_INFO[o] ?? '')}">${esc(o)}</code>`, cell: (m) => m.outcomes[o] ?? 0 })), { h: 'Total', cell: (m) => m.total }, { h: 'Credits spent', cell: (m) => num(m.creditsSpent, 0) }];
      html += table(`<code>${esc(name)}</code> outcomes by setup and model`, armModelHead(cols), armModelRows(b, b.headline, cols));
      if (b.notValid.length) {
        const rows = b.notValid.map((n) => `<tr><th scope="row" class="text">${esc(n.scenario)}</th><td class="text">${esc(armLabel(n.arm))}</td><td class="text">${esc(modelLabel(n.model))}</td><td>${n.trial}</td><td class="text"><code>${esc(n.outcome)}</code></td><td class="text">${n.error ? esc(n.error) : '<span class="muted">None recorded</span>'}</td><td>${num(n.credits)}</td></tr>`).join('');
        html += `<details class="more"><summary>${b.notValid.length} <code>${esc(name)}</code> run(s) that are not valid</summary>${table(`Runs that are not valid`, '<tr><th scope="col" class="text">Scenario</th><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th><th scope="col">Run</th><th scope="col" class="text">Outcome</th><th scope="col" class="text">Error</th><th scope="col">Credits</th></tr>', rows)}</details>`;
      }
      if (name === 'apps' && b.launchLevelNames.length) {
        const lv = b.launchLevelNames;
        const lcols = lv.map((l) => ({ h: l === 'not measured' ? 'Not measured' : `Level ${esc(l)}`, cell: (m) => m.launchLevels[l] ?? 0 }));
        html += `<details class="more"><summary>How far the apps launched</summary><p>0 did not run, 1 a window exists, 2 controls respond to input, 3 the scenario's main screen appeared. "Not measured": the app ran but level 2 could not be checked.</p>${table('Launch level by setup and model (all runs)', armModelHead(lcols), armModelRows(b, b.headline, lcols))}</details>`;
      }
    }
    return html;
  }

  function tasksSection(r) {
    const b = view(r.benchmarks.tasks);
    if (!b || !b.arms.length) return '<h3>Tasks</h3><p>This release has no <code>tasks</code> runs.</p>';
    const sanity = b.sanityScenarios;
    const note = sanity.length
      ? `<p><strong>Tasks don't separate the setups yet:</strong> ${sanity.length} of ${b.scenarios.length} tasks pass in ${Math.round(D.rules.sanityPassRate * 100)}% or more of runs even ${esc(armLabel(BASE).toLowerCase())} (<a href="${REPO}/issues/1253">#1253</a>).</p>`
      : '';
    const excl = (arm, m) => (m.byModelKey === 'all' ? b.headlineExcludingSanity[arm].all : b.headlineExcludingSanity[arm].byModel[m.byModelKey]);
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', b.headline[arm].all], ...b.models.map((m) => [m, b.headline[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${armHtml(arm)}</th>` : '';
        const x = excl(arm, { byModelKey: m });
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th><td>${v.valid} of ${v.total}</td><td>${frac(v.passed, v.valid)}</td><td>${pct(v.passRate)}${ci(v)}</td><td>${x.valid ? `${pct(x.passRate)} <span class="ci">${x.passed}/${x.valid}</span>` : NA}</td><td>${v.askedBeforeAdmin}</td><td>${num(v.creditsMean)}</td><td>${num(v.minutesMean, 2)}</td></tr>`;
      });
    }
    const head = `<tr><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th><th scope="col">Valid of total</th><th scope="col">Passed</th><th scope="col">Pass rate, all tasks</th><th scope="col">Pass rate without sanity checks</th><th scope="col">Asked before admin</th><th scope="col">Credits per run</th><th scope="col">Minutes per run</th></tr>`;
    const eff = hasBoth(b)
      ? `<p>Change ${esc(armLabel(WITH).toLowerCase())} minus ${esc(armLabel(BASE).toLowerCase())}: pass rate ${diffPts(b.effect.all.passRate)} on all tasks, ${diffPts(b.effectExcludingSanity.all.passRate)} without sanity checks; credits per run ${diffNum(b.effect.all.creditsMean)}.</p>`
      : '';
    return `<h3>Tasks</h3>
      <p>Small scripted jobs (packaging, signing, UI) in Windows Sandbox, ${b.scenarios.length} tasks.</p>
      ${note}${eff}
      ${table('Tasks results by setup and model', head, body)}
      <details class="more"><summary>Per task</summary>
        ${perModelMatrix(b, (v) => (v.valid ? `<span title="${v.passed} of ${v.valid}">${pct(v.passRate)}</span>` : NA), 'Pass rate by task, setup and model', 'Task', 'Sanity check, not in headline')}
      </details>`;
  }

  function commandsSection(r) {
    const benches = Object.entries(r.benchmarks).map(([bn, b]) => [bn, view(b)]);
    const cols = benches.flatMap(([bn, b]) => b.arms.map((a) => ({ bn, b, a })));
    const names = new Set();
    for (const c of cols) Object.keys(c.b.winappCommands[c.a].names).forEach((n) => names.add(n));
    if (!names.size) return '';
    const total = (n) => cols.reduce((s, c) => s + (c.b.winappCommands[c.a].names[n]?.trials ?? 0), 0);
    const sorted = [...names].sort((x, y) => total(y) - total(x) || x.localeCompare(y));
    const head = `<tr><th scope="col" rowspan="2" class="text">Command</th>${benches.map(([bn, b]) => `<th scope="colgroup" colspan="${b.arms.length}"><code>${esc(bn)}</code></th>`).join('')}</tr><tr>${cols.map((c) => `<th scope="col">${armHtml(c.a)}</th>`).join('')}</tr>`;
    const body = sorted.map((n) => `<tr><th scope="row" class="text"><code>${esc(n)}</code></th>${cols.map((c) => {
      const u = c.b.winappCommands[c.a];
      if (!u.recorded) return '<td><span class="muted">not recorded</span></td>';
      return `<td>${u.names[n]?.trials ?? 0} of ${u.recorded}</td>`;
    }).join('')}</tr>`).join('');
    return `<h3>winapp commands</h3><details class="more"><summary>Which winapp commands the agent ran, in how many runs</summary>${table('winapp commands by benchmark and setup', head, body)}</details>`;
  }

  function methodSection(r) {
    const commitLinks = (list) => list.map((c) => `<a href="${REPO}/commit/${esc(c)}"><code>${esc(c.slice(0, 8))}</code></a>`).join(', ') || NA;
    const benches = Object.entries(r.benchmarks);
    const day = (iso) => esc(iso?.slice(0, 10) ?? 'n/a');
    // One block per benchmark, so an entry that merges runs from different commits shows each.
    const perBench = benches.map(([n, b]) => `<dt><code>${esc(n)}</code></dt><dd>${b.trialCount} runs, ${day(b.firstStartedAt)} to ${day(b.lastStartedAt)}. Commit${b.commits.length === 1 ? '' : 's'}: ${commitLinks(b.commits)}. winapp version: ${b.winappVersions.length ? b.winappVersions.map(esc).join(', ') : 'not recorded'}. Scoring version: ${b.scoringVersions.map(esc).join(', ')}.</dd>`).join('');
    const rawUrl = `${D.rawDataUrl}/${encodeURIComponent(r.release ?? '')}`;
    const warnings = D.warnings.length
      ? `<details><summary>${D.warnings.length} data note(s) from the build</summary><ul>${D.warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></details>`
      : '<p>The build found no problems in the data files.</p>';
    return `<h3>How we measured</h3>
      <dl class="facts">
        <dt>Run</dt><dd>${esc(entryName(r))}${r.label ? ` <span class="muted">(release value${r.releases.length === 1 ? '' : 's'}: ${r.releases.map(esc).join(', ')})</span>` : ''}</dd>
        <dt>Models</dt><dd>${r.models.map(modelLabel).map(esc).join(', ')}</dd>
        ${perBench}
      </dl>
      <ul>
        <li>Three setups: ${[BASE, CLI, WITH].map((a) => `<strong>${esc(armLabel(a))}</strong> (<code>${esc(a)}</code>): ${esc(ARM_INFO[a].desc)}`).join(' ')} Each "change" is ${esc(armLabel(WITH).toLowerCase())} minus ${esc(armLabel(BASE).toLowerCase())}. The Skills tab shows only ${esc(armLabel(WITH).toLowerCase())}, because the other setups have no skills installed.</li>
        <li>In the <code>apps</code> benchmark an agent builds a complete WinUI app from a written brief. Each run is one agent attempt at one scenario, with one model and one setup.</li>
        <li>${esc(DEFS.valid)}</li>
        <li>${esc(DEFS.works)}</li>
        <li>${esc(DEFS.quality)}</li>
        <li>${esc(DEFS.requirements)}</li>
        <li>${esc(DEFS.pass)} Pass rates show a 95% confidence interval (Wilson).</li>
        <li>${esc(DEFS.design)}</li>
        <li>${esc(DEFS.credits)} No dollar prices are used.</li>
        <li>A scenario passed in ${Math.round(D.rules.sanityPassRate * 100)}% or more of valid runs ${esc(armLabel(BASE).toLowerCase())} is a sanity check: it can't show whether the tools help (<a href="${REPO}/issues/1253">#1253</a>). Sanity-check tasks are left out of the tasks headline; apps scenarios are flagged but kept.</li>
        <li>Flags: a model is flagged when its pass rate on a setup differs from the other models' by ${Math.round(D.rules.modelSplitPoints * 100)} points or more, or when it uses more than ${D.rules.costMultiple}x the credits it used on the other setups. Flags point to where to look; they are not results.</li>
        <li>Skill claims (Helps, Helps but costs more, No measurable difference, Not enough data) will come from fixed rules in <a href="${REPO}/issues/1182">#1182</a>. Until then every skill shows "Not enough data".</li>
      </ul>
      <p>Raw run files: ${!r.release || r.release === 'dev'
        ? 'development (<code>dev</code>) runs are not published.'
        : `<a href="${esc(rawUrl)}">${esc(rawUrl)}</a> (works once the <code>benchmark-data</code> branch exists).`} Format: <a href="${REPO}/blob/main/benchmarks/RESULTS-FORMAT.md">RESULTS-FORMAT.md</a>. Every number on this page: <a href="data.json">data.json</a>. To rerun: <a href="${REPO}/blob/main/benchmarks/apps/README.md">apps</a>, <a href="${REPO}/blob/main/benchmarks/tasks/README.md">tasks</a>.</p>
      ${warnings}`;
  }

  function renderDetails(r) {
    return `<h2>Details</h2>${outcomesSection(r)}${tasksSection(r)}${commandsSection(r)}${methodSection(r)}`;
  }

  // ---------- page ----------
  const current = () => D.releases.find((x) => x.id === state.release) ?? D.releases[0];
  const entryName = (r) => r.label ?? (r.id === 'dev' ? 'dev (development runs)' : r.id);

  function render() {
    const r = current();
    state.release = r.id;
    $('release-meta').textContent = `${r.firstStartedAt?.slice(0, 10) ?? ''} to ${r.lastStartedAt?.slice(0, 10) ?? ''}`;
    $('banner').innerHTML = r.releases.includes('dev')
      ? '<p class="banner">Development runs, not a release. Numbers may change.</p>'
      : '';
    $('panel-overview').innerHTML = renderOverview(r);
    $('panel-apps').innerHTML = renderApps(r);
    $('panel-skills').innerHTML = renderSkills(r);
    $('panel-details').innerHTML = renderDetails(r);
    drawTrends();
    $('footer').innerHTML = `Built ${esc(D.generatedAt.slice(0, 16).replace('T', ' '))} UTC. All numbers: <a href="data.json">data.json</a>.`;
    bindAppMetric();
  }

  // Trends depend on all runs, not the selected one. Its selectors are redrawn with the panel.
  function drawTrends() {
    $('panel-trends').innerHTML = renderTrends();
    for (const [id, key] of [['trend-series', 'series'], ['trend-model', 'trendModel'], ['trend-metric', 'trendMetric']]) {
      const sel = $(id);
      if (!sel) continue;
      sel.addEventListener('change', () => { state[key] = sel.value; drawTrends(); $(id)?.focus(); });
    }
  }

  // The selector is redrawn with its panel, so bind it after each draw.
  function bindAppMetric() {
    const sel = $('app-metric');
    if (!sel) return;
    sel.addEventListener('change', () => {
      state.appMetric = sel.value;
      $('panel-apps').innerHTML = renderApps(current());
      bindAppMetric();
      $('app-metric').focus();
    });
  }

  function selectTab(name, focus) {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    if (!tabs.some((t) => t.dataset.tab === name)) name = 'overview';
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
    history.replaceState(null, '', `#${new URLSearchParams({ release: state.release, tab: state.tab })}`);
  }

  function init() {
    if (!D || !D.releases?.length) {
      $('main').innerHTML = '<p>No benchmark data was found. Build the site with <code>build.mjs --data &lt;folder&gt;</code>.</p>';
      return;
    }
    const params = new URLSearchParams(location.hash.slice(1));
    // Entries arrive most recent run first, so the first one is the default.
    state.release = D.releases.some((r) => r.id === params.get('release')) ? params.get('release') : D.releases[0].id;
    const sel = $('release');
    sel.innerHTML = D.releases.map((r) => `<option value="${esc(r.id)}">${esc(entryName(r))}</option>`).join('');
    sel.value = state.release;
    sel.addEventListener('change', () => { state.release = sel.value; render(); updateHash(); });

    const tabs = [...document.querySelectorAll('[role="tab"]')];
    tabs.forEach((t, i) => {
      t.addEventListener('click', () => { selectTab(t.dataset.tab); updateHash(); });
      t.addEventListener('keydown', (e) => {
        const j = { ArrowRight: (i + 1) % tabs.length, ArrowLeft: (i - 1 + tabs.length) % tabs.length, Home: 0, End: tabs.length - 1 }[e.key];
        if (j === undefined) return;
        e.preventDefault();
        selectTab(tabs[j].dataset.tab, true);
        updateHash();
      });
    });
    render();
    selectTab(params.get('tab') ?? 'overview');
  }

  init();
})();
