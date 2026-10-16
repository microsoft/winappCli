/**
 * Local clone cache for GitHub repos referenced by `from_repo` agent setups.
 *
 * Cache layout:
 *   <root>/<owner>__<repo>__<sanitized-ref>/      (the working tree)
 *
 * `<root>` defaults to `~/.cache/win-dev-bench/repos/` (`%LOCALAPPDATA%` on Windows)
 * and can be overridden via the `BENCH_REPO_CACHE` env var.
 *
 * - Branch refs (anything that's not a 40-char hex SHA or a tag we've seen):
 *   `git fetch` on subsequent hits to pick up new commits.
 * - Tag refs and 40-char SHAs: clone once, never re-fetch.
 * - Setting `BENCH_REPO_FORCE_REFRESH=1` (or passing `force: true`) blows the
 *   cache for the requested ref and re-clones.
 */
import { spawn } from "child_process";
import { existsSync, mkdirSync, rmSync, statSync } from "fs";
import { homedir, platform } from "os";
import { join, resolve } from "path";

export interface RepoRef {
  /** "owner/name". */
  repo: string;
  /** Branch, tag, or 40-char SHA. */
  ref: string;
}

export interface ResolvedRepo {
  /** Absolute path to the working tree. */
  path: string;
  /** 40-char SHA the working tree is checked out to. */
  sha: string;
  /** Requested ref as supplied. */
  ref: string;
  /** Whether the cache was created or refreshed on this call. */
  refreshed: boolean;
}

export interface RefInfo {
  name: string;            // e.g. "main", "feature/foo", "v1.2.0", "pull/123/head"
  kind: "branch" | "tag" | "pr";
  sha?: string;            // 40-char SHA when known
  isDefault?: boolean;     // default branch
  title?: string;          // for PRs: PR title
  number?: number;         // for PRs
  author?: string;         // for PRs: author login
  draft?: boolean;         // for PRs
  committedAt?: string;    // ISO-8601 of HEAD commit (branches, when gh available) or PR last update
}

const SHA_RE = /^[0-9a-f]{40}$/i;
const isWindows = platform() === "win32";

/** Resolve the cache root, honoring `BENCH_REPO_CACHE` env var. */
export function getCacheRoot(): string {
  if (process.env.BENCH_REPO_CACHE) return resolve(process.env.BENCH_REPO_CACHE);
  const base = isWindows
    ? (process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local"))
    : join(homedir(), ".cache");
  return join(base, "win-dev-bench", "repos");
}

/** Filesystem-safe representation of a git ref. */
export function sanitizeRef(ref: string): string {
  return ref.replace(/\//g, "--").replace(/[^A-Za-z0-9_.\-]/g, "_");
}

/** Validate "owner/name" — accepts the GitHub naming charset. */
function parseRepo(repo: string): { owner: string; name: string } {
  const m = repo.match(/^([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+)$/);
  if (!m) throw new Error(`Invalid repo spec: "${repo}" (expected "owner/name")`);
  return { owner: m[1], name: m[2] };
}

/** Per-(repo, ref) cache directory path. */
export function cachePathFor(spec: RepoRef): string {
  const { owner, name } = parseRepo(spec.repo);
  return join(getCacheRoot(), `${owner}__${name}__${sanitizeRef(spec.ref)}`);
}

interface RunResult { stdout: string; stderr: string; code: number }

function run(cmd: string, args: string[], cwd?: string, timeoutMs = 120_000): Promise<RunResult> {
  return new Promise((res) => {
    const child = spawn(cmd, args, { cwd, shell: false, windowsHide: true });
    let stdout = "", stderr = "";
    let timed = false;
    const t = setTimeout(() => { timed = true; try { child.kill("SIGKILL"); } catch {} }, timeoutMs);
    child.stdout.on("data", (d) => { stdout += d.toString(); });
    child.stderr.on("data", (d) => { stderr += d.toString(); });
    child.on("error", (err) => { clearTimeout(t); res({ stdout, stderr: stderr + String(err), code: -1 }); });
    child.on("close", (code) => { clearTimeout(t); res({ stdout, stderr: timed ? stderr + "\n[timeout]" : stderr, code: code ?? -1 }); });
  });
}

async function git(args: string[], cwd?: string, timeoutMs = 120_000): Promise<RunResult> {
  return run("git", args, cwd, timeoutMs);
}

/** Heuristic: looks like a 40-char SHA. */
function looksLikeSha(ref: string): boolean {
  return SHA_RE.test(ref);
}

async function isTag(repoDir: string, ref: string): Promise<boolean> {
  const r = await git(["rev-parse", "--verify", `refs/tags/${ref}`], repoDir);
  return r.code === 0;
}

/** True for refs we never auto-refresh: tags + SHAs. */
async function isImmutableRef(repoDir: string, ref: string): Promise<boolean> {
  if (looksLikeSha(ref)) return true;
  return isTag(repoDir, ref);
}

/**
 * Clone (or refresh) the repo at the given ref into the per-ref cache dir.
 * Returns the working-tree path and resolved HEAD SHA.
 */
/**
 * Clone (or refresh) the repo at the given ref into the per-ref cache dir.
 * Returns the working-tree path and resolved HEAD SHA.
 *
 * Concurrency: multiple trials in the same process commonly call this with
 * the same (repo, ref) — e.g. 5 iterations of the same agent running in
 * parallel. Git operations on the same working tree race on Windows
 * (.git/index.lock, pack files held open) and the loser hits EPERM. We
 * serialize per cache dir with an in-process promise mutex.
 */
const resolveLocks = new Map<string, Promise<unknown>>();
export async function resolveRepo(spec: RepoRef, opts: { force?: boolean } = {}): Promise<ResolvedRepo> {
  if (!spec.repo) throw new Error("resolveRepo: repo is required");
  if (!spec.ref) throw new Error("resolveRepo: ref is required");
  const cacheDir = cachePathFor(spec);
  // Chain onto any in-flight call for the same cache dir.
  const prior = resolveLocks.get(cacheDir) ?? Promise.resolve();
  const run = prior.catch(() => undefined).then(() => resolveRepoInner(spec, opts, cacheDir));
  resolveLocks.set(cacheDir, run);
  try {
    return await run;
  } finally {
    if (resolveLocks.get(cacheDir) === run) resolveLocks.delete(cacheDir);
  }
}

/** rmSync with maxRetries — defends against Windows AV / lingering handles. */
function safeRm(path: string): void {
  rmSync(path, { recursive: true, force: true, maxRetries: 5, retryDelay: 250 });
}

async function resolveRepoInner(spec: RepoRef, opts: { force?: boolean }, cacheDir: string): Promise<ResolvedRepo> {
  // SHA checkouts are immutable and shared by concurrent trials of a run, so
  // a forced refresh must never delete one out from under a running session.
  const force = (opts.force || process.env.BENCH_REPO_FORCE_REFRESH === "1") && !looksLikeSha(spec.ref);
  const { owner, name } = parseRepo(spec.repo);
  const url = `https://github.com/${owner}/${name}.git`;
  let refreshed = false;

  if (force && existsSync(cacheDir)) {
    safeRm(cacheDir);
  }

  if (!existsSync(cacheDir) || !existsSync(join(cacheDir, ".git"))) {
    mkdirSync(getCacheRoot(), { recursive: true });
    // For SHAs we need full history (or at least the commit) — partial clone + fetch by SHA.
    if (looksLikeSha(spec.ref)) {
      const init = await git(["init", "--quiet", cacheDir]);
      if (init.code !== 0) throw new Error(`git init failed for ${cacheDir}: ${init.stderr}`);
      const addRemote = await git(["remote", "add", "origin", url], cacheDir);
      if (addRemote.code !== 0) throw new Error(`git remote add failed: ${addRemote.stderr}`);
      const fetch = await git(["fetch", "--depth", "1", "origin", spec.ref], cacheDir, 300_000);
      if (fetch.code !== 0) throw new Error(`git fetch ${spec.ref} failed: ${fetch.stderr}`);
      const co = await git(["checkout", "--quiet", spec.ref], cacheDir);
      if (co.code !== 0) throw new Error(`git checkout ${spec.ref} failed: ${co.stderr}`);
    } else {
      // Try a branch/tag clone first.
      const cl = await git(
        ["clone", "--depth", "1", "--filter", "blob:none", "--branch", spec.ref, "--single-branch", url, cacheDir],
        undefined,
        300_000,
      );
      if (cl.code !== 0) {
        // Fallback: PR refs (e.g. "pull/123/head") aren't reachable via --branch; init + fetch.
        if (existsSync(cacheDir)) safeRm(cacheDir);
        const init = await git(["init", "--quiet", cacheDir]);
        if (init.code !== 0) throw new Error(`git init failed for ${cacheDir}: ${init.stderr}`);
        const addRemote = await git(["remote", "add", "origin", url], cacheDir);
        if (addRemote.code !== 0) throw new Error(`git remote add failed: ${addRemote.stderr}`);
        const fetch = await git(["fetch", "--depth", "1", "origin", `${spec.ref}:refs/bench/${sanitizeRef(spec.ref)}`], cacheDir, 300_000);
        if (fetch.code !== 0) {
          throw new Error(`git fetch ${spec.ref} failed (also tried clone): ${fetch.stderr || cl.stderr}`);
        }
        const co = await git(["checkout", "--quiet", `refs/bench/${sanitizeRef(spec.ref)}`], cacheDir);
        if (co.code !== 0) throw new Error(`git checkout ${spec.ref} failed: ${co.stderr}`);
      }
    }
    refreshed = true;
  } else if (!(await isImmutableRef(cacheDir, spec.ref))) {
    // Existing branch checkout — pull new commits.
    const fetch = await git(["fetch", "--depth", "1", "origin", spec.ref], cacheDir, 180_000);
    if (fetch.code === 0) {
      const prev = (await git(["rev-parse", "HEAD"], cacheDir)).stdout.trim();
      const reset = await git(["reset", "--hard", "FETCH_HEAD"], cacheDir);
      if (reset.code !== 0) {
        // Non-fatal — keep stale checkout rather than failing the trial.
      } else {
        const cur = (await git(["rev-parse", "HEAD"], cacheDir)).stdout.trim();
        if (cur && cur !== prev) refreshed = true;
      }
    }
  }

  const head = await git(["rev-parse", "HEAD"], cacheDir);
  if (head.code !== 0) throw new Error(`git rev-parse HEAD failed in ${cacheDir}: ${head.stderr}`);
  return {
    path: cacheDir,
    sha: head.stdout.trim(),
    ref: spec.ref,
    refreshed,
  };
}

/** Like resolveRepo but returns `<repoPath>/<subpath>` and verifies it exists. */
export async function resolveRepoSubpath(
  spec: RepoRef,
  subpath: string,
  opts: { force?: boolean } = {},
): Promise<ResolvedRepo & { subpath: string; subpathFull: string }> {
  const r = await resolveRepo(spec, opts);
  const full = join(r.path, subpath);
  if (!existsSync(full) || !statSync(full).isDirectory()) {
    throw new Error(
      `Subpath "${subpath}" not found in ${spec.repo}@${spec.ref} (cache: ${r.path})`,
    );
  }
  return { ...r, subpath, subpathFull: full };
}

/** True when `gh` CLI is available and authenticated for github.com. */
async function ghAuthenticated(): Promise<boolean> {
  const r = await run("gh", ["auth", "status", "--hostname", "github.com"], undefined, 15_000);
  return r.code === 0;
}

/**
 * List refs available on the remote. Uses `gh api` when authenticated for
 * richer info (PR titles, default branch), falls back to `git ls-remote`.
 */
export async function listRefs(
  repo: string,
  kinds: ("branches" | "tags" | "prs")[] = ["branches"],
): Promise<RefInfo[]> {
  const { owner, name } = parseRepo(repo);
  const out: RefInfo[] = [];
  const useGh = await ghAuthenticated();

  if (useGh) {
    if (kinds.includes("branches")) {
      // Use GraphQL so we can get committedDate in a single request (the REST
      // /branches endpoint omits it — needs N follow-ups).
      const query = `query($owner:String!,$name:String!,$after:String){
        repository(owner:$owner,name:$name){
          defaultBranchRef{name}
          refs(refPrefix:"refs/heads/",first:100,after:$after){
            pageInfo{hasNextPage endCursor}
            nodes{name target{... on Commit{oid committedDate}}}
          }
        }
      }`;
      let defaultBranch = "main";
      let after: string | null = null;
      let safety = 50; // up to 5,000 branches
      while (safety-- > 0) {
        const args = [
          "api", "graphql",
          "-f", `query=${query}`,
          "-f", `owner=${owner}`,
          "-f", `name=${name}`,
        ];
        if (after) args.push("-f", `after=${after}`);
        const r = await run("gh", args, undefined, 30_000);
        if (r.code !== 0) break;
        try {
          const j = JSON.parse(r.stdout);
          const repoNode = j?.data?.repository;
          if (!repoNode) break;
          if (repoNode.defaultBranchRef?.name) defaultBranch = repoNode.defaultBranchRef.name;
          for (const n of repoNode.refs?.nodes || []) {
            if (!n?.name) continue;
            out.push({
              name: n.name,
              kind: "branch",
              sha: n.target?.oid,
              committedAt: n.target?.committedDate,
              isDefault: n.name === defaultBranch,
            });
          }
          const pi = repoNode.refs?.pageInfo;
          if (!pi?.hasNextPage) break;
          after = pi.endCursor;
        } catch { break; }
      }
      // Fix up isDefault retroactively in case the default branch arrived in a
      // later page than its sibling rows.
      for (const r of out) if (r.kind === "branch") r.isDefault = r.name === defaultBranch;
    }
    if (kinds.includes("tags")) {
      const tg = await run("gh", ["api", "--paginate", `repos/${owner}/${name}/tags`, "--jq", ".[] | {name, sha:.commit.sha}"], undefined, 60_000);
      if (tg.code === 0) {
        for (const line of tg.stdout.split("\n").filter(Boolean)) {
          try {
            const j = JSON.parse(line);
            out.push({ name: j.name, kind: "tag", sha: j.sha });
          } catch {}
        }
      }
    }
    if (kinds.includes("prs")) {
      const pr = await run("gh", ["api", "--paginate", `repos/${owner}/${name}/pulls?state=open&per_page=100`, "--jq", ".[] | {number, title, headRef:.head.ref, headSha:.head.sha, author:.user.login, draft, updatedAt:.updated_at}"], undefined, 60_000);
      if (pr.code === 0) {
        for (const line of pr.stdout.split("\n").filter(Boolean)) {
          try {
            const j = JSON.parse(line);
            // Use the pull/N/head ref so PRs from forks can be fetched.
            out.push({ name: `pull/${j.number}/head`, kind: "pr", sha: j.headSha, number: j.number, title: j.title, author: j.author, draft: !!j.draft, committedAt: j.updatedAt });
          } catch {}
        }
      }
    }
    return out;
  }

  // Fallback: git ls-remote (no titles, no PR support beyond what's on the remote).
  const url = `https://github.com/${owner}/${name}.git`;
  if (kinds.includes("branches")) {
    const r = await run("git", ["ls-remote", "--heads", url], undefined, 30_000);
    if (r.code === 0) {
      for (const ln of r.stdout.split("\n").filter(Boolean)) {
        const [sha, ref] = ln.split(/\s+/);
        const m = ref?.match(/^refs\/heads\/(.+)$/);
        if (m) out.push({ name: m[1], kind: "branch", sha, isDefault: m[1] === "main" || m[1] === "master" });
      }
    }
  }
  if (kinds.includes("tags")) {
    const r = await run("git", ["ls-remote", "--tags", url], undefined, 30_000);
    if (r.code === 0) {
      for (const ln of r.stdout.split("\n").filter(Boolean)) {
        const [sha, ref] = ln.split(/\s+/);
        const m = ref?.match(/^refs\/tags\/([^^]+)$/);
        if (m) out.push({ name: m[1], kind: "tag", sha });
      }
    }
  }
  return out;
}
