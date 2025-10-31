const { execSync } = require("child_process");
const { getWinappCliPath } = require('./winapp-cli-utils');

/**
 * Execute a command with BuildTools bin path added to PATH environment
 * @param {string} command - The command to execute
 * @param {object} options - Options to pass to execSync (optional)
 * @returns {Buffer} - The output from execSync
 */
function execSyncWithBuildTools(command, options = {}) {
  // Parse the command to extract tool name and arguments
  const parts = command.trim().split(/\s+/);
  const toolName = parts[0];
  const args = parts.slice(1);
  
  // Build command for native CLI tool
  const cliPath = getWinappCliPath();
  const fullCommand = `"${cliPath}" tool -- ${toolName} ${args.join(' ')}`;
  
  // Execute synchronously using native CLI
  try {
    return execSync(fullCommand, options);
  } catch (error) {
    // Re-throw with original command context for better error messages
    const newError = new Error(`Command failed: ${command}`);
    newError.code = error.code;
    newError.signal = error.signal;
    newError.stderr = error.stderr;
    newError.stdout = error.stdout;
    throw newError;
  }
}

module.exports = {
  execSyncWithBuildTools
};
