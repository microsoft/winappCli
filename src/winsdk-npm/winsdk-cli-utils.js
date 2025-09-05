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
    path.join(__dirname, `../winsdk-CLI/Winsdk.Cli/bin/Debug/net9.0/${arch}/Winsdk.Cli.exe`),
    path.join(__dirname, `../winsdk-CLI/Winsdk.Cli/bin/Release/net9.0/${arch}/Winsdk.Cli.exe`),
    'winsdk.exe' // If installed globally
  ];
  
  return possiblePaths.find(p => fs.existsSync(p)) || possiblePaths[0];
}

/**
 * Helper function to call the native winsdk-cli
 */
async function callWinsdkCli(args, options = {}) {
  const { verbose = true, exitOnError = false } = options;
  const winsdkCliPath = getWinsdkCliPath();
  
  return new Promise((resolve, reject) => {
    const child = spawn(winsdkCliPath, args, {
      stdio: verbose ? 'inherit' : 'pipe',
      shell: false
    });
    
    child.on('close', (code) => {
      if (code === 0) {
        resolve();
      } else {
        if (exitOnError) {
          process.exit(code);
        } else {
          reject(new Error(`winsdk-cli exited with code ${code}`));
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
