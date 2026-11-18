/**
 * Per-run pinning of floating upstream refs (branches) to commit SHAs.
 *
 * The first trial in a run that resolves `owner/repo@ref` records the SHA in
 * `<runDir>/upstream-pins.json`; every later trial (including resumed runs)
 * checks out that SHA, so a long run can't silently mix plugin versions when
 * the upstream branch moves mid-run.
 */
import { existsSync, readFileSync, writeFileSync } from "fs";
import { join } from "path";

const PIN_FILE = "upstream-pins.json";
const SHA_RE = /^[0-9a-f]{40}$/i;

export function isFullSha(ref: string): boolean {
  return SHA_RE.test(ref);
}

function pinKey(repo: string, ref: string): string {
  return `${repo.toLowerCase()}@${ref}`;
}

function readPins(runDir: string): Record<string, string> {
  const p = join(runDir, PIN_FILE);
  if (!existsSync(p)) return {};
  try { return JSON.parse(readFileSync(p, "utf-8")) ?? {}; } catch { return {}; }
}

export function readPin(runDir: string, repo: string, ref: string): string | undefined {
  if (isFullSha(ref)) return undefined;
  const sha = readPins(runDir)[pinKey(repo, ref)];
  return sha && isFullSha(sha) ? sha : undefined;
}

/**
 * Record `sha` for `repo@ref` unless a pin already exists. Returns the pinned
 * SHA, which callers must use (it differs from `sha` when another trial won).
 * Read-modify-write is synchronous, so it is atomic within one Node process.
 */
export function pinRef(runDir: string, repo: string, ref: string, sha: string): string {
  if (isFullSha(ref)) return ref;
  const pins = readPins(runDir);
  const key = pinKey(repo, ref);
  if (pins[key] && isFullSha(pins[key])) return pins[key];
  pins[key] = sha;
  writeFileSync(join(runDir, PIN_FILE), JSON.stringify(pins, null, 2) + "\n");
  return sha;
}
