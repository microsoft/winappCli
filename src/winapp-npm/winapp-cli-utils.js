const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawn } = require('child_process');

const WINAPP_CLI_CALLER_VALUE = 'nodejs-package';

/**
 * Helper function to get the path to the winapp-cli executable
 */
function getWinappCliPath() {
  // Determine architecture
  const arch = os.arch() === 'arm64' ? 'win-arm64' : 'win-x64';
  
  // Look for the winapp-cli executable in various locations
  const possiblePaths = [
    // Distribution build (single-file executable)
    path.join(__dirname, `bin/${arch}/winapp.exe`),
    // Development builds (when building from source)
    path.join(__dirname, `../winapp-CLI/WinApp.Cli/bin/Debug/net10.0-windows/${arch}/winapp.exe`),
    path.join(__dirname, `../winapp-CLI/WinApp.Cli/bin/Release/net10.0-windows/${arch}/winapp.exe`),
    // Global installation
    'winapp.exe'
  ];
  
  return possiblePaths.find(p => fs.existsSync(p)) || possiblePaths[0];
}

/**
 * Helper function to call the native winapp-cli
 * Always captures output and returns it along with the exit code
 */
async function callWinappCli(args, options = {}) {
  const { exitOnError = false } = options;
  const winappCliPath = getWinappCliPath();
  
  return new Promise((resolve, reject) => {
    const child = spawn(winappCliPath, args, {
      stdio: 'inherit',
      cwd: process.cwd(),
      shell: false,
      env: {
        ...process.env,
        WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE
      }
    });
    
    child.on('close', (code) => {
      if (code === 0) {
        resolve({ exitCode: code });
      } else {
        if (exitOnError) {
          console.error(`winapp-cli exited with code ${code}`);
          process.exit(code);
        } else {
          reject(new Error(`winapp-cli exited with code ${code}`));
        }
      }
    });

    child.on('error', (error) => {
      if (exitOnError) {
        console.error(`Failed to execute winapp-cli: ${error.message}`);
        console.error(`Tried to run: ${winappCliPath}`);
        process.exit(1);
      } else {
        reject(new Error(`Failed to execute winapp-cli: ${error.message}`));
      }
    });
  });
}

module.exports = {
  getWinappCliPath: getWinappCliPath,
  callWinappCli: callWinappCli,
  WINAPP_CLI_CALLER_VALUE: WINAPP_CLI_CALLER_VALUE
};
