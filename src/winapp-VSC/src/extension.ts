import * as vscode from 'vscode';
import { exec } from 'child_process';
import { promisify } from 'util';

const execAsync = promisify(exec);

const WINAPP_DEBUG_TYPE = 'winapp';

class WinAppDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
	async resolveDebugConfiguration(
		folder: vscode.WorkspaceFolder | undefined,
		config: vscode.DebugConfiguration,
		_token?: vscode.CancellationToken
	): Promise<vscode.DebugConfiguration | undefined> {
		// If no configuration, create a default one
		if (!config.type && !config.request && !config.name) {
			config.type = WINAPP_DEBUG_TYPE;
			config.name = 'WinApp: Launch and Attach';
			config.request = 'launch';
		}

		return config;
	}

	       async resolveDebugConfigurationWithSubstitutedVariables(
		       folder: vscode.WorkspaceFolder | undefined,
		       config: vscode.DebugConfiguration,
		       _token?: vscode.CancellationToken
	       ): Promise<vscode.DebugConfiguration | undefined> {
		       if (!folder) {
			       vscode.window.showErrorMessage('No workspace folder open');
			       return undefined;
		       }

		       try {
			       // Build the command with mapped arguments
			       const cmdParts: string[] = ['D:\\WinAppCli\\src\\winapp-CLI\\WinApp.Cli\\bin\\Debug\\net10.0-windows\\win-arm64\\winapp.exe', 'run'];

			       if (config.manifest) {
				       cmdParts.push('--manifest', `"${config.manifest}"`);
			       }

				   // Determine the debugger type based on config or default to coreclr
				   const debuggerType = config.debuggerType || 'coreclr';

				   if (debuggerType === 'node') {
						if (!config.args) {
							config.args = '';
						}
						config.args = '--inspect' + (config.port ? `=${config.port}` : '') + ' ' + config.args;
				   }

			       if (config.args) {
				       cmdParts.push('--args', `"${config.args}"`);
			       }

			       const command = cmdParts.join(' ');

			       // Run "winapp run" which returns the process ID
			       const processId = await vscode.window.withProgress({
				       location: vscode.ProgressLocation.Notification,
				       title: 'Launching package...',
				       cancellable: false
			       }, async (progress) => {
				       progress.report({ message: 'Running winapp run...' });

					   let cwd = folder.uri.fsPath;
					   if (config.workingDirectory) {
						   cwd = config.workingDirectory;
					   }

				       const { stdout, stderr } = await execAsync(command, { cwd });

				       if (stderr) {
					       console.warn('winapp run stderr:', stderr);
				       }

				       const pid = parseProcessId(stdout);
				       if (!pid) {
					       throw new Error(`Could not parse process ID from winapp run output: ${stdout}`);
				       }

				       return pid;
			       });

				   // define debugConfiguration using vscode.DebugConfiguration type
				   var debugConfiguration = {
					   type: debuggerType,
					   name: config.name || 'Attach to WinApp Package',
					   request: 'attach'
					} as vscode.DebugConfiguration;

					// if debuggerType is 'node', use port from config or default to 9229
					if (debuggerType === 'node') {
						debugConfiguration.port = config.port || 9229;
					}else{
						// for other debugger types, set processId in config
						debugConfiguration.processId = processId;
					}
			       // Start the child debug session and return undefined so VS Code doesn't try to start a debug adapter for 'winapp'
			       await vscode.debug.startDebugging(folder, debugConfiguration);
			       return undefined;
		       } catch (error) {
			       const message = error instanceof Error ? error.message : String(error);
			       vscode.window.showErrorMessage(`Failed to launch and attach: ${message}`);
			       return undefined;
		       }
	       }
}

export function activate(context: vscode.ExtensionContext) {
	const provider = new WinAppDebugConfigurationProvider();

	context.subscriptions.push(
		vscode.debug.registerDebugConfigurationProvider(WINAPP_DEBUG_TYPE, provider)
	);
}

/**
 * Parse the process ID from the winapp run output.
 * Expects the output to contain the process ID (e.g., just the number or in a known format).
 */
function parseProcessId(output: string): number | undefined {
	const trimmed = output.trim();

	// Try to parse the output directly as a number
	const directParse = parseInt(trimmed, 10);
	if (!isNaN(directParse) && directParse > 0) {
		return directParse;
	}

	// Try to find a process ID in common formats like "PID: 1234" or "Process ID: 1234"
	const patterns = [
		/(?:pid|process\s*id)\s*[=:]\s*(\d+)/i,
		/^(\d+)$/m,
		/started\s+.*?(?:pid|process)\s*[=:]\s*(\d+)/i,
	];

	for (const pattern of patterns) {
		const match = trimmed.match(pattern);
		if (match) {
			const pid = parseInt(match[1], 10);
			if (!isNaN(pid) && pid > 0) {
				return pid;
			}
		}
	}

	return undefined;
}

export function deactivate() {}
