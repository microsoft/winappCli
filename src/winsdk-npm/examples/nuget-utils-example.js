const path = require('path');
const { downloadAndExtractNuGetPackage, getNuGetPackageVersions } = require('../nuget-utils');

// Example usage of the NuGet utilities

async function examples() {
  try {
    // Example 1: Download latest version of a package
    console.log('Example 1: Download latest Microsoft.Windows.SDK.BuildTools');
    await downloadAndExtractNuGetPackage(
      'Microsoft.Windows.SDK.BuildTools',
      path.join(__dirname, 'example-output', 'buildtools-latest')
    );
    
    // Example 2: Download specific version
    console.log('\nExample 2: Download specific version of Newtonsoft.Json');
    await downloadAndExtractNuGetPackage(
      'Newtonsoft.Json',
      path.join(__dirname, 'example-output', 'newtonsoft-specific'),
      { version: '13.0.1' }
    );
    
    // Example 3: Download with custom options
    console.log('\nExample 3: Download with custom options');
    await downloadAndExtractNuGetPackage(
      'Microsoft.Extensions.Logging',
      path.join(__dirname, 'example-output', 'logging'),
      {
        keepDownload: true,  // Keep the .nupkg file
        downloadPath: path.join(__dirname, 'downloads'),  // Custom download location
        verbose: true
      }
    );
    
    // Example 4: Just get available versions without downloading
    console.log('\nExample 4: Get available versions');
    const versions = await getNuGetPackageVersions('Microsoft.Extensions.DependencyInjection');
    console.log('Available versions:', versions.slice(-5)); // Show last 5 versions
    
  } catch (error) {
    console.error('Example failed:', error.message);
  }
}

// Uncomment the line below to run examples
// examples();
