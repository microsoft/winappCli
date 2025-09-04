const fs = require('fs').promises;
const fsSync = require('fs');
const path = require('path');
const { execSyncWithBuildTools } = require("./buildtools-utils");

/**
 * Adds MSIX identity information from an appxmanifest.xml file to an executable's embedded manifest
 * @param {string} exePath - Path to the executable file
 * @param {string} appxManifestPath - Path to the appxmanifest.xml file containing MSIX identity data
 * @param {Object} options - Optional configuration
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.tempDir - Directory for temporary files (default: same as exe directory)
 */
async function addMsixIdentityToExe(exePath, appxManifestPath, options = {}) {
  const { verbose = true, tempDir } = options;
  
  // Validate inputs
  if (!fsSync.existsSync(exePath)) {
    throw new Error(`Executable not found at: ${exePath}`);
  }
  
  if (!fsSync.existsSync(appxManifestPath)) {
    throw new Error(`AppX manifest not found at: ${appxManifestPath}`);
  }

  const workingDir = tempDir || path.dirname(exePath);
  const tempManifestPath = path.join(workingDir, 'temp_extracted.manifest');
  const combinedManifestPath = path.join(workingDir, 'combined.manifest');

  if (verbose) {
    console.log(`Processing executable: ${exePath}`);
    console.log(`Using AppX manifest: ${appxManifestPath}`);
  }

  try {
    // Read and extract MSIX identity from appxmanifest.xml
    const appxManifestContent = await fs.readFile(appxManifestPath, 'utf8');
    
    // Extract Package Identity information
    const identityMatch = appxManifestContent.match(/<Identity[^>]*>/i);
    if (!identityMatch) {
      throw new Error('No Identity element found in AppX manifest');
    }
    
    const identityElement = identityMatch[0];
    
    // Extract attributes from Identity element
    const nameMatch = identityElement.match(/Name\s*=\s*["']([^"']*)["']/i);
    const publisherMatch = identityElement.match(/Publisher\s*=\s*["']([^"']*)["']/i);
    
    if (!nameMatch || !publisherMatch) {
      throw new Error('AppX manifest Identity element missing required Name or Publisher attributes');
    }
    
    const packageName = nameMatch[1];
    const publisher = publisherMatch[1];
    
    // Extract Application ID from Applications/Application element
    const applicationMatch = appxManifestContent.match(/<Application[^>]*Id\s*=\s*["']([^"']*)["'][^>]*>/i);
    if (!applicationMatch) {
      throw new Error('No Application element with Id attribute found in AppX manifest');
    }
    
    const applicationId = applicationMatch[1];
    
    // Create the MSIX element for the win32 manifest
    const msixElement = `<msix xmlns="urn:schemas-microsoft-com:msix.v1"
            publisher="${publisher}"
            packageName="${packageName}"
            applicationId="${applicationId}"
        />`;

    if (verbose) {
      console.log('Extracting current manifest from executable...');
    }
    
    // Extract current manifest from the executable
    let hasExistingManifest = false;
    try {
      await execSyncWithBuildTools(`mt.exe -inputresource:"${exePath}";#1 -out:"${tempManifestPath}"`, { stdio: verbose ? 'inherit' : 'pipe' });
      hasExistingManifest = fsSync.existsSync(tempManifestPath);
    } catch (error) {
      if (verbose) {
        console.log('No existing manifest found in executable, creating new one');
      }
    }

    let finalManifest;

    if (hasExistingManifest) {
      if (verbose) {
        console.log('Combining with existing manifest...');
      }
      
      // Read existing manifest
      const existingManifest = await fs.readFile(tempManifestPath, 'utf8');
      
      // Find the closing </assembly> tag in existing manifest
      const existingManifestParts = existingManifest.split('</assembly>');
      
      if (existingManifestParts.length >= 2) {
        // Remove any existing msix section
        let cleanedExistingContent = existingManifestParts[0];
        cleanedExistingContent = cleanedExistingContent.replace(/<msix[\s\S]*?<\/msix>/gi, '');
        cleanedExistingContent = cleanedExistingContent.replace(/<msix[\s\S]*?\/>/gi, '');
        
        // Combine: existing content + msix element + closing tag + rest
        finalManifest = cleanedExistingContent + '\n  ' + msixElement + '\n</assembly>' + existingManifestParts.slice(1).join('</assembly>');
      } else {
        throw new Error('Invalid existing manifest structure');
      }
      
      // Clean up temporary file
      await fs.unlink(tempManifestPath);
    } else {
      // Create a new basic manifest with MSIX identity
      finalManifest = `<?xml version="1.0" encoding="UTF-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  ${msixElement}
  <assemblyIdentity version="1.0.0.0" name="${packageName}" type="win32"/>
</assembly>`;
    }

    // Write the combined manifest
    await fs.writeFile(combinedManifestPath, finalManifest, 'utf8');

    var command = `mt.exe -manifest "${combinedManifestPath}" -outputresource:"${exePath}";#1`;
    if (verbose) {
      console.log(`Final manifest content: ${finalManifest}`);
      console.log('Re-embedding manifest into executable...');
      console.log(`Command: ${command}`);
    }
    
    // Re-embed the combined manifest into the executable
    await execSyncWithBuildTools(command, { stdio: verbose ? 'inherit' : 'pipe' });

    if (verbose) {
      console.log('MSIX identity successfully embedded into executable');
    }
    
    // Clean up combined manifest file
    await fs.unlink(combinedManifestPath);
    
    return {
      success: true,
      packageName,
      publisher,
      applicationId
    };
    
  } catch (error) {
    // Clean up any temporary files
    if (fsSync.existsSync(tempManifestPath)) {
      await fs.unlink(tempManifestPath);
    }
    if (fsSync.existsSync(combinedManifestPath)) {
      await fs.unlink(combinedManifestPath);
    }
    
    throw new Error(`Failed to add MSIX identity to executable: ${error.message}`);
  }
}

/**
 * Creates a PRI configuration file for the given package directory
 * @param {string} packageDir - Path to the package directory
 * @param {Object} options - Optional configuration
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.language - Default language qualifier (default: 'en-US')
 * @param {string} options.platformVersion - Platform version (default: '10.0.0')
 */
async function createPriConfig(packageDir, options = {}) {
  const { verbose = true, language = 'en-US', platformVersion = '10.0.0' } = options;

  // Remove trailing backslashes from packageDir
  packageDir = packageDir.replace(/[\\\/]+$/, '');

  if (!fsSync.existsSync(packageDir)) {
    throw new Error(`Package directory not found: ${packageDir}`);
  }

  const configPath = path.join(packageDir, 'priconfig.xml');
  const command = `makepri createconfig /cf "${configPath}" /dq ${language} /pv ${platformVersion} /o`;
  
  if (verbose) {
    console.log('Creating PRI configuration file...');
  }

  try {
    await execSyncWithBuildTools(command, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log(`PRI configuration created: ${configPath}`);
    }
    
    return configPath;
  } catch (error) {
    throw new Error(`Failed to create PRI configuration: ${error.message}`);
  }
}

/**
 * Generates a PRI file from the configuration
 * @param {string} packageDir - Path to the package directory
 * @param {Object} options - Optional configuration
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.configPath - Path to PRI config file (default: packageDir/priconfig.xml)
 * @param {string} options.outputPath - Output path for PRI file (default: packageDir/resources.pri)
 */
async function generatePriFile(packageDir, options = {}) {
  const { verbose = true, configPath, outputPath } = options;
  
  // Remove trailing backslashes from packageDir
  packageDir = packageDir.replace(/[\\\/]+$/, '');
  
  if (!fsSync.existsSync(packageDir)) {
    throw new Error(`Package directory not found: ${packageDir}`);
  }

  const priConfigPath = configPath || path.join(packageDir, 'priconfig.xml');
  const priOutputPath = outputPath || path.join(packageDir, 'resources.pri');
  
  if (!fsSync.existsSync(priConfigPath)) {
    throw new Error(`PRI configuration file not found: ${priConfigPath}`);
  }

  const command = `makepri new /pr "${packageDir}" /cf "${priConfigPath}" /of "${priOutputPath}" /o`;
  
  if (verbose) {
    console.log('Generating PRI file...');
  }

  try {
    await execSyncWithBuildTools(command, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log(`PRI file generated: ${priOutputPath}`);
    }
    
    return priOutputPath;
  } catch (error) {
    throw new Error(`Failed to generate PRI file: ${error.message}`);
  }
}

/**
 * Generates a development certificate for MSIX package signing
 * @param {string} outputPath - Path where to save the certificate (.pfx)
 * @param {Object} options - Certificate options
 * @param {string} options.publisher - Publisher name (CN) for the certificate
 * @param {string} options.password - Password for the certificate (default: 'password')
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {number} options.validDays - Certificate validity in days (default: 365)
 */
async function generateDevCertificate(outputPath, options = {}) {
  const { 
    publisher, 
    password = 'password', 
    verbose = true, 
    validDays = 365 
  } = options;
  
  if (!publisher) {
    throw new Error('Publisher name is required for certificate generation');
  }

  // Ensure output directory exists
  const outputDir = path.dirname(outputPath);
  if (!fsSync.existsSync(outputDir)) {
    await fs.mkdir(outputDir, { recursive: true });
  }

  // Clean up the publisher name to ensure proper CN format
  // Remove any existing CN= prefix and clean up quotes
  let cleanPublisher = publisher.replace(/^CN=/, '').replace(/['"]/g, '');
  
  // Ensure we have a proper CN format
  const subjectName = `CN=${cleanPublisher}`;

  const command = `powershell -Command "New-SelfSignedCertificate -Type Custom -Subject '${subjectName}' -KeyUsage DigitalSignature -FriendlyName 'MSIX Dev Certificate' -CertStoreLocation 'Cert:\\CurrentUser\\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddDays(${validDays}) | Export-PfxCertificate -FilePath '${outputPath}' -Password (ConvertTo-SecureString -String '${password}' -Force -AsPlainText)"`;
  
  if (verbose) {
    console.log(`Generating development certificate for publisher: ${cleanPublisher}`);
    console.log(`Certificate subject: ${subjectName}`);
  }

  try {
    await execSyncWithBuildTools(command, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log(`Certificate generated: ${outputPath}`);
    }
    
    return {
      certificatePath: outputPath,
      password: password,
      publisher: cleanPublisher,
      subjectName: subjectName
    };
  } catch (error) {
    throw new Error(`Failed to generate development certificate: ${error.message}`);
  }
}

/**
 * Installs a development certificate to the local machine's trusted store
 * @param {string} certificatePath - Path to the .pfx certificate file
 * @param {Object} options - Installation options
 * @param {string} options.password - Certificate password (default: 'password')
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {boolean} options.force - Force install even if already present (default: false)
 */
async function installDevCertificate(certificatePath, options = {}) {
  const { password = 'password', verbose = true, force = false } = options;
  
  if (!fsSync.existsSync(certificatePath)) {
    throw new Error(`Certificate file not found: ${certificatePath}`);
  }

  if (verbose) {
    console.log(`Installing development certificate: ${certificatePath}`);
  }

  try {
    // Check if certificate is already installed (unless force is true)
    if (!force) {
      const checkCommand = `powershell -Command "Get-ChildItem -Path 'Cert:\\LocalMachine\\TrustedPeople' | Where-Object { $_.Subject -like '*${path.basename(certificatePath, '.pfx')}*' }"`;
      
      try {
        const result = await execSyncWithBuildTools(checkCommand, { stdio: 'pipe' });
        if (result.toString().trim()) {
          if (verbose) {
            console.log('Certificate appears to already be installed');
          }
          return { alreadyInstalled: true };
        }
      } catch (error) {
        // Continue with installation if check fails
      }
    }

    // Install to TrustedPeople store (required for MSIX sideloading)
    // This requires admin privileges, so we'll use Start-Process with RunAs to elevate
    // Convert to absolute path since elevated PowerShell starts in different directory
    const absoluteCertPath = path.resolve(certificatePath);
    const installScript = `Import-PfxCertificate -FilePath '${absoluteCertPath}' -CertStoreLocation 'Cert:\\LocalMachine\\TrustedPeople' -Password (ConvertTo-SecureString -String '${password}' -Force -AsPlainText); Write-Host 'Certificate installation completed. Press any key to continue...'; Read-Host`;
    const installCommand = `powershell -Command "Start-Process PowerShell -ArgumentList '-NoExit', '-Command', \\"${installScript}\\" -Verb RunAs -Wait"`;
    
    if (verbose) {
      console.log('Installing certificate with elevated permissions (UAC prompt may appear)...');
      console.log(`Using absolute path: ${absoluteCertPath}`);
      console.log('Note: PowerShell window will stay open to show any errors. Press any key in that window to close it.');
    }
    
    await execSyncWithBuildTools(installCommand, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log('Certificate installed successfully to TrustedPeople store');
    }
    
    return { installed: true };
  } catch (error) {
    throw new Error(`Failed to install development certificate: ${error.message}`);
  }
}

/**
 * Signs an MSIX package with a certificate
 * @param {string} msixPath - Path to the MSIX package to sign
 * @param {string} certificatePath - Path to the .pfx certificate file
 * @param {Object} options - Signing options
 * @param {string} options.password - Certificate password (default: 'password')
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.timestampUrl - Timestamp server URL (optional)
 */
async function signMsixPackage(msixPath, certificatePath, options = {}) {
  const { password = 'password', verbose = true, timestampUrl } = options;
  
  if (!fsSync.existsSync(msixPath)) {
    throw new Error(`MSIX package not found: ${msixPath}`);
  }
  
  if (!fsSync.existsSync(certificatePath)) {
    throw new Error(`Certificate file not found: ${certificatePath}`);
  }

  let command = `signtool sign /f "${certificatePath}" /p "${password}" /fd SHA256`;
  
  if (timestampUrl) {
    command += ` /tr "${timestampUrl}" /td SHA256`;
  }
  
  command += ` "${msixPath}"`;
  
  if (verbose) {
    console.log(`Signing MSIX package: ${msixPath}`);
  }

  try {
    await execSyncWithBuildTools(command, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log('MSIX package signed successfully');
    }
    
    return { signed: true, msixPath };
  } catch (error) {
    throw new Error(`Failed to sign MSIX package: ${error.message}`);
  }
}

/**
 * Creates an MSIX package from a prepared package directory
 * @param {string} inputFolder - Path to the folder containing the package contents
 * @param {string} outputFolder - Path to the folder where the MSIX will be created
 * @param {Object} options - Packaging options
 * @param {string} options.packageName - Name for the output MSIX file (default: derived from manifest)
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {boolean} options.skipPri - Skip PRI generation (default: false)
 * @param {boolean} options.autoSign - Automatically sign the package (default: false)
 * @param {string} options.certificatePath - Path to signing certificate (required if autoSign is true)
 * @param {string} options.certificatePassword - Certificate password (default: 'password')
 * @param {boolean} options.generateDevCert - Generate a new development certificate if none provided (default: false)
 * @param {boolean} options.installDevCert - Install certificate to machine (default: false)
 * @param {string} options.publisher - Publisher name for certificate generation (default: extracted from manifest)
 */
async function createMsixPackage(inputFolder, outputFolder, options = {}) {
  const {
    packageName,
    verbose = true,
    skipPri = false,
    autoSign = false,
    certificatePath,
    certificatePassword = 'password',
    generateDevCert = false,
    installDevCert = false,
    publisher
  } = options;

  // Remove trailing backslashes from inputFolder
  inputFolder = inputFolder.replace(/[\\\/]+$/, '');

  // Validate input folder and manifest
  if (!fsSync.existsSync(inputFolder)) {
    throw new Error(`Input folder not found: ${inputFolder}`);
  }

  const manifestPath = path.join(inputFolder, 'appxmanifest.xml');
  if (!fsSync.existsSync(manifestPath)) {
    throw new Error(`appxmanifest.xml not found in input folder: ${inputFolder}`);
  }

  // Ensure output folder exists
  if (!fsSync.existsSync(outputFolder)) {
    await fs.mkdir(outputFolder, { recursive: true });
  }

  // Determine package name
  let finalPackageName = packageName;
  let extractedPublisher = publisher;
  
  if (!finalPackageName || !extractedPublisher) {
    try {
      const manifestContent = await fs.readFile(manifestPath, 'utf8');
      
      if (!finalPackageName) {
        const nameMatch = manifestContent.match(/<Identity[^>]*Name\s*=\s*["']([^"']*)["']/i);
        finalPackageName = nameMatch ? nameMatch[1] : 'Package';
      }
      
      if (!extractedPublisher) {
        const publisherMatch = manifestContent.match(/<Identity[^>]*Publisher\s*=\s*["']([^"']*)["']/i);
        extractedPublisher = publisherMatch ? publisherMatch[1] : null;
      }
    } catch (error) {
      finalPackageName = finalPackageName || 'Package';
    }
  }

  const outputMsixPath = path.join(outputFolder, `${finalPackageName}.msix`);

  if (verbose) {
    console.log(`Creating MSIX package from: ${inputFolder}`);
    console.log(`Output: ${outputMsixPath}`);
  }

  try {
    // Generate PRI files if not skipped
    if (!skipPri) {
      if (verbose) {
        console.log('Generating PRI configuration and files...');
      }
      
      await createPriConfig(inputFolder, { verbose });
      await generatePriFile(inputFolder, { verbose });
    }

    // Create MSIX package
    const makeappxCommand = `makeappx pack /o /d "${inputFolder}" /nv /p "${outputMsixPath}"`;
    
    if (verbose) {
      console.log('Creating MSIX package...');
    }
    
    await execSyncWithBuildTools(makeappxCommand, { stdio: verbose ? 'inherit' : 'pipe' });

    let certPath = certificatePath;
    let certInfo = null;

    // Handle certificate generation and signing
    if (autoSign) {
      if (!certPath && generateDevCert) {
        if (!extractedPublisher) {
          throw new Error('Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.');
        }
        
        if (verbose) {
          console.log(`Generating certificate for publisher: ${extractedPublisher}`);
        }
        
        certPath = path.join(outputFolder, `${finalPackageName}_cert.pfx`);
        certInfo = await generateDevCertificate(certPath, {
          publisher: extractedPublisher,
          password: certificatePassword,
          verbose
        });
      }

      if (!certPath) {
        throw new Error('Certificate path required for signing. Provide certificatePath or set generateDevCert to true.');
      }

      // Install certificate if requested
      if (installDevCert) {
        await installDevCertificate(certPath, {
          password: certificatePassword,
          verbose
        });
      }

      // Sign the package
      await signMsixPackage(outputMsixPath, certPath, {
        password: certificatePassword,
        verbose
      });
    }

    // Clean up temporary PRI files
    if (!skipPri) {
      const tempFiles = [
        path.join(inputFolder, 'priconfig.xml'),
        path.join(inputFolder, 'resources.pri')
      ];
      
      for (const file of tempFiles) {
        try {
          if (fsSync.existsSync(file)) {
            await fs.unlink(file);
          }
        } catch (error) {
          if (verbose) {
            console.warn(`Warning: Could not clean up ${file}: ${error.message}`);
          }
        }
      }
    }

    const result = {
      success: true,
      msixPath: outputMsixPath,
      packageName: finalPackageName,
      signed: autoSign
    };

    if (certInfo) {
      result.certificate = certInfo;
    }

    if (verbose) {
      console.log(`MSIX package created successfully: ${outputMsixPath}`);
      if (autoSign) {
        console.log('Package has been signed');
      }
    }

    return result;

  } catch (error) {
    throw new Error(`Failed to create MSIX package: ${error.message}`);
  }
}

/**
 * Generates an appxmanifest.xml file and creates default assets for an app
 * 
 * Template variables available for replacement:
 * - {PackageName} - The package name as provided
 * - {PackageNameCamelCase} - Package name converted to camelCase (removes dashes/spaces/underscores)
 * - {PublisherName} - Publisher name (without CN= prefix)
 * - {Description} - Description of the app
 * - {Version} - Package version (default: '1.0.0.0')
 * 
 * @param {Object} options - Generation options and application information
 * @param {string} options.packageName - Name of the package (optional, defaults to package.json "name")
 * @param {string} options.publisherName - Publisher name (without CN= prefix) (optional, defaults to package.json "author")
 * @param {string} options.description - Description of the app (optional, defaults to package.json "description" or packageName)
 * @param {boolean} options.isSparsePackage - Whether this is a sparse package (external location) or full package (default: false)
 * @param {string} options.outputDir - Output directory for the manifest and assets (default: current directory)
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.version - Package version (default: '1.0.0.0')
 * @param {string} options.executable - Name of the executable file (default: '{PackageName}.exe')
 */
async function generateMsixAssets(options = {}) {
  const { 
    isSparsePackage = false, 
    outputDir = process.cwd(), 
    verbose = true,
    version = '1.0.0.0'
  } = options;
  
  let { packageName, publisherName, description } = options;
  
  // Try to read package.json for default values
  let packageJsonDefaults = {};
  try {
    const packageJsonPath = path.join(process.cwd(), 'package.json');
    if (fsSync.existsSync(packageJsonPath)) {
      const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
      const packageJson = JSON.parse(packageJsonContent);
      
      // Handle author field - can be string or object with name/email
      let authorName = packageJson.author;
      if (typeof packageJson.author === 'object' && packageJson.author !== null) {
        authorName = packageJson.author.name;
      }
      
      packageJsonDefaults = {
        packageName: packageJson.name,
        publisherName: authorName,
        description: packageJson.description
      };
      
      if (verbose && (packageJsonDefaults.packageName || packageJsonDefaults.publisherName || packageJsonDefaults.description)) {
        console.log('Found package.json, using defaults where not provided');
      }
    }
  } catch (error) {
    // Ignore package.json read errors, will use provided values or throw error
    if (verbose) {
      console.log('Could not read package.json, using provided values only');
    }
  }
  
  // Use provided values or fall back to package.json defaults
  packageName = packageName || packageJsonDefaults.packageName;
  publisherName = publisherName || packageJsonDefaults.publisherName;
  description = description || packageJsonDefaults.description;
  
  // Validate required parameters
  if (!packageName) {
    throw new Error('Package name is required (provide in options.packageName or package.json "name" field)');
  }
  
  if (!publisherName) {
    throw new Error('Publisher name is required (provide in options.publisherName or package.json "author" field)');
  }

  // Ensure output directory exists
  if (!fsSync.existsSync(outputDir)) {
    await fs.mkdir(outputDir, { recursive: true });
  }

  const finalDescription = description || packageName;
  const manifestPath = path.join(outputDir, 'appxmanifest.xml');
  const assetsDir = path.join(outputDir, 'Assets');

  if (verbose) {
    console.log(`Generating ${isSparsePackage ? 'sparse' : 'packaged'} MSIX manifest for: ${packageName}`);
    console.log(`Output directory: ${outputDir}`);
  }

  try {
    // Read the appropriate template
    const templateFileName = isSparsePackage ? 'appxmanifest.sparse.xml' : 'appxmanifest.packaged.xml';
    const templatePath = path.join(__dirname, 'appxmanifest-templates', templateFileName);
    
    if (!fsSync.existsSync(templatePath)) {
      throw new Error(`Template file not found: ${templatePath}`);
    }

    let manifestContent = await fs.readFile(templatePath, 'utf8');
    
    // Convert package name to camelCase for places that don't support dashes
    const packageNameCamelCase = packageName
      .split(/[-_\s]+/) // Split on dashes, underscores, or spaces
      .map((word, index) => {
        if (index === 0) {
          // Keep first word lowercase
          return word.toLowerCase();
        }
        // Capitalize first letter of subsequent words
        return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
      })
      .join('');

    const executable = options.executable || `${packageName}.exe`;
    
    // Replace placeholders in the template
    manifestContent = manifestContent
      .replace(/{PackageName}/g, packageName)
      .replace(/{PackageNameCamelCase}/g, packageNameCamelCase)
      .replace(/{PublisherName}/g, publisherName)
      .replace(/{Description}/g, finalDescription)
      .replace(/Version="1\.0\.0\.0"/g, `Version="${version}"`)
      .replace(/{Executable}/g, executable);

    // Write the manifest file
    await fs.writeFile(manifestPath, manifestContent, 'utf8');

    if (verbose) {
      console.log(`Manifest created: ${manifestPath}`);
    }

    // Create Assets directory
    if (!fsSync.existsSync(assetsDir)) {
      await fs.mkdir(assetsDir, { recursive: true });
    }

    // Copy default assets
    const defaultAssetsDir = path.join(__dirname, 'msix-default-assets');
    
    if (!fsSync.existsSync(defaultAssetsDir)) {
      throw new Error(`Default assets directory not found: ${defaultAssetsDir}`);
    }

    const assetFiles = await fs.readdir(defaultAssetsDir);
    
    for (const assetFile of assetFiles) {
      const sourcePath = path.join(defaultAssetsDir, assetFile);
      const destPath = path.join(assetsDir, assetFile);
      
      const stat = await fs.stat(sourcePath);
      if (stat.isFile()) {
        await fs.copyFile(sourcePath, destPath);
        
        if (verbose) {
          console.log(`Copied asset: ${assetFile}`);
        }
      }
    }

    const result = {
      success: true,
      manifestPath,
      assetsDir,
      packageType: isSparsePackage ? 'sparse' : 'packaged',
      packageName,
      publisherName,
      assetFiles: assetFiles.filter(file => !fsSync.statSync(path.join(defaultAssetsDir, file)).isDirectory())
    };

    if (verbose) {
      console.log(`✅ Successfully generated ${isSparsePackage ? 'sparse' : 'packaged'} MSIX manifest and assets`);
      console.log(`📁 Manifest: ${manifestPath}`);
      console.log(`🎨 Assets: ${assetsDir} (${result.assetFiles.length} files)`);
    }

    return result;

  } catch (error) {
    throw new Error(`Failed to generate appxmanifest: ${error.message}`);
  }
}

/**
 * Adds MSIX identity to the Electron debug process
 * @param {Object} options - Configuration options
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function addElectronDebugIdentity(options = {}) {
  const { verbose = true } = options;
  
  if (verbose) {
    console.log('🔧 Adding MSIX identity to Electron debug process...');
  }
  
  const electronExePath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.exe');
  const electronBackupPath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.backup.exe');
  const msixDebugDir = path.join(process.cwd(), 'msix-debug');
  const manifestPath = path.join(msixDebugDir, 'appxmanifest.xml');
  
  try {
    // Step 1: Make a backup of electron.exe
    if (!fsSync.existsSync(electronExePath)) {
      throw new Error(`Electron executable not found at: ${electronExePath}`);
    }
    
    if (fsSync.existsSync(electronBackupPath)) {
      if (verbose) {
        console.log('⏭️  Backup already exists, skipping backup step');
      }
    } else {
      if (verbose) {
        console.log('💾 Creating backup of electron.exe...');
      }
      await fs.copyFile(electronExePath, electronBackupPath);
      if (verbose) {
        console.log(`✅ Backup created: ${electronBackupPath}`);
      }
    }
    
    // Step 2: Generate sparse appxmanifest and assets if they don't exist
    if (fsSync.existsSync(manifestPath)) {
      if (verbose) {
        console.log('⏭️  Manifest already exists, skipping generation step');
      }
    } else {
      if (verbose) {
        console.log('📄 Generating sparse MSIX manifest and assets...');
      }
      
      const result = await generateMsixAssets({
        isSparsePackage: true,
        outputDir: msixDebugDir,
        executable: 'node_modules/electron/dist/electron.exe',
        verbose: verbose
      });
      
      if (verbose) {
        console.log(`✅ Sparse manifest generated: ${result.manifestPath}`);
      }
    }
    
    // Step 3: Add identity to electron.exe
    if (verbose) {
      console.log('🔐 Adding MSIX identity to electron.exe...');
    }
    
    const identityResult = await addMsixIdentityToExe(electronExePath, manifestPath, {
      verbose: verbose
    });
    
    if (verbose) {
      console.log('✅ MSIX identity added to electron.exe');
    }
    
    // Step 4: Unregister any existing package first
    if (verbose) {
      console.log('🗑️  Checking for existing package...');
    }
    
    try {
      // Get package name from manifest to check and unregister it
      const manifestContent = await fs.readFile(manifestPath, 'utf8');
      const nameMatch = manifestContent.match(/<Identity[^>]*Name\s*=\s*["']([^"']*)["']/i);
      
      if (nameMatch) {
        const packageName = nameMatch[1];
        
        // First check if package exists
        const checkCommand = `powershell -Command "Get-AppxPackage -Name '${packageName}'"`;
        
        try {
          const checkResult = await execSyncWithBuildTools(checkCommand, { stdio: 'pipe' });
          const checkOutput = checkResult.toString().trim();
          
          if (checkOutput && checkOutput.length > 0) {
            // Package exists, remove it
            if (verbose) {
              console.log(`📦 Found existing package '${packageName}', removing it...`);
            }
            
            const unregisterCommand = `powershell -Command "Get-AppxPackage -Name '${packageName}' | Remove-AppxPackage"`;
            await execSyncWithBuildTools(unregisterCommand, { stdio: verbose ? 'inherit' : 'pipe' });
            
            if (verbose) {
              console.log('✅ Existing package unregistered successfully');
            }
          } else {
            // No package found, proceed silently
            if (verbose) {
              console.log('ℹ️  No existing package found');
            }
          }
        } catch (checkError) {
          // If check fails, package likely doesn't exist
          if (verbose) {
            console.log('ℹ️  No existing package found');
          }
        }
      }
    } catch (error) {
      if (verbose) {
        console.log('⚠️  Note: Could not check for existing package');
      }
    }
    
    // Step 5: Register the manifest with external location
    if (verbose) {
      console.log('📋 Registering sparse package with external location...');
    }
    
    const currentDir = process.cwd();
    const registerCommand = `powershell -Command "Add-AppxPackage -Path '${manifestPath}' -ExternalLocation '${currentDir}' -Register -ForceUpdateFromAnyVersion"`;
    
    await execSyncWithBuildTools(registerCommand, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log('✅ Sparse package registered successfully');
    }
    
    const result = {
      success: true,
      electronExePath,
      backupPath: electronBackupPath,
      manifestPath,
      assetsDir: path.join(msixDebugDir, 'Assets'),
      packageName: identityResult.packageName,
      publisher: identityResult.publisher,
      applicationId: identityResult.applicationId
    };
    
    if (verbose) {
      console.log('🎉 Electron debug identity setup completed successfully!');
      console.log(`📦 Package: ${result.packageName}`);
      console.log(`👤 Publisher: ${result.publisher}`);
      console.log(`🆔 App ID: ${result.applicationId}`);
      console.log(`📁 Manifest: ${result.manifestPath}`);
    }
    
    return result;
    
  } catch (error) {
    throw new Error(`Failed to add Electron debug identity: ${error.message}`);
  }
}

module.exports = {
  addMsixIdentityToExe,
  createPriConfig,
  generatePriFile,
  generateDevCertificate,
  installDevCertificate,
  signMsixPackage,
  createMsixPackage,
  generateMsixAssets,
  addElectronDebugIdentity
};
