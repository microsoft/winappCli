const { execSyncWithBuildTools, getBuildToolPath } = require("../buildtools-utils");
const path = require("path");

/**
 * Example script showing how to use BuildTools utilities for code signing
 */

async function signExecutable(exePath, certPath, certPassword) {
  // Method 1: Using execSyncWithBuildTools (signtool.exe is now in PATH)
  const command = `signtool.exe sign /f "${certPath}" /p "${certPassword}" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "${exePath}"`;
  
  try {
    await execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log(`Successfully signed: ${exePath}`);
  } catch (error) {
    console.error(`Failed to sign ${exePath}:`, error.message);
    throw error;
  }
}

async function signExecutableAlternative(exePath, certPath, certPassword) {
  // Method 2: Using getBuildToolPath to get the full path to signtool.exe
  const signtoolPath = await getBuildToolPath("signtool.exe");
  const { execSync } = require("child_process");
  
  const command = `"${signtoolPath}" sign /f "${certPath}" /p "${certPassword}" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "${exePath}"`;
  
  try {
    execSync(command, { stdio: 'inherit' });
    console.log(`Successfully signed: ${exePath}`);
  } catch (error) {
    console.error(`Failed to sign ${exePath}:`, error.message);
    throw error;
  }
}

// Example usage:
// signExecutable("./dist/myapp.exe", "./certificate.pfx", "password123");

module.exports = {
  signExecutable,
  signExecutableAlternative
};
