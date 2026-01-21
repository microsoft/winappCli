import { execSync, ExecSyncOptions as NodeExecSyncOptions } from 'child_process';
import { getWinappCliPath } from './winapp-cli-utils';

/**
 * Execute a command with BuildTools bin path added to PATH environment
 * @param command - The command to execute
 * @param options - Options to pass to execSync (optional)
 * @returns The output from execSync
 */
export function execSyncWithBuildTools(command: string, options: NodeExecSyncOptions = {}): Buffer | string {
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
    const execError = error as { code?: number; signal?: string; stderr?: Buffer; stdout?: Buffer };
    const newError = new Error(`Command failed: ${command}`) as Error & {
      code?: number;
      signal?: string;
      stderr?: Buffer;
      stdout?: Buffer;
    };
    newError.code = execError.code;
    newError.signal = execError.signal;
    newError.stderr = execError.stderr;
    newError.stdout = execError.stdout;
    throw newError;
  }
}
