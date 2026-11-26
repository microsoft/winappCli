// "Try it" and "See it" for a PR: its CI build of winapp in a terminal, and the screenshots, videos or transcripts
// a review session captured. Builds and media live under the board's artifacts folder, never in the repo, and
// never replace the user's installed winapp: only the terminal the board opens puts the build first on PATH.
import { createHash } from "node:crypto";
import { copyFileSync, createReadStream, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { execFile, spawn } from "node:child_process";
import { extname, isAbsolute, join } from "node:path";
import { artifactsDir } from "./store.mjs";
import { downloadBuild, findBuildRun } from "./github.mjs";

const slug = (repo) => repo.replace("/", "__");
const KEEP_BUILDS = 4;

// ---- Media ("See it") -----------------------------------------------------------------------------------------

const MEDIA_TYPES = { ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".gif": "image/gif", ".webp": "image/webp", ".mp4": "video/mp4", ".webm": "video/webm" };
const MAX_MEDIA = 300 * 1024 * 1024;
export const mediaDir = (repo, pr) => join(artifactsDir(), "media", slug(repo), String(pr));

// Copy one file the session captured into the board's media folder. Returns its board name, or a warning.
function ingest(repo, pr, path, label) {
    if (typeof path !== "string" || !path) return {};
    if (!isAbsolute(path)) return { warning: `${label}: "${path}" isn't an absolute path` };
    if (!existsSync(path)) return { warning: `${label}: ${path} doesn't exist` };
    const ext = extname(path).toLowerCase();
    if (!MEDIA_TYPES[ext]) return { warning: `${label}: ${ext || "no extension"} isn't a supported image or video (png, jpg, gif, webp, mp4, webm)` };
    const st = statSync(path);
    if (st.size > MAX_MEDIA) return { warning: `${label}: ${Math.round(st.size / 1048576)} MB is over the 300 MB limit` };
    const name = createHash("sha256").update(`${path}|${st.size}|${st.mtimeMs}`).digest("hex").slice(0, 12) + ext;
    const dir = mediaDir(repo, pr);
    mkdirSync(dir, { recursive: true });
    if (!existsSync(join(dir, name))) copyFileSync(path, join(dir, name));
    return { name };
}

// Terminal transcripts: a session may pass a file instead of inline text. The board reads it as text.
const TEXT_EXTS = new Set([".txt", ".log", ".out", ".ansi", ".md"]);
const MAX_TEXT_FILE = 2 * 1024 * 1024;
function readTextFile(path, label) {
    if (!isAbsolute(path)) return { warning: `${label}: "${path}" isn't an absolute path` };
    if (!existsSync(path)) return { warning: `${label}: ${path} doesn't exist` };
    if (statSync(path).size > MAX_TEXT_FILE) return { warning: `${label}: ${path} is over 2 MB; pass the relevant part as text instead` };
    const text = readFileSync(path, "utf8").replace(/^\uFEFF/, "").replace(/\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*(\x07|\x1b\\)/g, "").replace(/\r\n/g, "\n");
    return { text };
}

// Replace file paths in review.media with board-served names, in place. Returns warnings.
export function ingestMedia(repo, pr, review) {
    const warnings = [];
    for (const [i, m] of (review.media || []).entries()) {
        const label = `media ${i + 1}${m.title ? ` ("${m.title}")` : ""}`;
        for (const [from, to, textKey] of [["path", "file", "text"], ["before", "beforeFile", "beforeText"], ["after", "afterFile", "afterText"]]) {
            if (!m[from]) continue;
            if (typeof m[from] === "string" && TEXT_EXTS.has(extname(m[from]).toLowerCase())) {
                const t = readTextFile(m[from], `${label} ${from}`);
                if (t.warning) warnings.push(t.warning);
                else if (m[textKey] === undefined) m[textKey] = t.text;
                delete m[from];
                continue;
            }
            const r = ingest(repo, pr, m[from], `${label} ${from}`);
            if (r.warning) warnings.push(r.warning);
            if (r.name) m[to] = r.name;
            delete m[from];
        }
        for (const k of ["beforeText", "afterText", "text"]) if (m[k] !== undefined) m[k] = String(m[k]).slice(0, 20000);
        if ((m.beforeText !== undefined || m.afterText !== undefined) && !m.file) m.kind = "comparison";
        for (const k of ["title", "caption"]) if (m[k] !== undefined) m[k] = String(m[k]).slice(0, 300);
        const has = m.file || m.beforeFile || m.afterFile || m.beforeText || m.afterText || m.text;
        if (!has) { warnings.push(`${label}: nothing to show (no file that could be copied, and no text)`); m.skip = true; }
    }
    review.media = (review.media || []).filter((m) => !m.skip);
    return warnings;
}

// Serve a media file, with byte ranges so videos can seek.
export function serveMedia(req, res, repo, pr, name) {
    if (!/^[0-9a-f]{12}\.(png|jpe?g|gif|webp|mp4|webm)$/.test(String(name))) { res.writeHead(400); return res.end(); }
    const file = join(mediaDir(repo, pr), name);
    if (!existsSync(file)) { res.writeHead(404); return res.end(); }
    const size = statSync(file).size;
    const type = MEDIA_TYPES[extname(name)];
    const m = String(req.headers.range || "").match(/^bytes=(\d*)-(\d*)$/);
    if (m && (m[1] || m[2])) {
        const start = m[1] ? Number(m[1]) : Math.max(0, size - Number(m[2]));
        const end = m[1] && m[2] ? Math.min(Number(m[2]), size - 1) : size - 1;
        if (start >= size || start > end) { res.writeHead(416, { "Content-Range": `bytes */${size}` }); return res.end(); }
        res.writeHead(206, { "Content-Type": type, "Content-Length": end - start + 1, "Content-Range": `bytes ${start}-${end}/${size}`, "Accept-Ranges": "bytes", "Cache-Control": "private, max-age=3600" });
        return createReadStream(file, { start, end }).pipe(res);
    }
    res.writeHead(200, { "Content-Type": type, "Content-Length": size, "Accept-Ranges": "bytes", "Cache-Control": "private, max-age=3600" });
    createReadStream(file).pipe(res);
}

// ---- The PR's build ("Try it") --------------------------------------------------------------------------------

export const buildDir = (repo, sha) => join(artifactsDir(), "builds", slug(repo), String(sha).slice(0, 12));
const jobs = new Map(); // repo#sha -> { status: "downloading" | "failed", step, error, at }
const runCache = new Map(); // repo#sha -> { at, run }

function readyBuild(repo, sha) {
    const dir = buildDir(repo, sha);
    if (!existsSync(join(dir, "winapp.exe"))) return null;
    let meta = {};
    try { meta = JSON.parse(readFileSync(join(dir, "build.json"), "utf8")); } catch { /* older download */ }
    return { dir, exe: join(dir, "winapp.exe"), version: meta.version || null, runUrl: meta.runUrl || null };
}

// What the Try it tab shows for this head: ready, downloading, available in CI, still building, failed, or none.
export async function tryStatus(repo, sha) {
    if (!sha) return { state: "none", reason: "The board doesn't know the PR's head commit yet." };
    const ready = readyBuild(repo, sha);
    if (ready) return { state: "ready", sha, version: ready.version, runUrl: ready.runUrl, dir: ready.dir };
    const job = jobs.get(`${repo}#${sha}`);
    if (job?.status === "downloading") return { state: "downloading", sha, step: job.step, since: job.at };
    const key = `${repo}#${sha}`;
    let run = runCache.get(key);
    if (!run || Date.now() - run.at > 60_000) {
        try { run = { at: Date.now(), run: await findBuildRun(repo, sha) }; } catch (e) { run = { at: Date.now(), run: { state: "error", error: e.message } }; }
        runCache.set(key, run);
    }
    const r = run.run;
    const failed = job?.status === "failed" ? job.error : null;
    if (r.state === "ready") return { state: "available", sha, runUrl: r.url, error: failed };
    if (r.state === "building") return { state: "building", sha, runUrl: r.url };
    if (r.state === "failed") return { state: "ci-failed", sha, runUrl: r.url, conclusion: r.conclusion };
    if (r.state === "error") return { state: "error", sha, error: r.error };
    return { state: "none", sha, reason: `No CI build for ${String(sha).slice(0, 7)} yet.` };
}

// Download the build in the background. onChange is called when the status changes.
export function startDownload(repo, sha, onChange = () => {}) {
    const key = `${repo}#${sha}`;
    if (jobs.get(key)?.status === "downloading" || readyBuild(repo, sha)) return false;
    const job = { status: "downloading", step: "Finding the CI build", at: new Date().toISOString() };
    jobs.set(key, job);
    onChange();
    (async () => {
        const run = await findBuildRun(repo, sha);
        if (run.state !== "ready") throw new Error(run.state === "building" ? "CI is still building this commit." : "There's no successful CI build of this commit.");
        const dir = buildDir(repo, sha);
        const { exe } = await downloadBuild(repo, run.runId, dir, undefined, (step) => { job.step = step; onChange(); });
        const version = await new Promise((resolve) => execFile(exe, ["--version"], { timeout: 30000, windowsHide: true }, (err, out) => resolve(err ? null : String(out).trim().split(/\r?\n/)[0])));
        writeFileSync(join(dir, "build.json"), JSON.stringify({ sha, version, runId: run.runId, runUrl: run.url, at: new Date().toISOString() }, null, 2));
        pruneBuilds(repo, sha);
        jobs.delete(key);
    })().catch((e) => { jobs.set(key, { status: "failed", error: e.message, at: new Date().toISOString() }); }).finally(onChange);
    return true;
}

// Keep the newest few builds per repo; each is about 130 MB.
function pruneBuilds(repo, keepSha) {
    const root = join(artifactsDir(), "builds", slug(repo));
    if (!existsSync(root)) return;
    const dirs = readdirSync(root).filter((d) => !d.endsWith(".part")).map((d) => ({ d, t: statSync(join(root, d)).mtimeMs })).sort((a, b) => b.t - a.t);
    for (const { d } of dirs.slice(KEEP_BUILDS)) if (d !== String(keepSha).slice(0, 12)) rmSync(join(root, d), { recursive: true, force: true });
}

let shellCache = null;
async function shell() {
    if (shellCache) return shellCache;
    const has = (exe) => new Promise((r) => execFile("where.exe", [exe], { windowsHide: true }, (err) => r(!err)));
    shellCache = (await has("pwsh.exe")) ? "pwsh" : "powershell";
    return shellCache;
}
const psq = (s) => `'${String(s).replace(/'/g, "''")}'`;

// A PowerShell script that puts the build first on PATH in this terminal only, and prints what to try.
export async function tryScript(repo, pr, sha, tryIt = []) {
    const b = readyBuild(repo, sha);
    if (!b) throw new Error("Get the build first.");
    // Commands like `winapp ui record -o beeps.mp4` write files, so start in a scratch folder rather than wherever
    // the extension happens to run.
    const work = join(artifactsDir(), "try", slug(repo), `pr${pr}`);
    mkdirSync(work, { recursive: true });
    const lines = [
        `$env:PATH = ${psq(b.dir + ";")} + $env:PATH`,
        `Set-Location -LiteralPath ${psq(work)}`,
        `$Host.UI.RawUI.WindowTitle = ${psq(`winapp from PR #${pr}`)}`,
        `Write-Host ''`,
        `Write-Host '  winapp ' -NoNewline; Write-Host (& winapp --version) -ForegroundColor Cyan -NoNewline; Write-Host ${psq(` from PR #${pr} @ ${String(sha).slice(0, 7)}`)}`,
        `Write-Host '  Only this terminal uses it. Your installed winapp is unchanged.' -ForegroundColor DarkGray`,
        `Write-Host ${psq(`  Files you create go to ${work}`)} -ForegroundColor DarkGray`,
        `Write-Host ''`,
    ];
    if (tryIt.length) {
        lines.push(`Write-Host '  Quick things to try:' -ForegroundColor Yellow`);
        for (const t of tryIt) {
            if (t.title) lines.push(`Write-Host ${psq(`  # ${t.title}`)} -ForegroundColor DarkGray`);
            lines.push(`Write-Host ${psq(`    ${t.command}`)}`);
        }
        lines.push(`Write-Host ''`);
    }
    const file = join(b.dir, `try-pr${pr}.ps1`);
    writeFileSync(file, "\uFEFF" + lines.join("\r\n") + "\r\n");
    const sh = await shell();
    return { file, work, command: `${sh} -NoLogo -NoExit -ExecutionPolicy Bypass -File "${file}"`, shell: sh, version: b.version };
}

// Open the try script in its own console window. `start` gives the shell a real console; spawning the shell
// directly from this process would hand it our NUL stdin, and -NoExit then quits at once. The command line goes to
// cmd verbatim: Node's own quoting turns the window title into "\"...\"", which start misreads and then hangs on a
// hidden error. Resolves when start has launched the shell, rejects with a plain reason otherwise.
export function openWindow(t, title, spawnFn = spawn, timeoutMs = 8000) {
    const clean = (s) => String(s).replace(/["%^&|<>]/g, "");
    const line = `start "${clean(title)}" /D "${t.work}" ${t.shell} -NoLogo -NoExit -ExecutionPolicy Bypass -File "${t.file}"`;
    return new Promise((resolve, reject) => {
        let child;
        try {
            child = spawnFn(process.env.ComSpec || "cmd.exe", ["/d", "/s", "/c", `"${line}"`], { windowsVerbatimArguments: true, detached: true, stdio: "ignore", windowsHide: true });
        } catch (e) { reject(new Error(`Couldn't start cmd.exe: ${e.message}`)); return; }
        const timer = setTimeout(() => {
            try { child.kill(); } catch { /* already gone */ }
            reject(new Error("Windows didn't open the window within a few seconds. Use Open a terminal with it instead."));
        }, timeoutMs);
        child.on("error", (e) => { clearTimeout(timer); reject(new Error(`Couldn't start cmd.exe: ${e.message}`)); });
        child.on("exit", (code) => {
            clearTimeout(timer);
            if (code === 0) resolve({ line });
            else reject(new Error(`Windows couldn't open ${t.shell} (exit code ${code}).`));
        });
    });
}
