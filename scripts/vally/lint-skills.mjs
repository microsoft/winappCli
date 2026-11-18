// Lints every shipped plugin with vally, the check github/awesome-copilot runs before it accepts
// a plugin listing (runVallyLintGate in its eng/external-plugin-quality-gates.mjs). Keep the
// @microsoft/vally version in package.json equal to the one awesome-copilot pins.
//
// Plugin roots are found as in scripts/validate-plugin-package.ps1: any folder under plugins/
// that holds plugin.json and a skills/ folder. Like the listing check, each root is linted through
// the skills paths its plugin.json declares, or as a whole when it declares none.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { runLint, LintConsoleReporter } from "@microsoft/vally";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const pluginsDir = path.join(repoRoot, "plugins");
const isDirectory = (p) => fs.statSync(p, { throwIfNoEntry: false })?.isDirectory() ?? false;

const pluginRoots = fs.readdirSync(pluginsDir, { recursive: true })
  .filter((entry) => path.basename(entry) === "plugin.json")
  .map((entry) => path.join(pluginsDir, path.dirname(entry)))
  .filter((dir) => isDirectory(path.join(dir, "skills")))
  .sort();

if (pluginRoots.length === 0) {
  console.error(`No plugin roots found under ${pluginsDir}.`);
  process.exit(1);
}

let passed = true;
for (const root of pluginRoots) {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, "plugin.json"), "utf8"));
  const skillPaths = [].concat(manifest.skills ?? [])
    .map((p) => path.resolve(root, p))
    .filter(isDirectory);
  for (const target of skillPaths.length > 0 ? skillPaths : [root]) {
    console.log(`\n### ${path.relative(repoRoot, target).replaceAll(path.sep, "/")}`);
    const result = await runLint({ rootPath: target });
    await new LintConsoleReporter({ verbose: true, stream: process.stdout }).report(result);
    passed &&= result.passed;
  }
}
process.exitCode = passed ? 0 : 1;
