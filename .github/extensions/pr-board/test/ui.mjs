// PR board UI tests. Run from the extension folder:
//
//   node test/ui.mjs                 all offline tests (fake GitHub, fake agent, isolated state)
//   node test/ui.mjs --grep peek     only tests whose name contains "peek"
//   node test/ui.mjs --live          read-only smoke test of your real board (real gh, real state)
//
// Each test starts from the same seeded board and a fresh page. A test fails on any console error, uncaught
// exception, failed request, GitHub write, or GitHub call the fake world doesn't know. On failure, a
// screenshot, the page's HTML and the error go to test/output/.
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { startBoard } from "./harness/board.mjs";
import { launch } from "./harness/browser.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const OUT = join(HERE, "output");
const args = process.argv.slice(2);
const grep = args.includes("--grep") ? args[args.indexOf("--grep") + 1].toLowerCase().split("|") : null;
const live = args.includes("--live");

const tests = [];
export const test = (name, fn, opts = {}) => tests.push({ name, fn, ...opts });

const { register } = await import(live ? "./live.mjs" : "./cases.mjs");
register(test);

rmSync(OUT, { recursive: true, force: true });
mkdirSync(join(OUT, "screens"), { recursive: true });
const selected = tests.filter((t) => !grep || grep.some((g) => t.name.toLowerCase().includes(g)));
const started = Date.now();
const board = await startBoard({ live });
const page = await launch();
const { origin } = new URL(board.url);
// Offline means offline: the page may only talk to the board's loopback server (avatars are refused).
if (!live) await page.offline(origin);

let passed = 0;
const failures = [];
for (const t of selected) {
    const t0 = Date.now();
    const slug = t.name.replace(/[^\w]+/g, "-").replace(/^-|-$/g, "").toLowerCase().slice(0, 80);
    try {
        board.reset();
        if (board.world) { board.world.fault = null; board.world.mutations.length = 0; board.world.unmocked.length = 0; board.world.allowMutations = false; }
        board.host.sends.length = 0;
        board.host.sendError = null;
        page.clear();
        // A test that clicks a link which opens a new tab leaves this page in the background, where timers slow down.
        await page.send("Page.bringToFront");
        await page.size(t.width || 1040, t.height || 1000);
        await page.send("Page.navigate", { url: "about:blank" });
        await page.goto(board.url);
        await page.eval(`localStorage.clear()`);
        await page.goto(board.url);
        await page.theme(t.theme || "light");
        const ctx = { page, board, world: board.world, host: board.host, origin, shot: async (name) => writeFileSync(join(OUT, "screens", `${name}.png`), await page.screenshot()) };
        await t.fn(ctx);
        if (await page.eval(`document.visibilityState`) !== "visible") throw new Error("The board went to the background: something opened a new tab (a click landed on a link?)");
        if (page.problems.length) throw new Error(`The page reported problems:\n  ${page.problems.join("\n  ")}`);
        if (board.world?.unmocked.length) throw new Error(`Unmocked GitHub calls:\n  ${board.world.unmocked.map((a) => "gh " + a.join(" ").slice(0, 140)).join("\n  ")}`);
        if (board.world?.mutations.length && !t.allowMutations) throw new Error(`The board tried to write to GitHub:\n  ${board.world.mutations.map((a) => "gh " + a.join(" ").slice(0, 140)).join("\n  ")}`);
        if (board.liveWrites.length) throw new Error(`The board tried to write to GitHub (refused):\n  ${board.liveWrites.map((a) => "gh " + a.join(" ").slice(0, 140)).join("\n  ")}`);
        passed++;
        console.log(`  ✓ ${t.name} (${Date.now() - t0}ms)`);
    } catch (e) {
        failures.push({ name: t.name, error: e });
        console.log(`  ✗ ${t.name}\n      ${String(e.stack || e).split("\n").slice(0, 6).join("\n      ")}`);
        try {
            writeFileSync(join(OUT, `${slug}.png`), await page.screenshot());
            writeFileSync(join(OUT, `${slug}.html`), await page.html());
            writeFileSync(join(OUT, `${slug}.txt`), `${String(e.stack || e)}\n\nPage problems:\n${page.problems.join("\n")}\n`);
        } catch { /* the page is gone */ }
    } finally {
        if (t.changesGitHub && board.world) await board.resetWorld().catch((e) => console.log(`      (couldn't reset the fake repo: ${e.message})`));
    }
}
await page.close();
await board.close();
const secs = ((Date.now() - started) / 1000).toFixed(0);
console.log(`\n${passed} passed, ${failures.length} failed, ${selected.length} total (${secs}s)${failures.length ? `\nFailure screenshots and HTML: ${OUT}` : ""}`);
process.exit(failures.length ? 1 : 0);
