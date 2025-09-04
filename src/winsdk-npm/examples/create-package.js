const { execSyncWithBuildTools } = require("../buildtools-utils");
const path = require("path");

/**
 * Example script showing how to use BuildTools utilities for creating app packages
 */

function createAppxPackage(sourceDir, outputPath, manifestPath) {
  // Using makeappx.exe to create an APPX package
  const command = `makeappx.exe pack /d "${sourceDir}" /p "${outputPath}" /m "${manifestPath}"`;
  
  try {
    console.log(`Creating APPX package: ${outputPath}`);
    execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log(`Successfully created APPX package: ${outputPath}`);
  } catch (error) {
    console.error(`Failed to create APPX package:`, error.message);
    throw error;
  }
}

function createMsixPackage(sourceDir, outputPath) {
  // Using makeappx.exe to create an MSIX package
  const command = `makeappx.exe pack /d "${sourceDir}" /p "${outputPath}"`;
  
  try {
    console.log(`Creating MSIX package: ${outputPath}`);
    execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log(`Successfully created MSIX package: ${outputPath}`);
  } catch (error) {
    console.error(`Failed to create MSIX package:`, error.message);
    throw error;
  }
}

function createPriFile(sourceDir, outputPath, configPath) {
  // Using makepri.exe to create PRI files for resources
  const command = `makepri.exe createconfig /cf "${configPath}" /dq en-US /pv 10.0.0`;
  
  try {
    console.log(`Creating PRI configuration: ${configPath}`);
    execSyncWithBuildTools(command, { stdio: 'inherit' });
    
    const priCommand = `makepri.exe new /pr "${sourceDir}" /cf "${configPath}" /of "${outputPath}"`;
    console.log(`Creating PRI file: ${outputPath}`);
    execSyncWithBuildTools(priCommand, { stdio: 'inherit' });
    
    console.log(`Successfully created PRI file: ${outputPath}`);
  } catch (error) {
    console.error(`Failed to create PRI file:`, error.message);
    throw error;
  }
}

// Example usage:
// createAppxPackage("./app-package", "./dist/myapp.appx", "./Package.appxmanifest");
// createMsixPackage("./app-package", "./dist/myapp.msix");

module.exports = {
  createAppxPackage,
  createMsixPackage,
  createPriFile
};
