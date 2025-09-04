#!/usr/bin/env node

const { execSyncWithBuildTools } = require("./buildtools-utils");

/**
 * Generic script runner for BuildTools commands
 * Usage: node run-buildtool.js <command> [args...]
 * Example: node run-buildtool.js makeappx.exe pack /o /d "./msix" /nv /p "./dist/SparseExample.msix"
 */

async function main() {
  const args = process.argv.slice(2);
  
  if (args.length === 0) {
    console.error('Usage: node run-buildtool.js <command> [args...]');
    console.error('Example: node run-buildtool.js makeappx.exe pack /o /d "./msix" /nv /p "./dist/SparseExample.msix"');
    process.exit(1);
  }
  
  // Join all arguments into a single command
  const command = args.join(' ');
  
  try {
    console.log(`Executing: ${command}`);
    await execSyncWithBuildTools(command, { stdio: 'inherit' });
    console.log('Command completed successfully!');
  } catch (error) {
    console.error(`Command failed:`, error.message);
    process.exit(1);
  }
}

// Run if called directly
if (require.main === module) {
  main();
}

module.exports = { main };
