/**
 * Get the path to a .winapp directory (local or global)
 * @param {boolean} isGlobal - Whether to get the global path (true) or local path (false)
 * @returns {string} The full path to the .winapp directory
 * @throws {Error} If the .winapp directory is not found
 */
function getWinappPath(isGlobal = false) {
  const { execSync } = require('child_process');
  const { getWinappCliPath: getWinappCliPath } = require('./winapp-cli-utils');
  
  try {
    const winappCliPath = getWinappCliPath();
    const globalFlag = isGlobal ? ' --global' : '';
    const result = execSync(`"${winappCliPath}" get-winapp-path${globalFlag}`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe']
    });
    return result.trim();
  } catch (error) {
    const pathType = isGlobal ? 'Global' : 'Local';
    const setupCommand = isGlobal ? 'winapp setup' : 'winapp init';
    throw new Error(`${pathType} .winapp directory not found. Make sure to run '${setupCommand}' first.`);
  }
}

/**
 * Get the path to the global .winappglobal directory
 * @returns {string} The full path to the global .winappglobal directory
 * @throws {Error} If the global .winappglobal directory is not found
 */
function getGlobalWinappPath() {
  return getWinappPath(true);
}

/**
 * Get the path to the local .winapp directory
 * @returns {string} The full path to the local .winapp directory
 * @throws {Error} If the local .winapp directory is not found
 */
function getLocalWinappPath() {
  return getWinappPath(false);
}

module.exports = {
  getGlobalWinappPath,
  getLocalWinappPath
};