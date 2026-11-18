import { existsSync } from "fs";
import { fileURLToPath } from "url";

/**
 * Path to the platform package's native Copilot CLI binary, or undefined.
 *
 * Bypasses `@github/copilot/npm-loader.js`: its `spawnSync(..., {stdio:'inherit'})`
 * can't inherit the SDK's piped fds on Windows, so copilot.exe EOFs in ~15 ms
 * and the SDK reports "CLI server exited unexpectedly with code 0". Pass the
 * result as `COPILOT_CLI_PATH` in the CopilotClient env so the SDK spawns the
 * binary directly. Bare-resolve the platform package — its `exports` field
 * maps "." to "./copilot.exe", so a subpath would hit ERR_PACKAGE_PATH_NOT_EXPORTED.
 */
export function resolveCopilotCliPath(): string | undefined {
  try {
    const candidate = fileURLToPath(import.meta.resolve(`@github/copilot-${process.platform}-${process.arch}`));
    return existsSync(candidate) ? candidate : undefined;
  } catch {
    return undefined;
  }
}
