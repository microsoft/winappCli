// Finds release folders under one or more data roots and reads their trial files.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, basename } from 'node:path';
import { checkTrial, isReleaseName } from './aggregate.mjs';

// A release folder is any folder named `dev` or `vX.Y.Z` that holds JSON files. It may sit
// directly under a root (`<root>\v0.7.1`, the benchmark-data layout) or one level deeper
// (`<root>\apps\dev`, `<root>\tasks\dev`). The benchmark comes from each file, not the folder.
export function findReleaseFolders(root, depth = 0) {
  const found = [];
  let entries;
  try {
    entries = readdirSync(root, { withFileTypes: true });
  } catch (e) {
    throw new Error(`Cannot read data folder ${root}: ${e.message}`);
  }
  if (isReleaseName(basename(root)) && entries.some((e) => e.isFile() && e.name.endsWith('.json'))) {
    found.push(root);
  }
  if (depth < 2) {
    for (const e of entries) if (e.isDirectory() && !e.name.startsWith('.')) found.push(...findReleaseFolders(join(root, e.name), depth + 1));
  }
  return found;
}

// Returns { releases: Map<release, trial[]>, warnings: string[] }.
export function loadData(roots) {
  const releases = new Map();
  const warnings = [];
  const seen = new Map();
  for (const root of roots) {
    if (!statSync(root, { throwIfNoEntry: false })?.isDirectory()) throw new Error(`Data folder not found: ${root}`);
    const folders = findReleaseFolders(root);
    if (!folders.length) warnings.push(`No release folders (dev or vX.Y.Z) found under ${root}`);
    for (const folder of folders) {
      const release = basename(folder);
      const list = releases.get(release) ?? [];
      releases.set(release, list);
      let index = null;
      let read = 0;
      for (const name of readdirSync(folder).filter((n) => n.endsWith('.json')).sort()) {
        const path = join(folder, name);
        let t;
        try {
          t = JSON.parse(readFileSync(path, 'utf8'));
        } catch (e) {
          warnings.push(`${path}: not valid JSON (${e.message}); skipped`);
          continue;
        }
        if (name === 'index.json') {
          index = t;
          continue;
        }
        const problems = checkTrial(t);
        if (problems.length) {
          warnings.push(`${path}: ${problems.join('; ')}; skipped`);
          continue;
        }
        if (t.release !== release) warnings.push(`${path}: release field is ${t.release} but the folder is ${release}; counted under ${release}`);
        const key = [release, t.benchmark, t.scenario, t.arm, t.model, t.trial].join('|');
        if (seen.has(key)) {
          warnings.push(`${path}: same trial as ${seen.get(key)}; skipped`);
          continue;
        }
        seen.set(key, path);
        list.push({ ...t, release, file: name });
        read++;
      }
      if (index && Number.isInteger(index.trialCount) && index.trialCount !== read) {
        warnings.push(`${join(folder, 'index.json')}: says ${index.trialCount} trials but the folder has ${read} readable trial files`);
      }
    }
  }
  for (const [r, list] of releases) if (!list.length) releases.delete(r);
  return { releases, warnings };
}
