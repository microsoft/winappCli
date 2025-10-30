const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawn } = require('child_process');

/**
 * Helper function to get the path to the winsdk-cli executable
 */
function getWinsdkCliPath() {
  // Determine architecture
  const arch = os.arch() === 'arm64' ? 'win-arm64' : 'win-x64';
  
  // Look for the winsdk-cli executable in various locations
  const possiblePaths = [
    // Distribution build (single-file executable)
    path.join(__dirname, `bin/${arch}/Winsdk.Cli.exe`),
    // Development builds (when building from source)
    path.join(__dirname, `../winsdk-CLI/Winsdk.Cli/bin/Debug/net9.0-windows/${arch}/Winsdk.Cli.exe`),
    path.join(__dirname, `../winsdk-CLI/Winsdk.Cli/bin/Release/net9.0-windows/${arch}/Winsdk.Cli.exe`),
    // Global installation
    'winsdk.exe'
  ];
  
  return possiblePaths.find(p => fs.existsSync(p)) || possiblePaths[0];
}

/**
 * Helper function to call the native winsdk-cli
 * Always captures output and returns it along with the exit code
 */
async function callWinsdkCli(args, options = {}) {
  const { verbose = false, exitOnError = false } = options;
  const winsdkCliPath = getWinsdkCliPath();
  
  return new Promise((resolve, reject) => {
    let stderr = '';
    
    const child = spawn(winsdkCliPath, args, {
      stdio: verbose ? 'inherit' : 'pipe',
      cwd: process.cwd(),
      shell: false
    });
    
    // Only capture stderr when not using inherit mode (needed for error messages)
    if (!verbose) {
      child.stderr.on('data', (data) => {
        stderr += data.toString();
      });
    }
    
    child.on('close', (code) => {
      if (code === 0) {
        resolve({ exitCode: code });
      } else {
        if (exitOnError) {
          // Print stderr only if not verbose, as it would have been printed already
          if (!verbose) {
            console.error(`winsdk-cli failed: ${stderr}`);
          }
          process.exit(code);
        } else {
          reject(new Error(`winsdk-cli exited with code ${code}: ${stderr}`));
        }
      }
    });
    
    child.on('error', (error) => {
      if (exitOnError) {
        console.error(`Failed to execute winsdk-cli: ${error.message}`);
        console.error(`Tried to run: ${winsdkCliPath}`);
        process.exit(1);
      } else {
        reject(new Error(`Failed to execute winsdk-cli: ${error.message}`));
      }
    });
  });
}

module.exports = {
  getWinsdkCliPath,
  callWinsdkCli
};
