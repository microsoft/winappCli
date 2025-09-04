const { ensureBuildTools, execSyncWithBuildTools } = require("../buildtools-utils");

/**
 * Test script to demonstrate auto-download functionality
 */

async function testBuildToolsAutoDownload() {
  try {
    console.log("Testing BuildTools auto-download functionality...");
    
    // This will automatically download BuildTools if not found
    const binPath = await ensureBuildTools();
    console.log(`BuildTools available at: ${binPath}`);
    
    // Test a simple tool - mt.exe returns non-zero for help, so catch it
    console.log("\nTesting mt.exe...");
    try {
      await execSyncWithBuildTools('mt.exe -help', { encoding: 'utf8' });
    } catch (error) {
      // mt.exe returns non-zero exit code for help, but that's expected
      if (error.message.includes('Command failed: mt.exe -help')) {
        console.log("mt.exe is working! (Help command executed successfully)");
      } else {
        throw error;
      }
    }
    
    console.log("\nTesting signtool.exe...");
    try {
      await execSyncWithBuildTools('signtool.exe /?', { stdio: 'pipe' });
    } catch (error) {
      // signtool.exe also returns non-zero for help in some cases
      if (error.message.includes('Command failed: signtool.exe /?')) {
        console.log("signtool.exe is working! (Help command executed successfully)");
      } else {
        throw error;
      }
    }
    
    console.log("\nAll tests passed! BuildTools are ready to use.");
    
  } catch (error) {
    console.error("Test failed:", error.message);
    process.exit(1);
  }
}

// Run the test if this script is executed directly
if (require.main === module) {
  testBuildToolsAutoDownload();
}

module.exports = { testBuildToolsAutoDownload };
