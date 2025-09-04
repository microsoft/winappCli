const { execSyncWithBuildTools, getBuildToolPath } = require("../buildtools-utils");

/**
 * Example script showing how to use various BuildTools utilities
 */

function compileResourceFile(rcFilePath, outputPath) {
  // Using rc.exe to compile resource files
  const command = `rc.exe /fo "${outputPath}" "${rcFilePath}"`;
  
  try {
    console.log(`Compiling resource file: ${rcFilePath}`);
    execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log(`Successfully compiled to: ${outputPath}`);
  } catch (error) {
    console.error(`Failed to compile resource file:`, error.message);
    throw error;
  }
}

function generateUuid() {
  // Using uuidgen.exe to generate UUIDs
  try {
    const result = execSyncWithBuildTools('uuidgen.exe', { encoding: 'utf8' });
    return result.trim();
  } catch (error) {
    console.error(`Failed to generate UUID:`, error.message);
    throw error;
  }
}

function createCertificate(certName, outputPath) {
  // Using MakeCert.exe to create test certificates
  const command = `MakeCert.exe -sv "${outputPath}.pvk" -n "CN=${certName}" "${outputPath}.cer"`;
  
  try {
    console.log(`Creating certificate: ${certName}`);
    execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log(`Successfully created certificate files: ${outputPath}.cer and ${outputPath}.pvk`);
  } catch (error) {
    console.error(`Failed to create certificate:`, error.message);
    throw error;
  }
}

function listAvailableTools() {
  // List all available tools in the BuildTools bin directory
  const { findBuildToolsBinPath } = require("../buildtools-utils");
  const fs = require("fs");
  const path = require("path");
  
  try {
    const binPath = findBuildToolsBinPath();
    const tools = fs.readdirSync(binPath)
      .filter(file => file.endsWith('.exe'))
      .sort();
    
    console.log("Available BuildTools:");
    tools.forEach(tool => {
      console.log(`  - ${tool}`);
    });
    
    return tools;
  } catch (error) {
    console.error(`Failed to list tools:`, error.message);
    throw error;
  }
}

// Example usage:
// console.log("Generated UUID:", generateUuid());
// listAvailableTools();
// compileResourceFile("./resources.rc", "./resources.res");

module.exports = {
  compileResourceFile,
  generateUuid,
  createCertificate,
  listAvailableTools
};
