import { execSync } from 'child_process';
import { getWinappCliPath } from './winapp-cli-utils';

/**
 * Get the path to a .winapp directory (local or global)
 * @param isGlobal - Whether to get the global path (true) or local path (false)
 * @returns The full path to the .winapp directory
 * @throws Error if the .winapp directory is not found
 */
function getWinappPath(isGlobal: boolean = false): string {
  try {
    const winappCliPath = getWinappCliPath();
    const globalFlag = isGlobal ? ' --global' : '';
    const result = execSync(`"${winappCliPath}" get-winapp-path${globalFlag}`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    return result.trim();
  } catch {
    const pathType = isGlobal ? 'Global' : 'Local';
    const setupCommand = isGlobal ? 'winapp setup' : 'winapp init';
    throw new Error(`${pathType} .winapp directory not found. Make sure to run '${setupCommand}' first.`);
  }
}

/**
 * Get the path to the global .winapp directory
 * @returns The full path to the global .winapp directory
 * @throws Error if the global .winapp directory is not found
 */
export function getGlobalWinappPath(): string {
  return getWinappPath(true);
}

/**
 * Get the path to the local .winapp directory
 * @returns The full path to the local .winapp directory
 * @throws Error if the local .winapp directory is not found
 */
export function getLocalWinappPath(): string {
  return getWinappPath(false);
}
