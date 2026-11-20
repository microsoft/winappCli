// A tiny headless Edge driver over the Chrome DevTools Protocol (no npm dependencies).
// It records console errors, uncaught exceptions and failed requests so every test can assert there were none.
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const EDGE = [process.env.PR_BOARD_BROWSER, "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Google/Chrome/Application/chrome.exe"].filter(Boolean);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Dark theme tokens, standing in for what the app injects into the canvas.
const DARK = ":root{--background-color-default:#1f1f1f;--text-color-default:#e6edf3;--text-color-muted:#9198a1;--border-color-default:#3d444d;--true-color-red:#f85149;--true-color-red-muted:rgba(248,81,73,.15);--true-color-green:#3fb950;--true-color-green-muted:rgba(63,185,80,.15);--true-color-blue:#4493f8;--true-color-blue-muted:rgba(68,147,248,.18);--true-color-yellow:#d29922;--true-color-yellow-muted:rgba(210,153,34,.15);--true-color-purple:#ab7df8;--true-color-purple-muted:rgba(171,125,248,.15);--true-color-orange:#f0883e;--true-color-orange-muted:rgba(240,136,62,.15);color-scheme:dark}";

export async function launch() {
    const exe = EDGE.find((p) => existsSync(p));
    if (!exe) throw new Error("No Edge or Chrome found. Set PR_BOARD_BROWSER to a Chromium browser.");
    const dir = mkdtempSync(join(tmpdir(), "prb-edge-"));
    const port = 9300 + (process.pid % 500);
    const proc = spawn(exe, ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check", `--remote-debugging-port=${port}`, `--user-data-dir=${dir}`, "about:blank"], { stdio: "ignore" });
    let targets;
    for (let i = 0; i < 80 && !targets; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(150); } }
    if (!targets) throw new Error("The browser didn't start");
    const ws = new WebSocket(targets.find((t) => t.type === "page").webSocketDebuggerUrl);
    await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
    let id = 0;
    const pending = new Map();
    const page = {
        problems: [], // console errors, exceptions and failed requests since the last clear
        requests: [],
        allowStatus: new Set(), // "METHOD path status" combinations a test expects
    };
    ws.addEventListener("message", (m) => {
        const d = JSON.parse(m.data);
        if (d.id && pending.has(d.id)) { const { resolve, reject } = pending.get(d.id); pending.delete(d.id); d.error ? reject(new Error(d.error.message)) : resolve(d.result); return; }
        const p = d.params;
        if (d.method === "Runtime.exceptionThrown") page.problems.push(`exception: ${p.exceptionDetails.exception?.description || p.exceptionDetails.text}`);
        if (d.method === "Runtime.consoleAPICalled" && p.type === "error") page.problems.push(`console.error: ${p.args.map((a) => a.value ?? a.description).join(" ")}`);
        if (d.method === "Network.requestWillBeSent") page.requests.push({ id: p.requestId, method: p.request.method, path: new URL(p.request.url).pathname, body: p.request.postData || "" });
        if (d.method === "Network.responseReceived" && p.response.status >= 400) {
            const r = page.requests.find((x) => x.id === p.requestId);
            const key = `${r?.method} ${r?.path} ${p.response.status}`;
            if (!page.allowStatus.has(key) && !/favicon/.test(p.response.url)) page.problems.push(`HTTP ${key}`);
        }
        if (d.method === "Network.loadingFailed" && !p.canceled && p.type !== "EventSource" && p.blockedReason !== "inspector" && p.errorText !== "net::ERR_BLOCKED_BY_CLIENT") page.problems.push(`request failed: ${p.errorText} (${page.requests.find((x) => x.id === p.requestId)?.path})`);
        if (d.method === "Fetch.requestPaused") {
            if (p.request.url.startsWith(page.origin)) send("Fetch.continueRequest", { requestId: p.requestId }).catch(() => {});
            else {
                // Avatars come from github.com/<login>.png; Chrome doesn't always report them as images.
                if (p.resourceType !== "Image" && !/^https:\/\/github\.com\/[\w-]+\.png/.test(p.request.url)) page.problems.push(`the page tried to reach ${p.request.url.slice(0, 120)}`);
                send("Fetch.failRequest", { requestId: p.requestId, errorReason: "BlockedByClient" }).catch(() => {});
            }
        }
    });
    const send = (method, params = {}) => new Promise((resolve, reject) => { const i = ++id; pending.set(i, { resolve, reject }); ws.send(JSON.stringify({ id: i, method, params })); });
    await send("Runtime.enable"); await send("Network.enable"); await send("Page.enable");

    Object.assign(page, {
        send,
        async eval(expr) {
            const r = await send("Runtime.evaluate", { expression: typeof expr === "function" ? `(${expr})()` : expr, returnByValue: true, awaitPromise: true });
            if (r.exceptionDetails) throw new Error(`in page: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
            return r.result.value;
        },
        async size(width, height = 1000) { await send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false }); },
        async goto(url) {
            await send("Page.navigate", { url });
            await page.waitFor(`!!document.querySelector(".ov-title")`, 20000, "the board to render");
        },
        async waitFor(expr, ms = 5000, what = expr) {
            const end = Date.now() + ms;
            for (;;) {
                try { if (await page.eval(expr)) return; } catch { /* page navigating */ }
                if (Date.now() > end) throw new Error(`Timed out after ${ms}ms waiting for ${what}`);
                await sleep(60);
            }
        },
        async theme(name) {
            await page.eval(`(()=>{let s=document.getElementById('test-theme');if(!s){s=document.createElement('style');s.id='test-theme';document.head.append(s)}s.textContent=${JSON.stringify(name === "dark" ? DARK : "")};document.documentElement.dataset.colorMode=${JSON.stringify(name)}})()`);
        },
        async box(selector) {
            const b = await page.eval(`(()=>{const e=document.querySelector(${JSON.stringify(selector)}); if(!e) return null; e.scrollIntoView({block:"nearest"}); const r=e.getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2,w:r.width,h:r.height}})()`);
            if (!b) throw new Error(`No element matches ${selector}`);
            return b;
        },
        async mouse(type, x, y, buttons = 1) { await send("Input.dispatchMouseEvent", { type, x, y, button: "left", buttons, clickCount: 1 }); },
        // A real click: the mouse goes down and up at the element's center.
        async click(selector, { at } = {}) {
            const { x, y } = at || (await page.box(selector));
            await page.mouse("mouseMoved", x, y, 0);
            await page.mouse("mousePressed", x, y);
            await page.mouse("mouseReleased", x, y, 0);
            await sleep(80);
        },
        async drag(from, to, steps = 6) {
            await page.mouse("mouseMoved", from.x, from.y, 0);
            await page.mouse("mousePressed", from.x, from.y);
            for (let i = 1; i <= steps; i++) await page.mouse("mouseMoved", from.x + ((to.x - from.x) * i) / steps, from.y + ((to.y - from.y) * i) / steps);
            await page.mouse("mouseReleased", to.x, to.y, 0);
            await sleep(80);
        },
        async key(key, { code = key.length === 1 ? `Key${key.toUpperCase()}` : key, text = key.length === 1 ? key : undefined, keyCode } = {}) {
            const vk = keyCode ?? ({ Escape: 27, Enter: 13, ArrowDown: 40, ArrowUp: 38, Tab: 9 }[key] ?? key.toUpperCase().charCodeAt(0));
            await send("Input.dispatchKeyEvent", { type: "keyDown", key, code, text, windowsVirtualKeyCode: vk });
            await send("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: vk });
            await sleep(60);
        },
        async type(text) { await send("Input.insertText", { text }); },
        // Let the page reach only this origin. Other requests fail; only blocked images are tolerated.
        async offline(origin) { page.origin = origin; await send("Fetch.enable", { patterns: [{ urlPattern: "*" }] }); },
        async screenshot() { return Buffer.from((await send("Page.captureScreenshot", { format: "png" })).data, "base64"); },
        async html() { return page.eval("document.documentElement.outerHTML"); },
        clear() { page.problems.length = 0; page.allowStatus.clear(); },
        async close() {
            try { ws.close(); } catch { /* already closed */ }
            proc.kill();
            await sleep(300);
            try { rmSync(dir, { recursive: true, force: true }); } catch { /* the browser may still hold files */ }
        },
    });
    return page;
}
