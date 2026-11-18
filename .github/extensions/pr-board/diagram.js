// Adapted from angelser's pr-arch-review, https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe
// (web/diagram.js, used unchanged apart from this header).
// Readable diagram renderer: layered (Sugiyama-style) layout for flow/layers diagrams and
// a column layout for sequence diagrams. Emits plain SVG styled by styles.css.
const SVGNS = "http://www.w3.org/2000/svg";

const LABEL_SIZE = 18;
const SUB_SIZE = 14;
const LABEL_LINE = 24;
const SUB_LINE = 19;
const EDGE_LABEL_SIZE = 14;
const PAD_X = 22;
const PAD_Y = 16;
const MAX_TEXT_W = 240;
const MIN_NODE_W = 150;
const NODE_GAP = 56;
const MARGIN = 32;

let measureCtx;
function fontFamily() {
    return getComputedStyle(document.body).fontFamily || "Segoe UI, sans-serif";
}
function textWidth(text, size, weight = 400) {
    measureCtx ??= document.createElement("canvas").getContext("2d");
    measureCtx.font = `${weight} ${size}px ${fontFamily()}`;
    return measureCtx.measureText(text).width;
}
function wrap(text, size, weight, maxW, maxLines = 3) {
    const words = String(text ?? "").split(/\s+/).filter(Boolean);
    const lines = [];
    let cur = "";
    for (const w of words) {
        const next = cur ? `${cur} ${w}` : w;
        if (cur && textWidth(next, size, weight) > maxW) {
            lines.push(cur);
            cur = w;
        } else cur = next;
    }
    if (cur) lines.push(cur);
    if (lines.length > maxLines) {
        const kept = lines.slice(0, maxLines);
        kept[maxLines - 1] += "…";
        return kept;
    }
    return lines.length ? lines : [""];
}

function el(tag, attrs = {}, parent) {
    const e = document.createElementNS(SVGNS, tag);
    for (const [k, v] of Object.entries(attrs)) if (v !== undefined && v !== null) e.setAttribute(k, v);
    parent?.appendChild(e);
    return e;
}

export function visibleInView(status, view) {
    if (view === "before") return status !== "added";
    if (view === "after") return status !== "removed";
    return true;
}

export function hasChanges(d) {
    const items = d.kind === "sequence" ? [...(d.participants ?? []), ...(d.messages ?? [])] : [...(d.nodes ?? []), ...(d.edges ?? [])];
    return items.some((x) => x.status === "added" || x.status === "removed");
}

/* ------------------------------------------------------------------ */
/* Layered layout                                                       */
/* ------------------------------------------------------------------ */

function sizeNode(n) {
    const label = wrap(n.label ?? n.id, LABEL_SIZE, 600, MAX_TEXT_W, 3);
    const sub = n.sublabel ? wrap(n.sublabel, SUB_SIZE, 400, MAX_TEXT_W, 2) : [];
    const textW = Math.max(...label.map((l) => textWidth(l, LABEL_SIZE, 600)), ...sub.map((l) => textWidth(l, SUB_SIZE, 400)), 0);
    let w = Math.max(MIN_NODE_W, Math.ceil(textW) + PAD_X * 2);
    let h = PAD_Y * 2 + label.length * LABEL_LINE + (sub.length ? 4 + sub.length * SUB_LINE : 0);
    if (n.kind === "datastore") h += 18;
    if (n.kind === "actor") h += 26;
    if (n.kind === "queue") w += 16;
    return { label, sub, w, h };
}

function breakCycles(ids, edges) {
    const out = new Map(ids.map((id) => [id, []]));
    edges.forEach((e, i) => out.get(e.from).push([e.to, i]));
    const state = new Map();
    const reversed = new Set();
    const visit = (u) => {
        state.set(u, 1);
        for (const [v, i] of out.get(u)) {
            if (state.get(v) === 1) reversed.add(i);
            else if (!state.has(v)) visit(v);
        }
        state.set(u, 2);
    };
    for (const id of ids) if (!state.has(id)) visit(id);
    return reversed;
}

function longestPathLayers(ids, dag) {
    const indeg = new Map(ids.map((id) => [id, 0]));
    const out = new Map(ids.map((id) => [id, []]));
    const inn = new Map(ids.map((id) => [id, []]));
    for (const [u, v] of dag) {
        indeg.set(v, indeg.get(v) + 1);
        out.get(u).push(v);
        inn.get(v).push(u);
    }
    const layer = new Map(ids.map((id) => [id, 0]));
    const queue = ids.filter((id) => indeg.get(id) === 0);
    const order = [];
    while (queue.length) {
        const u = queue.shift();
        order.push(u);
        for (const v of out.get(u)) {
            layer.set(v, Math.max(layer.get(v), layer.get(u) + 1));
            indeg.set(v, indeg.get(v) - 1);
            if (indeg.get(v) === 0) queue.push(v);
        }
    }
    // Pull nodes down next to their successors (sources otherwise float to the top).
    for (const u of [...order].reverse()) {
        const succ = out.get(u);
        if (succ.length && inn.get(u).length === 0) layer.set(u, Math.min(...succ.map((v) => layer.get(v))) - 1);
    }
    return layer;
}

function countCrossings(layers, adjDown, pos) {
    let c = 0;
    for (let l = 0; l < layers.length - 1; l++) {
        const segs = [];
        for (const u of layers[l]) for (const v of adjDown.get(u) ?? []) segs.push([pos.get(u), pos.get(v)]);
        for (let i = 0; i < segs.length; i++)
            for (let j = i + 1; j < segs.length; j++) if ((segs[i][0] - segs[j][0]) * (segs[i][1] - segs[j][1]) < 0) c++;
    }
    return c;
}

// Pool-adjacent-violators: least-squares positions honouring order + min separation.
function isotonic(desired, seps) {
    const n = desired.length;
    const off = new Array(n).fill(0);
    for (let i = 1; i < n; i++) off[i] = off[i - 1] + seps[i - 1];
    const blocks = [];
    for (let i = 0; i < n; i++) {
        blocks.push({ sum: desired[i] - off[i], cnt: 1, start: i });
        while (blocks.length > 1) {
            const a = blocks[blocks.length - 2];
            const b = blocks[blocks.length - 1];
            if (a.sum / a.cnt <= b.sum / b.cnt) break;
            a.sum += b.sum;
            a.cnt += b.cnt;
            blocks.pop();
        }
    }
    const out = new Array(n);
    for (const b of blocks) for (let k = 0; k < b.cnt; k++) out[b.start + k] = b.sum / b.cnt + off[b.start + k];
    return out;
}

function layoutGraph(diagram, view) {
    const horizontal = diagram.direction === "LR";
    const swimlanes = diagram.kind === "layers";
    const nodes = (diagram.nodes ?? []).filter((n) => visibleInView(n.status, view));
    const ids = nodes.map((n) => n.id);
    const idSet = new Set(ids);
    const edges = (diagram.edges ?? []).filter((e) => idSet.has(e.from) && idSet.has(e.to) && visibleInView(e.status, view));
    const groups = diagram.groups ?? [];
    const groupIndex = new Map(groups.map((g, i) => [g.id, i]));

    const info = new Map();
    for (const n of nodes) info.set(n.id, { node: n, ...sizeNode(n), dummy: false });
    const crossSize = (id) => (horizontal ? info.get(id).h : info.get(id).w);
    const mainSize = (id) => (horizontal ? info.get(id).w : info.get(id).h);

    // 1. Layer assignment.
    const loops = edges.filter((e) => e.from === e.to);
    const real = edges.filter((e) => e.from !== e.to);
    let layerOf;
    if (swimlanes) {
        const unknown = groups.length;
        layerOf = new Map(nodes.map((n) => [n.id, groupIndex.has(n.group) ? groupIndex.get(n.group) : unknown]));
    } else {
        const rev = breakCycles(ids, real);
        layerOf = longestPathLayers(
            ids,
            real.map((e, i) => (rev.has(i) ? [e.to, e.from] : [e.from, e.to])),
        );
    }
    const minLayer = Math.min(0, ...layerOf.values());
    for (const [k, v] of layerOf) layerOf.set(k, v - minLayer);
    const layerCount = Math.max(0, ...layerOf.values()) + 1;

    // 2. Dummy chains for edges spanning several layers.
    const chains = []; // per edge: list of ids from `from` to `to`
    const adj = new Map(); // undirected adjacency between neighbouring layers
    const link = (a, b) => {
        if (!adj.has(a)) adj.set(a, []);
        if (!adj.has(b)) adj.set(b, []);
        adj.get(a).push(b);
        adj.get(b).push(a);
    };
    let dummyN = 0;
    for (const e of real) {
        const la = layerOf.get(e.from);
        const lb = layerOf.get(e.to);
        const chain = [e.from];
        if (la !== lb) {
            const step = lb > la ? 1 : -1;
            const mid = Math.floor((Math.abs(lb - la) - 1) / 2);
            let k = 0;
            for (let l = la + step; l !== lb; l += step, k++) {
                const id = `__d${dummyN++}`;
                const labelW = k === mid && e.label ? textWidth(e.label, EDGE_LABEL_SIZE, 500) + 24 : 0;
                info.set(id, { dummy: true, w: horizontal ? 10 : Math.max(16, labelW), h: horizontal ? 24 : 10, labelW });
                layerOf.set(id, l);
                chain.push(id);
            }
            chain.push(e.to);
            for (let i = 0; i < chain.length - 1; i++) link(chain[i], chain[i + 1]);
        } else chain.push(e.to);
        chains.push({ edge: e, chain });
    }

    // 3. Ordering within layers (barycenter sweeps, keep best).
    const layers = Array.from({ length: layerCount }, () => []);
    const firstSeen = new Map();
    let seq = 0;
    const seen = (id) => {
        if (!firstSeen.has(id)) firstSeen.set(id, seq++);
    };
    for (const n of nodes) seen(n.id);
    for (const { chain } of chains) chain.forEach(seen);
    for (const [id, l] of layerOf) layers[l].push(id);
    const grpKey = (id) => (info.get(id).dummy ? 0 : groupIndex.get(info.get(id).node.group) ?? 0);
    for (const L of layers) L.sort((a, b) => grpKey(a) - grpKey(b) || firstSeen.get(a) - firstSeen.get(b));

    const pos = new Map();
    const reindex = () => layers.forEach((L) => L.forEach((id, i) => pos.set(id, i)));
    reindex();
    const adjDown = new Map();
    for (let l = 0; l < layerCount - 1; l++)
        for (const u of layers[l]) adjDown.set(u, (adj.get(u) ?? []).filter((v) => layerOf.get(v) === l + 1));
    let best = layers.map((L) => [...L]);
    let bestC = countCrossings(layers, adjDown, pos);
    for (let it = 0; it < 12 && bestC > 0; it++) {
        const down = it % 2 === 0;
        const range = down ? [...Array(layerCount).keys()].slice(1) : [...Array(layerCount).keys()].reverse().slice(1);
        for (const l of range) {
            const ref = down ? l - 1 : l + 1;
            const bc = new Map();
            for (const id of layers[l]) {
                const nb = (adj.get(id) ?? []).filter((v) => layerOf.get(v) === ref);
                bc.set(id, nb.length ? nb.reduce((s, v) => s + pos.get(v), 0) / nb.length : pos.get(id));
            }
            layers[l].sort((a, b) => bc.get(a) - bc.get(b) || pos.get(a) - pos.get(b));
            layers[l].forEach((id, i) => pos.set(id, i));
        }
        const c = countCrossings(layers, adjDown, pos);
        if (c < bestC) {
            bestC = c;
            best = layers.map((L) => [...L]);
        }
    }
    best.forEach((L, i) => (layers[i] = L));
    reindex();

    // 4. Main-axis positions (layer bands sized by content and edge labels).
    const laneHeader = swimlanes ? (horizontal ? 44 : 0) : 0;
    const laneLeft = swimlanes && !horizontal ? Math.max(120, ...groups.map((g) => textWidth(g.label ?? g.id, 15, 700) + 40)) : 0;
    const layerMain = layers.map((L) => Math.max(0, ...L.filter((id) => !info.get(id).dummy).map(mainSize), L.length ? 24 : 0));
    const gapAfter = [];
    for (let l = 0; l < layerCount - 1; l++) {
        const labels = chains.filter(({ edge, chain }) => edge.label && Math.min(layerOf.get(chain[0]), layerOf.get(chain[1])) === l && chain.length === 2);
        const maxLabel = Math.max(0, ...labels.map(({ edge }) => textWidth(edge.label, EDGE_LABEL_SIZE, 500)));
        gapAfter.push(horizontal ? Math.max(110, maxLabel + 70) : labels.length ? 104 : 76);
    }
    if (swimlanes) for (let l = 0; l < gapAfter.length; l++) gapAfter[l] += 24;
    const mainCenter = [];
    let cursor = MARGIN + laneHeader + (swimlanes ? 20 : 0);
    for (let l = 0; l < layerCount; l++) {
        mainCenter.push(cursor + layerMain[l] / 2);
        cursor += layerMain[l] + (gapAfter[l] ?? 0);
    }
    const mainExtent = cursor + (swimlanes ? 20 : 0) + MARGIN;

    // 5. Cross-axis positions: iterate toward neighbour barycenters with isotonic packing.
    const cross = new Map();
    const sepBetween = (a, b) => {
        const da = info.get(a).dummy;
        const db = info.get(b).dummy;
        const gap = da && db ? 18 : da || db ? 30 : NODE_GAP;
        return crossSize(a) / 2 + crossSize(b) / 2 + gap;
    };
    for (const L of layers) {
        let c = 0;
        L.forEach((id, i) => {
            if (i > 0) c += sepBetween(L[i - 1], id);
            cross.set(id, c);
        });
        const shift = -c / 2;
        L.forEach((id) => cross.set(id, cross.get(id) + shift));
    }
    for (let it = 0; it < 24; it++) {
        const order = it % 2 === 0 ? [...layers.keys()] : [...layers.keys()].reverse();
        for (const l of order) {
            const L = layers[l];
            if (!L.length) continue;
            const desired = L.map((id) => {
                const nb = adj.get(id) ?? [];
                if (!nb.length) return cross.get(id);
                return nb.reduce((s, v) => s + cross.get(v), 0) / nb.length;
            });
            const seps = L.slice(1).map((id, i) => sepBetween(L[i], id));
            isotonic(desired, seps).forEach((x, i) => cross.set(L[i], x));
        }
    }
    let minCross = Infinity;
    let maxCross = -Infinity;
    for (const [id, c] of cross) {
        minCross = Math.min(minCross, c - crossSize(id) / 2);
        maxCross = Math.max(maxCross, c + crossSize(id) / 2);
    }
    const crossStart = MARGIN + (swimlanes && !horizontal ? laneLeft : 0) + (swimlanes ? 16 : 0);
    for (const [id, c] of cross) cross.set(id, c - minCross + crossStart);
    const crossExtent = maxCross - minCross + crossStart + MARGIN + (swimlanes ? 16 : 0);

    const xy = (id) => {
        const m = mainCenter[layerOf.get(id)];
        const c = cross.get(id);
        return horizontal ? { x: m, y: c } : { x: c, y: m };
    };
    const placed = new Map();
    for (const [id, inf] of info) placed.set(id, { ...inf, ...xy(id), layer: layerOf.get(id) });

    // 6. Edge routes with ports spread along node sides.
    const routes = chains.map(({ edge, chain }) => ({ edge, pts: chain.map((id) => ({ id, ...xy(id) })) }));
    const portUse = new Map(); // `${id}:${side}` -> [{route, end, other}]
    const sideOf = (from, to) => {
        if (layerOf.get(from) === layerOf.get(to)) return horizontal ? (cross.get(to) > cross.get(from) ? "b" : "t") : cross.get(to) > cross.get(from) ? "r" : "l";
        const fwd = layerOf.get(to) > layerOf.get(from);
        return horizontal ? (fwd ? "r" : "l") : fwd ? "b" : "t";
    };
    for (const r of routes) {
        const ids2 = r.pts.map((p) => p.id);
        const startSide = sideOf(ids2[0], ids2[1]);
        const endSide = sideOf(ids2[ids2.length - 1], ids2[ids2.length - 2]);
        r.startSide = startSide;
        r.endSide = endSide;
        for (const [end, side, otherId] of [["start", startSide, ids2[1]], ["end", endSide, ids2[ids2.length - 2]]]) {
            const nodeId = end === "start" ? ids2[0] : ids2[ids2.length - 1];
            const k = `${nodeId}:${side}`;
            if (!portUse.has(k)) portUse.set(k, []);
            portUse.get(k).push({ r, end, other: otherId });
        }
    }
    for (const [k, uses] of portUse) {
        const [nodeId, side] = k.split(":");
        const p = placed.get(nodeId);
        const alongX = side === "t" || side === "b";
        uses.sort((a, b) => (alongX ? placed.get(a.other).x - placed.get(b.other).x : placed.get(a.other).y - placed.get(b.other).y));
        const span = (alongX ? p.w : p.h) * 0.6;
        uses.forEach((u, i) => {
            const off = uses.length === 1 ? 0 : -span / 2 + (span * i) / (uses.length - 1);
            const pt = {
                x: side === "l" ? p.x - p.w / 2 : side === "r" ? p.x + p.w / 2 : p.x + off,
                y: side === "t" ? p.y - p.h / 2 : side === "b" ? p.y + p.h / 2 : p.y + off,
                side,
            };
            if (u.end === "start") u.r.pts[0] = { ...u.r.pts[0], ...pt };
            else u.r.pts[u.r.pts.length - 1] = { ...u.r.pts[u.r.pts.length - 1], ...pt };
        });
    }

    const lanes = [];
    if (swimlanes) {
        for (let l = 0; l < layerCount; l++) {
            const g = groups[l] ?? { id: "__other", label: "Other" };
            if (!layers[l].length && !groups[l]) continue;
            const half = layerMain[l] / 2 + 28;
            lanes.push({ label: g.label ?? g.id, from: mainCenter[l] - half, to: mainCenter[l] + half, index: l });
        }
    }

    const width = horizontal ? mainExtent : crossExtent;
    const height = horizontal ? crossExtent : mainExtent;
    return { placed, routes, loops, lanes, width, height, horizontal, laneLeft };
}

/* ------------------------------------------------------------------ */
/* Drawing                                                              */
/* ------------------------------------------------------------------ */

function pathThrough(pts, horizontal) {
    const ctrl = (p, dirSign, d) => {
        // tangent along the port side normal for endpoints, along main axis for dummies
        const side = p.side;
        if (side === "l") return { x: p.x - d, y: p.y };
        if (side === "r") return { x: p.x + d, y: p.y };
        if (side === "t") return { x: p.x, y: p.y - d };
        if (side === "b") return { x: p.x, y: p.y + d };
        return horizontal ? { x: p.x + dirSign * d, y: p.y } : { x: p.x, y: p.y + dirSign * d };
    };
    let d = `M ${pts[0].x} ${pts[0].y}`;
    for (let i = 0; i < pts.length - 1; i++) {
        const a = pts[i];
        const b = pts[i + 1];
        const dist = Math.max(30, (horizontal ? Math.abs(b.x - a.x) : Math.abs(b.y - a.y)) * 0.5, Math.hypot(b.x - a.x, b.y - a.y) * 0.25);
        const sign = horizontal ? Math.sign(b.x - a.x) || 1 : Math.sign(b.y - a.y) || 1;
        const c1 = ctrl(a, sign, dist);
        const c2 = ctrl(b.side ? b : { ...b, side: undefined }, -sign, dist);
        d += ` C ${c1.x} ${c1.y} ${c2.x} ${c2.y} ${b.x} ${b.y}`;
    }
    return d;
}

function drawDefs(svg) {
    const defs = el("defs", {}, svg);
    for (const s of ["unchanged", "added", "modified", "removed", "external", "hl"]) {
        const m = el("marker", { id: `arrow-${s}`, viewBox: "0 0 12 12", refX: 10, refY: 6, markerWidth: 9, markerHeight: 9, orient: "auto-start-reverse", markerUnits: "userSpaceOnUse" }, defs);
        el("path", { d: "M 0 0 L 12 6 L 0 12 z", class: `arrowhead st-${s}` }, m);
    }
    const f = el("filter", { id: "node-shadow", x: "-10%", y: "-10%", width: "120%", height: "130%" }, defs);
    el("feDropShadow", { dx: 0, dy: 2, stdDeviation: 3, "flood-opacity": 0.18 }, f);
}

function drawNodeShape(g, p, n) {
    const x = p.x - p.w / 2;
    const y = p.y - p.h / 2;
    const cls = "node-shape";
    switch (n.kind) {
        case "datastore": {
            const ry = 9;
            el("path", { class: cls, d: `M ${x} ${y + ry} A ${p.w / 2} ${ry} 0 0 1 ${x + p.w} ${y + ry} V ${y + p.h - ry} A ${p.w / 2} ${ry} 0 0 1 ${x} ${y + p.h - ry} Z` }, g);
            el("path", { class: "node-detail", d: `M ${x} ${y + ry} A ${p.w / 2} ${ry} 0 0 0 ${x + p.w} ${y + ry}` }, g);
            break;
        }
        case "queue":
            el("rect", { class: cls, x, y, width: p.w, height: p.h, rx: 8 }, g);
            for (const k of [1, 2]) el("line", { class: "node-detail", x1: x + p.w - 8 * k - 4, y1: y + 6, x2: x + p.w - 8 * k - 4, y2: y + p.h - 6 }, g);
            break;
        case "api":
            el("rect", { class: cls, x, y, width: p.w, height: p.h, rx: Math.min(p.h / 2, 26) }, g);
            break;
        case "ui":
            el("rect", { class: cls, x, y, width: p.w, height: p.h, rx: 8 }, g);
            el("line", { class: "node-detail", x1: x, y1: y + 12, x2: x + p.w, y2: y + 12 }, g);
            break;
        case "actor":
            el("rect", { class: cls, x, y, width: p.w, height: p.h, rx: 14 }, g);
            el("circle", { class: "node-detail", cx: p.x, cy: y + 16, r: 8 }, g);
            break;
        default:
            el("rect", { class: cls, x, y, width: p.w, height: p.h, rx: 10 }, g);
    }
}

function statusLabel(s) {
    return { added: "NEW", modified: "CHANGED", removed: "REMOVED", external: "EXTERNAL" }[s];
}

function drawGraph(svg, diagram, layout, opts) {
    const { placed, routes, loops, lanes, horizontal } = layout;
    const groups = diagram.groups ?? [];
    const groupColor = new Map(groups.map((g, i) => [g.id, i % 8]));

    const laneLayer = el("g", { class: "lanes" }, svg);
    for (const lane of lanes) {
        if (horizontal) {
            el("rect", { class: `lane lane-${lane.index % 2}`, x: lane.from, y: 8, width: lane.to - lane.from, height: layout.height - 16, rx: 12 }, laneLayer);
            el("text", { class: "lane-label", x: (lane.from + lane.to) / 2, y: 34, "text-anchor": "middle" }, laneLayer).textContent = lane.label;
        } else {
            el("rect", { class: `lane lane-${lane.index % 2}`, x: 8, y: lane.from, width: layout.width - 16, height: lane.to - lane.from, rx: 12 }, laneLayer);
            const t = el("text", { class: "lane-label", x: 28, y: (lane.from + lane.to) / 2, "dominant-baseline": "middle" }, laneLayer);
            t.textContent = lane.label;
        }
    }

    const edgeLayer = el("g", { class: "edges" }, svg);
    const labelLayer = el("g", { class: "edge-labels" }, svg);
    const nodeLayer = el("g", { class: "nodes" }, svg);
    const labelRects = [];
    const nodeRects = [...placed.values()].filter((p) => !p.dummy).map((p) => ({ x: p.x - p.w / 2, y: p.y - p.h / 2, w: p.w, h: p.h }));
    const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

    for (const r of routes) {
        const e = r.edge;
        const st = e.status ?? "unchanged";
        const g = el("g", { class: `edge st-${st} style-${e.style ?? "sync"}`, "data-from": e.from, "data-to": e.to }, edgeLayer);
        const d = pathThrough(r.pts, horizontal);
        el("path", { class: "edge-hit", d }, g);
        el("path", {
            class: "edge-line",
            d,
            "marker-end": `url(#arrow-${st})`,
            "marker-start": e.bidirectional ? `url(#arrow-${st})` : undefined,
        }, g);
        if (e.label) {
            let mid;
            if (r.pts.length > 2) {
                const dummies = r.pts.slice(1, -1);
                mid = dummies.find((p) => placed.get(p.id)?.labelW) ?? dummies[Math.floor((dummies.length - 1) / 2)];
            } else mid = { x: (r.pts[0].x + r.pts[1].x) / 2, y: (r.pts[0].y + r.pts[1].y) / 2 };
            const tw = textWidth(e.label, EDGE_LABEL_SIZE, 500);
            const rect = { x: mid.x - tw / 2 - 9, y: mid.y - 13, w: tw + 18, h: 26 };
            for (let k = 0; k < 12; k++) {
                const hit = [...labelRects, ...nodeRects].find((o) => overlaps(rect, o));
                if (!hit) break;
                if (horizontal) rect.y = hit.y + hit.h + 3 > rect.y && rect.y >= hit.y ? hit.y + hit.h + 3 : hit.y - rect.h - 3;
                else rect.x = rect.x >= hit.x ? hit.x + hit.w + 4 : hit.x - rect.w - 4;
            }
            labelRects.push(rect);
            const lg = el("g", { class: `edge-label st-${st}`, "data-from": e.from, "data-to": e.to }, labelLayer);
            el("rect", { x: rect.x, y: rect.y, width: rect.w, height: rect.h, rx: 13 }, lg);
            const t = el("text", { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2 + 1, "text-anchor": "middle", "dominant-baseline": "middle" }, lg);
            t.textContent = e.label;
        }
    }
    for (const e of loops) {
        const p = placed.get(e.from);
        if (!p) continue;
        const st = e.status ?? "unchanged";
        const x = p.x + p.w / 2;
        const g = el("g", { class: `edge st-${st}`, "data-from": e.from, "data-to": e.to }, edgeLayer);
        el("path", { class: "edge-line", d: `M ${x} ${p.y - 10} C ${x + 50} ${p.y - 40} ${x + 50} ${p.y + 40} ${x} ${p.y + 10}`, "marker-end": `url(#arrow-${st})` }, g);
        if (e.label) {
            const t = el("text", { class: "loop-label", x: x + 46, y: p.y + 5 }, labelLayer);
            t.textContent = e.label;
        }
    }

    const ends = new Map();
    for (const r of routes) for (const pt of [r.pts[0], r.pts[r.pts.length - 1]]) {
        if (!ends.has(pt.id)) ends.set(pt.id, []);
        ends.get(pt.id).push(pt);
    }

    for (const n of diagram.nodes ?? []) {
        const p = placed.get(n.id);
        if (!p) continue;
        const st = n.status ?? "unchanged";
        const g = el("g", {
            class: `node st-${st} kind-${n.kind ?? "component"}${opts.selected === n.id ? " selected" : ""}`,
            "data-id": n.id,
            tabindex: 0,
            role: "button",
            "aria-label": `${n.label} ${statusLabel(st) ?? ""}`,
        }, nodeLayer);
        drawNodeShape(g, p, n);
        if (n.group && groupColor.has(n.group)) {
            const x = p.x - p.w / 2;
            const y = p.y - p.h / 2;
            el("rect", { class: `group-stripe gc-${groupColor.get(n.group)}`, x: x + 1.5, y: y + 8, width: 5, height: p.h - 16, rx: 2.5 }, g);
        }
        const extra = n.kind === "datastore" ? 9 : n.kind === "actor" ? 13 : 0;
        const textH = p.label.length * LABEL_LINE + (p.sub.length ? 4 + p.sub.length * SUB_LINE : 0);
        let ty = p.y - textH / 2 + extra + LABEL_LINE * 0.72;
        const t = el("text", { class: "node-label", "text-anchor": "middle" }, g);
        for (const line of p.label) {
            el("tspan", { x: p.x, y: ty }, t).textContent = line;
            ty += LABEL_LINE;
        }
        if (p.sub.length) {
            ty += 4 - LABEL_LINE + SUB_LINE;
            const s = el("text", { class: "node-sublabel", "text-anchor": "middle" }, g);
            for (const line of p.sub) {
                el("tspan", { x: p.x, y: ty }, s).textContent = line;
                ty += SUB_LINE;
            }
        }
        const tag = statusLabel(st);
        if (tag && opts.view === "diff") {
            const tw = textWidth(tag, 11, 700) + 14;
            const left = p.x - p.w / 2 + 10, right = p.x + p.w / 2 - tw - 22;
            const top = p.y - p.h / 2 - 11, bottom = p.y + p.h / 2 - 9;
            // Keep the tag off arrowheads: slide along top then bottom border to the first slot with no edge endpoint.
            const pts = ends.get(n.id) ?? [];
            const free = ([x, y]) => !pts.some((q) => q.x > x - 8 && q.x < x + tw + 8 && q.y > y - 10 && q.y < y + 30);
            const slots = [];
            for (const y of [top, bottom]) for (let x = left; x <= right; x += 6) slots.push([x, y]);
            const [bx, by] = slots.find(free) ?? [p.x - p.w / 2 - tw - 6, p.y - 10];
            const b = el("g", { class: `status-tag st-${st}` }, g);
            el("rect", { x: bx, y: by, width: tw, height: 20, rx: 10 }, b);
            el("text", { x: bx + tw / 2, y: by + 10.5, "text-anchor": "middle", "dominant-baseline": "middle" }, b).textContent = tag;
        }
        const badge = opts.badges?.get(n.id);
        if (badge) {
            const bx = p.x + p.w / 2 - 4;
            const by = p.y - p.h / 2 + 2;
            const b = el("g", { class: `badge sev-${badge.worst}` }, g);
            el("circle", { cx: bx, cy: by, r: 13 }, b);
            el("text", { x: bx, y: by + 1, "text-anchor": "middle", "dominant-baseline": "middle" }, b).textContent = String(badge.count);
            el("title", {}, b).textContent = `${badge.count} finding(s), worst: ${badge.worst}`;
        }
        if (n.description) el("title", {}, g).textContent = n.description;
    }
}

/* ------------------------------------------------------------------ */
/* Sequence diagrams                                                    */
/* ------------------------------------------------------------------ */

function drawSequence(svg, diagram, opts) {
    const parts = (diagram.participants ?? []).filter((p) => visibleInView(p.status, opts.view));
    const ids = new Set(parts.map((p) => p.id));
    const msgs = (diagram.messages ?? []).filter((m) => m.divider || (ids.has(m.from) && ids.has(m.to) && visibleInView(m.status, opts.view)));
    const sized = parts.map((p) => ({ p, ...sizeNode({ label: p.label, sublabel: p.sublabel }) }));
    // Column spacing must fit the widest message label between adjacent columns.
    const index = new Map(parts.map((p, i) => [p.id, i]));
    const needed = new Array(Math.max(0, parts.length - 1)).fill(0);
    for (const m of msgs) {
        if (m.divider || m.from === m.to || !m.label) continue;
        const a = Math.min(index.get(m.from), index.get(m.to));
        const b = Math.max(index.get(m.from), index.get(m.to));
        const w = (textWidth(m.label, 15, 500) + 40) / (b - a);
        for (let k = a; k < b; k++) needed[k] = Math.max(needed[k], w);
    }
    const xs = [];
    let x = MARGIN + (sized[0]?.w ?? 0) / 2;
    sized.forEach((s, i) => {
        if (i > 0) x += Math.max(s.w / 2 + sized[i - 1].w / 2 + 50, needed[i - 1], 200);
        xs.push(x);
    });
    const headH = Math.max(0, ...sized.map((s) => s.h));
    let y = MARGIN + headH + 40;
    const rows = msgs.map((m) => {
        const h = m.divider ? 54 : m.from === m.to ? 82 : m.note ? 76 : 60;
        const r = { m, y };
        y += h;
        return r;
    });
    const bottom = y + 10;
    const width = (xs[xs.length - 1] ?? 0) + (sized[sized.length - 1]?.w ?? 0) / 2 + MARGIN + 120;
    const height = bottom + headH + MARGIN;
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);

    const lifelines = el("g", { class: "lifelines" }, svg);
    sized.forEach((s, i) => {
        el("line", { class: "lifeline", x1: xs[i], y1: MARGIN + headH, x2: xs[i], y2: bottom }, lifelines);
    });
    const drawHead = (s, i, top) => {
        const st = s.p.status ?? "unchanged";
        const g = el("g", { class: `node participant st-${st}`, "data-id": s.p.id, tabindex: 0, role: "button" }, svg);
        const p = { x: xs[i], y: top + s.h / 2, w: s.w, h: s.h, label: s.label, sub: s.sub };
        drawNodeShape(g, p, {});
        let ty = top + PAD_Y + LABEL_LINE * 0.72;
        const t = el("text", { class: "node-label", "text-anchor": "middle" }, g);
        for (const line of s.label) {
            el("tspan", { x: xs[i], y: ty }, t).textContent = line;
            ty += LABEL_LINE;
        }
        if (s.sub.length) {
            ty += 4 - LABEL_LINE + SUB_LINE;
            const sub = el("text", { class: "node-sublabel", "text-anchor": "middle" }, g);
            for (const line of s.sub) {
                el("tspan", { x: xs[i], y: ty }, sub).textContent = line;
                ty += SUB_LINE;
            }
        }
        const tag = statusLabel(st);
        if (tag && opts.view === "diff" && top === MARGIN) {
            const tw = textWidth(tag, 11, 700) + 14;
            const b = el("g", { class: `status-tag st-${st}` }, g);
            el("rect", { x: xs[i] - s.w / 2 + 10, y: top - 11, width: tw, height: 20, rx: 10 }, b);
            el("text", { x: xs[i] - s.w / 2 + 10 + tw / 2, y: top - 0.5, "text-anchor": "middle", "dominant-baseline": "middle" }, b).textContent = tag;
        }
        const badge = opts.badges?.get(s.p.id);
        if (badge && top === MARGIN) {
            const b = el("g", { class: `badge sev-${badge.worst}` }, g);
            el("circle", { cx: xs[i] + s.w / 2 - 4, cy: top + 2, r: 13 }, b);
            el("text", { x: xs[i] + s.w / 2 - 4, y: top + 3, "text-anchor": "middle", "dominant-baseline": "middle" }, b).textContent = String(badge.count);
        }
    };
    sized.forEach((s, i) => drawHead(s, i, MARGIN));
    sized.forEach((s, i) => drawHead(s, i, bottom));

    let n = 0;
    for (const { m, y: ry } of rows) {
        if (m.divider) {
            const g = el("g", { class: "seq-divider" }, svg);
            el("line", { x1: MARGIN / 2, y1: ry + 24, x2: width - MARGIN / 2, y2: ry + 24 }, g);
            const tw = textWidth(m.divider, 14, 700) + 24;
            el("rect", { x: MARGIN, y: ry + 11, width: tw, height: 26, rx: 6 }, g);
            el("text", { x: MARGIN + tw / 2, y: ry + 25, "text-anchor": "middle", "dominant-baseline": "middle" }, g).textContent = m.divider;
            continue;
        }
        n++;
        const st = m.status ?? "unchanged";
        const kind = m.kind ?? "sync";
        const g = el("g", { class: `edge msg st-${st} style-${kind === "sync" ? "sync" : "async"}`, "data-from": m.from, "data-to": m.to }, svg);
        const x1 = xs[index.get(m.from)];
        const x2 = xs[index.get(m.to)];
        const ay = ry + 34;
        if (m.from === m.to) {
            el("path", { class: "edge-line", d: `M ${x1} ${ay} h 60 v 30 h -56`, "marker-end": `url(#arrow-${st})` }, g);
            el("text", { class: "msg-label", x: x1 + 70, y: ay + 18 }, g).textContent = m.label ?? "";
        } else {
            const dir = Math.sign(x2 - x1);
            el("path", { class: "edge-line", d: `M ${x1 + dir * 4} ${ay} L ${x2 - dir * 4} ${ay}`, "marker-end": `url(#arrow-${st})` }, g);
            const t = el("text", { class: "msg-label", x: (x1 + x2) / 2, y: ay - 10, "text-anchor": "middle" }, g);
            t.textContent = m.label ?? "";
        }
        const num = el("g", { class: `msg-num st-${st}` }, g);
        el("circle", { cx: x1 - 18 * (Math.sign(x2 - x1) || 1), cy: ay, r: 11 }, num);
        el("text", { x: x1 - 18 * (Math.sign(x2 - x1) || 1), y: ay + 1, "text-anchor": "middle", "dominant-baseline": "middle" }, num).textContent = String(n);
        if (m.note) {
            const nx = Math.min(x1, x2) + 10;
            el("text", { class: "msg-note", x: nx, y: ay + 26 }, g).textContent = m.note;
        }
        if (kind === "return") g.classList.add("style-return");
    }
    return { width, height };
}

/* ------------------------------------------------------------------ */
/* Public API                                                           */
/* ------------------------------------------------------------------ */

/**
 * Render a diagram spec to an SVG element.
 * opts: { view: 'diff'|'before'|'after', selected, badges: Map<nodeId,{count,worst}>, onSelect(nodeId) }
 */
export function renderDiagram(diagram, opts = {}) {
    const svg = document.createElementNS(SVGNS, "svg");
    svg.setAttribute("class", `diagram view-${opts.view ?? "diff"}`);
    svg.setAttribute("xmlns", SVGNS);
    drawDefs(svg);
    let size;
    if (diagram.kind === "sequence") size = drawSequence(svg, diagram, opts);
    else {
        let layout;
        if (diagram.direction === "TB" || diagram.direction === "LR") layout = layoutGraph(diagram, opts.view ?? "diff");
        else {
            // Auto: pick the orientation that is most readable in the available viewport.
            const tb = layoutGraph({ ...diagram, direction: "TB" }, opts.view ?? "diff");
            const lr = layoutGraph({ ...diagram, direction: "LR" }, opts.view ?? "diff");
            const fit = opts.fitTo ?? { w: 1200, h: 700 };
            const scale = (l) => Math.min(fit.w / l.width, fit.h / l.height, 1);
            // Side panels scroll vertically; avoid horizontal panning unless LR fits entirely at 100%.
            if (scale(lr) >= 1 && scale(tb) < 1) layout = lr;
            else if (tb.width <= fit.w) layout = tb;
            else layout = scale(lr) > scale(tb) * 1.08 ? lr : tb;
        }
        svg.setAttribute("viewBox", `0 0 ${layout.width} ${layout.height}`);
        drawGraph(svg, diagram, layout, opts);
        size = { width: layout.width, height: layout.height };
    }
    svg.dataset.width = size.width;
    svg.dataset.height = size.height;

    const neighbours = (id) => {
        const s = new Set([id]);
        for (const e of svg.querySelectorAll(".edge")) {
            if (e.dataset.from === id) s.add(e.dataset.to);
            if (e.dataset.to === id) s.add(e.dataset.from);
        }
        return s;
    };
    const setFocus = (id) => {
        svg.classList.toggle("has-focus", !!id);
        const nb = id ? neighbours(id) : new Set();
        for (const n of svg.querySelectorAll(".node")) n.classList.toggle("focus", nb.has(n.dataset.id));
        for (const e of svg.querySelectorAll(".edge, .edge-label")) e.classList.toggle("focus", !!id && (e.dataset.from === id || e.dataset.to === id));
    };
    for (const n of svg.querySelectorAll(".node")) {
        n.addEventListener("mouseenter", () => setFocus(n.dataset.id));
        n.addEventListener("mouseleave", () => setFocus(opts.selected ?? null));
        n.addEventListener("click", () => opts.onSelect?.(n.dataset.id));
        n.addEventListener("keydown", (ev) => {
            if (ev.key === "Enter" || ev.key === " ") opts.onSelect?.(n.dataset.id);
        });
    }
    if (opts.selected) setFocus(opts.selected);
    return svg;
}

/** Self-contained SVG string (styles inlined from the live document) for download. */
export function exportSvg(svg) {
    const clone = svg.cloneNode(true);
    const props = ["fill", "stroke", "stroke-width", "stroke-dasharray", "opacity", "font-size", "font-weight", "font-family", "filter"];
    const src = svg.querySelectorAll("*");
    const dst = clone.querySelectorAll("*");
    src.forEach((node, i) => {
        const cs = getComputedStyle(node);
        dst[i].setAttribute("style", props.map((p) => `${p}:${cs.getPropertyValue(p)}`).join(";"));
    });
    clone.setAttribute("width", svg.dataset.width);
    clone.setAttribute("height", svg.dataset.height);
    const bg = getComputedStyle(document.body).backgroundColor;
    clone.insertBefore(Object.assign(document.createElementNS(SVGNS, "rect"), {}), clone.firstChild.nextSibling);
    const rect = clone.childNodes[1];
    rect.setAttribute("width", "100%");
    rect.setAttribute("height", "100%");
    rect.setAttribute("fill", bg);
    return new XMLSerializer().serializeToString(clone);
}
