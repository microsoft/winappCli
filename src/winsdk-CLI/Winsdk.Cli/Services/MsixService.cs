using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Winsdk.Cli.Services;

namespace Winsdk.Cli;

internal class MsixService
{
    private readonly BuildToolsService _buildToolsService;
    private readonly PowerShellService _powerShellService;

    public MsixService(BuildToolsService buildToolsService)
    {
        _buildToolsService = buildToolsService;
        _powerShellService = new PowerShellService();
    }

    /// <summary>
    /// Parses an AppX manifest file and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="FileNotFoundException">Thrown when the manifest file is not found</exception>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public async Task<MsixIdentityResult> ParseAppxManifestAsync(string appxManifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(appxManifestPath))
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}");

        // Read and extract MSIX identity from appxmanifest.xml
        var appxManifestContent = await File.ReadAllTextAsync(appxManifestPath, Encoding.UTF8, cancellationToken);

        // Extract Package Identity information
        var identityMatch = Regex.Match(appxManifestContent, @"<Identity[^>]*>", RegexOptions.IgnoreCase);
        if (!identityMatch.Success)
            throw new InvalidOperationException("No Identity element found in AppX manifest");

        var identityElement = identityMatch.Value;

        // Extract attributes from Identity element
        var nameMatch = Regex.Match(identityElement, @"Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        var publisherMatch = Regex.Match(identityElement, @"Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);

        if (!nameMatch.Success || !publisherMatch.Success)
            throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

        var packageName = nameMatch.Groups[1].Value;
        var publisher = publisherMatch.Groups[1].Value;

        // Extract Application ID from Applications/Application element
        var applicationMatch = Regex.Match(appxManifestContent, @"<Application[^>]*Id\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase);
        if (!applicationMatch.Success)
            throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");

        var applicationId = applicationMatch.Groups[1].Value;

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    public async Task<MsixIdentityResult> AddMsixIdentityToExeAsync(string exePath, string appxManifestPath, bool noInstall, string? applicationLocation = null, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(appxManifestPath))
        {
            appxManifestPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), appxManifestPath));
        }

        // Validate inputs
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Executable not found at: {exePath}");
        }

        if (!File.Exists(appxManifestPath))
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winsdk manifest generate'.");
        }

        if (verbose)
        {
            Console.WriteLine($"Processing executable: {exePath}");
            Console.WriteLine($"Using AppX manifest: {appxManifestPath}");
        }

        // Generate sparse package structure
        var (debugManifestPath, debugIdentity) = await GenerateSparsePackageStructureAsync(
            appxManifestPath, 
            exePath,
            applicationLocation, 
            verbose, 
            cancellationToken);

        // Update executable with debug identity
        await EmbedMsixIdentityToExeAsync(exePath, debugIdentity, applicationLocation, verbose, cancellationToken);

        if (noInstall)
        {
            if (verbose)
            {
                Console.WriteLine("Skipping package installation as per --no-install option.");
            }
        }
        else
        {
            // Register the debug appxmanifest
            var workingDirectory = applicationLocation ?? Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
            
            // Unregister any existing package first
            await UnregisterExistingPackageAsync(debugIdentity.PackageName, verbose, cancellationToken);
            
            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, workingDirectory, verbose, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    private async Task EmbedMsixIdentityToExeAsync(string exePath, MsixIdentityResult identityInfo, string? applicationLocation, bool verbose, CancellationToken cancellationToken)
    {
        var workingDir = applicationLocation ?? Path.GetDirectoryName(exePath)!;
        var tempManifestPath = Path.Combine(workingDir, "temp_extracted.manifest");
        var combinedManifestPath = Path.Combine(workingDir, "combined.manifest");

        try
        {
            // Create the MSIX element for the win32 manifest
            var msixElement = $@"<msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />";

            if (verbose)
            {
                Console.WriteLine("Extracting current manifest from executable...");
            }

            // Extract current manifest from the executable
            bool hasExistingManifest = false;
            try
            {
                await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", verbose, cancellationToken);
                hasExistingManifest = File.Exists(tempManifestPath);
            }
            catch
            {
                if (verbose)
                {
                    Console.WriteLine("No existing manifest found in executable, creating new one");
                }
            }

            string finalManifest;

            if (hasExistingManifest)
            {
                if (verbose)
                {
                    Console.WriteLine("Combining with existing manifest...");
                }

                // Read existing manifest
                var existingManifest = await File.ReadAllTextAsync(tempManifestPath, Encoding.UTF8, cancellationToken);

                // Find the closing </assembly> tag in existing manifest
                var existingManifestParts = existingManifest.Split("</assembly>");

                if (existingManifestParts.Length >= 2)
                {
                    // Remove any existing msix section
                    var cleanedExistingContent = existingManifestParts[0];
                    cleanedExistingContent = Regex.Replace(cleanedExistingContent, @"<msix[\s\S]*?</msix>", "", RegexOptions.IgnoreCase);
                    cleanedExistingContent = Regex.Replace(cleanedExistingContent, @"<msix[\s\S]*?/>", "", RegexOptions.IgnoreCase);

                    // Combine: existing content + msix element + closing tag + rest
                    finalManifest = cleanedExistingContent + "\n  " + msixElement + "\n</assembly>" + string.Join("</assembly>", existingManifestParts.Skip(1));
                }
                else
                {
                    throw new InvalidOperationException("Invalid existing manifest structure");
                }

                // Clean up temporary file
                TryDeleteFile(tempManifestPath);
            }
            else
            {
                // Create a new basic manifest with MSIX identity
                finalManifest = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
    <assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
      {msixElement}
      <assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>
    </assembly>";
            }

            // Write the combined manifest
            await File.WriteAllTextAsync(combinedManifestPath, finalManifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            var command = $@"-manifest ""{combinedManifestPath}"" -outputresource:""{exePath}"";#1";
            if (verbose)
            {
                Console.WriteLine($"Final manifest content: {finalManifest}");
                Console.WriteLine("Re-embedding manifest into executable...");
                Console.WriteLine($"Command: mt.exe {command}");
            }

            // Re-embed the combined manifest into the executable
            await RunMtToolAsync(command, verbose, cancellationToken);

            if (verbose)
            {
                Console.WriteLine("MSIX identity successfully embedded into executable");
            }

            // Clean up combined manifest file
            TryDeleteFile(combinedManifestPath);
        }
        catch (Exception ex)
        {
            // Clean up any temporary files
            TryDeleteFile(tempManifestPath);
            TryDeleteFile(combinedManifestPath);

            throw new InvalidOperationException($"Failed to add MSIX identity to executable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="language">Default language qualifier (default: 'en-US')</param>
    /// <param name="platformVersion">Platform version (default: '10.0.0')</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the created configuration file</returns>
    public async Task<string> CreatePriConfigAsync(string packageDir, string language = "en-US", string platformVersion = "10.0.0", bool verbose = true, CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from packageDir
        packageDir = packageDir.TrimEnd('\\', '/');

        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");

        var configPath = Path.Combine(packageDir, "priconfig.xml");
        var arguments = $@"createconfig /cf ""{configPath}"" /dq {language} /pv {platformVersion} /o";

        if (verbose)
        {
            Console.WriteLine("Creating PRI configuration file...");
        }

        try
        {
            await _buildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose, cancellationToken: cancellationToken);

            if (verbose)
            {
                Console.WriteLine($"PRI configuration created: {configPath}");
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="configPath">Path to PRI config file (default: packageDir/priconfig.xml)</param>
    /// <param name="outputPath">Output path for PRI file (default: packageDir/resources.pri)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of resource files that were processed</returns>
    public async Task<List<string>> GeneratePriFileAsync(string packageDir, string? configPath = null, string? outputPath = null, bool verbose = true, CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from packageDir
        packageDir = packageDir.TrimEnd('\\', '/');

        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");

        var priConfigPath = configPath ?? Path.Combine(packageDir, "priconfig.xml");
        var priOutputPath = outputPath ?? Path.Combine(packageDir, "resources.pri");

        if (!File.Exists(priConfigPath))
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");

        var arguments = $@"new /pr ""{packageDir}"" /cf ""{priConfigPath}"" /of ""{priOutputPath}"" /o";

        if (verbose)
        {
            Console.WriteLine("Generating PRI file...");
        }

        try
        {
            var (stdout, stderr) = await _buildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<string>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                if (line.StartsWith("Resource File: ", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line.Substring("Resource File: ".Length).Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(Path.Combine(packageDir, fileName));
                    }
                }
            }

            if (verbose)
            {
                Console.WriteLine($"PRI file generated: {priOutputPath}");
                if (resourceFiles.Count > 0)
                {
                    Console.WriteLine($"Processed {resourceFiles.Count} resource files");
                }
            }

            return resourceFiles;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
    /// <param name="inputFolder">Path to the folder containing the package contents</param>
    /// <param name="outputFolder">Path to the folder where the MSIX will be created</param>
    /// <param name="packageName">Name for the output MSIX file (default: derived from manifest)</param>
    /// <param name="skipPri">Skip PRI generation</param>
    /// <param name="autoSign">Automatically sign the package</param>
    /// <param name="certificatePath">Path to signing certificate (required if autoSign is true)</param>
    /// <param name="certificatePassword">Certificate password</param>
    /// <param name="generateDevCert">Generate a new development certificate if none provided</param>
    /// <param name="installDevCert">Install certificate to machine</param>
    /// <param name="publisher">Publisher name for certificate generation (default: extracted from manifest)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the MSIX path and signing status</returns>
    public async Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        string inputFolder,
        string outputFolder,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        string? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        string? manifestPath = null,
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from inputFolder
        inputFolder = inputFolder.TrimEnd('\\', '/');

        // Validate input folder and manifest
        if (!Directory.Exists(inputFolder))
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");

        // Determine manifest path based on priority:
        // 1. Use provided manifestPath parameter
        // 2. Check for appxmanifest.xml in input folder
        // 3. Check for appxmanifest.xml in current directory
        string resolvedManifestPath;
        if (!string.IsNullOrEmpty(manifestPath))
        {
            resolvedManifestPath = manifestPath;
            if (verbose)
            {
                Console.WriteLine($"📄 Using specified manifest: {resolvedManifestPath}");
            }
        }
        else
        {
            var inputFolderManifest = Path.Combine(inputFolder, "appxmanifest.xml");
            if (File.Exists(inputFolderManifest))
            {
                resolvedManifestPath = inputFolderManifest;
                if (verbose)
                {
                    Console.WriteLine($"📄 Using manifest from input folder: {inputFolderManifest}");
                }
            }
            else
            {
                var currentDirManifest = Path.Combine(Directory.GetCurrentDirectory(), "appxmanifest.xml");
                if (File.Exists(currentDirManifest))
                {
                    resolvedManifestPath = currentDirManifest;
                    if (verbose)
                    {
                        Console.WriteLine($"📄 Using manifest from current directory: {currentDirManifest}");
                    }
                }
                else
                {
                    throw new FileNotFoundException($"Manifest file not found. Searched in: input folder ({inputFolderManifest}), current directory ({currentDirManifest})");
                }
            }
        }

        if (!File.Exists(resolvedManifestPath))
            throw new FileNotFoundException($"Manifest file not found: {resolvedManifestPath}");

        // Ensure output folder exists
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;

        if (string.IsNullOrWhiteSpace(finalPackageName) || string.IsNullOrWhiteSpace(extractedPublisher))
        {
            try
            {
                var manifestContent = await File.ReadAllTextAsync(resolvedManifestPath, Encoding.UTF8, cancellationToken);

                if (string.IsNullOrWhiteSpace(finalPackageName))
                {
                    var nameMatch = Regex.Match(manifestContent, @"<Identity[^>]*Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    finalPackageName = nameMatch.Success ? nameMatch.Groups[1].Value : "Package";
                }

                if (string.IsNullOrWhiteSpace(extractedPublisher))
                {
                    var publisherMatch = Regex.Match(manifestContent, @"<Identity[^>]*Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    extractedPublisher = publisherMatch.Success ? publisherMatch.Groups[1].Value : null;
                }
            }
            catch
            {
                finalPackageName ??= "Package";
            }
        }

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        var outputMsixPath = Path.Combine(outputFolder, $"{finalPackageName}.msix");

        if (verbose)
        {
            Console.WriteLine($"Creating MSIX package from: {inputFolder}");
            Console.WriteLine($"Output: {outputMsixPath}");
        }

        try
        {
            List<string> tempFiles = [];
            // Generate PRI files if not skipped
            if (!skipPri)
            {
                if (verbose)
                {
                    Console.WriteLine("Generating PRI configuration and files...");
                }

                string priConfigFilePath = await CreatePriConfigAsync(inputFolder, verbose: verbose, cancellationToken: cancellationToken);
                tempFiles.Add(priConfigFilePath);
                var resourceFiles = await GeneratePriFileAsync(inputFolder, verbose: verbose, cancellationToken: cancellationToken);
                tempFiles.AddRange(resourceFiles);
                if (verbose && resourceFiles.Count > 0)
                {
                    Console.WriteLine($"Resource files included in PRI:");
                    foreach (var resourceFile in resourceFiles)
                    {
                        Console.WriteLine($"  - {resourceFile}");
                    }
                }
            }

            await CreateMsixPackageFromFolderAsync(inputFolder, verbose, outputMsixPath, cancellationToken);

            var certPath = certificatePath;
            // Handle certificate generation and signing
            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, verbose, finalPackageName, extractedPublisher, outputMsixPath, certPath, cancellationToken);
            }

            // Clean up temporary PRI files
            if (!skipPri)
            {
                foreach (var file in tempFiles)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"Warning: Could not clean up {file}");
                        }
                    }
                }
            }

            if (verbose)
            {
                Console.WriteLine($"MSIX package created successfully: {outputMsixPath}");
                if (autoSign)
                {
                    Console.WriteLine("Package has been signed");
                }
            }

            return new CreateMsixPackageResult(outputMsixPath, autoSign);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create MSIX package: {ex.Message}", ex);
        }
    }

    private async Task SignMsixPackageAsync(string outputFolder, string certificatePassword, bool generateDevCert, bool installDevCert, bool verbose, string finalPackageName, string? extractedPublisher, string outputMsixPath, string? certPath, CancellationToken cancellationToken)
    {
        var certificateService = new CertificateServices(_buildToolsService);

        if (string.IsNullOrWhiteSpace(certPath) && generateDevCert)
        {
            if (string.IsNullOrWhiteSpace(extractedPublisher))
                throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");

            if (verbose)
            {
                Console.WriteLine($"Generating certificate for publisher: {extractedPublisher}");
            }

            certPath = Path.Combine(outputFolder, $"{finalPackageName}_cert.pfx");
            await certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, certificatePassword, verbose: verbose, cancellationToken: cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");

        // Install certificate if requested
        if (installDevCert)
        {
            var result = await certificateService.InstallCertificateAsync(certPath, certificatePassword, false, verbose, cancellationToken);
        }

        // Sign the package
        await certificateService.SignFileAsync(outputMsixPath, certPath, certificatePassword, verbose: verbose, cancellationToken: cancellationToken);
    }

    private async Task CreateMsixPackageFromFolderAsync(string inputFolder, bool verbose, string outputMsixPath, CancellationToken cancellationToken)
    {
        // Create MSIX package
        var makeappxArguments = $@"pack /o /d ""{inputFolder}"" /nv /p ""{outputMsixPath}""";

        if (verbose)
        {
            Console.WriteLine("Creating MSIX package...");
        }

        await _buildToolsService.RunBuildToolAsync("makeappx.exe", makeappxArguments, verbose, cancellationToken: cancellationToken);
    }

    private async Task RunMtToolAsync(string arguments, bool verbose, CancellationToken cancellationToken = default)
    {
        // Use the new BuildToolsService to run mt.exe
        await _buildToolsService.RunBuildToolAsync("mt.exe", arguments, verbose, cancellationToken: cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    /// <summary>
    /// Searches for appxmanifest.xml in the project by looking for .winsdk directory in parent directories
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from. If null, uses current directory.</param>
    /// <returns>Path to the project's appxmanifest.xml file, or null if not found</returns>
    public static string? FindProjectManifest(string? startDirectory = null)
    {
        var currentDir = startDirectory ?? Directory.GetCurrentDirectory();
        var directory = new DirectoryInfo(currentDir);

        while (directory != null)
        {
            var manifestPath = Path.Combine(directory.FullName, "appxmanifest.xml");
            if (File.Exists(manifestPath))
            {
                return manifestPath;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Generates a sparse package structure for debug purposes
    /// </summary>
    /// <param name="originalManifestPath">Path to the original appxmanifest.xml</param>
    /// <param name="executablePath">Path to the executable that the manifest should reference</param>
    /// <param name="baseDirectory">Base directory to create the debug structure in</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the debug manifest path and modified identity info</returns>
    public async Task<(string debugManifestPath, MsixIdentityResult debugIdentity)> GenerateSparsePackageStructureAsync(
        string originalManifestPath, 
        string executablePath,
        string? baseDirectory = null, 
        bool verbose = true, 
        CancellationToken cancellationToken = default)
    {
        var workingDir = baseDirectory ?? Directory.GetCurrentDirectory();
        var winsdkDir = Path.Combine(workingDir, ".winsdk");
        var debugDir = Path.Combine(winsdkDir, "debug");

        if (verbose)
        {
            Console.WriteLine($"🔧 Creating sparse package structure in: {debugDir}");
        }

        // Step 1: Create debug directory, removing existing one if present
        if (Directory.Exists(debugDir))
        {
            if (verbose)
            {
                Console.WriteLine("🗑️  Removing existing debug directory...");
            }
            Directory.Delete(debugDir, recursive: true);
        }

        Directory.CreateDirectory(debugDir);
        if (verbose)
        {
            Console.WriteLine("📁 Created debug directory");
        }

        // Step 2: Parse original manifest to get identity and assets
        var originalManifestContent = await File.ReadAllTextAsync(originalManifestPath, Encoding.UTF8, cancellationToken);
        var originalIdentity = await ParseAppxManifestAsync(originalManifestPath, cancellationToken);

        // Step 3: Create debug identity with ".debug" suffix
        var debugIdentity = CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        var debugManifestContent = CreateDebugManifestContent(
            originalManifestContent, 
            originalIdentity, 
            debugIdentity, 
            executablePath,
            baseDirectory,
            verbose);

        // Step 5: Write debug manifest
        var debugManifestPath = Path.Combine(debugDir, "appxmanifest.xml");
        await File.WriteAllTextAsync(debugManifestPath, debugManifestContent, Encoding.UTF8, cancellationToken);
        
        if (verbose)
        {
            Console.WriteLine($"📄 Created debug manifest: {debugManifestPath}");
        }

        // Step 6: Copy all assets
        await CopyAllAssetsAsync(originalManifestPath, debugDir, verbose, cancellationToken);

        return (debugManifestPath, debugIdentity);
    }

    /// <summary>
    /// Creates a debug version of the identity by appending ".debug" to package name and application ID
    /// </summary>
    private static MsixIdentityResult CreateDebugIdentity(MsixIdentityResult originalIdentity)
    {
        var debugPackageName = originalIdentity.PackageName.EndsWith(".debug")
            ? originalIdentity.PackageName
            : $"{originalIdentity.PackageName}.debug";

        var debugApplicationId = originalIdentity.ApplicationId.EndsWith(".debug")
            ? originalIdentity.ApplicationId
            : $"{originalIdentity.ApplicationId}.debug";

        return new MsixIdentityResult(debugPackageName, originalIdentity.Publisher, debugApplicationId);
    }

    /// <summary>
    /// Creates the content for the debug manifest with sparse packaging and debug identity
    /// </summary>
    private string CreateDebugManifestContent(
        string originalManifestContent,
        MsixIdentityResult originalIdentity,
        MsixIdentityResult debugIdentity,
        string executablePath,
        string? baseDirectory,
        bool verbose)
    {
        var modifiedContent = originalManifestContent;

        // Replace package identity attributes
        modifiedContent = Regex.Replace(
            modifiedContent,
            @"(<Identity[^>]*Name\s*=\s*)[""']([^""']*)[""']",
            $@"$1""{debugIdentity.PackageName}""",
            RegexOptions.IgnoreCase);

        // Replace application ID
        modifiedContent = Regex.Replace(
            modifiedContent,
            @"(<Application[^>]*Id\s*=\s*)[""']([^""']*)[""']",
            $@"$1""{debugIdentity.ApplicationId}""",
            RegexOptions.IgnoreCase);

        // Replace executable path with relative path from package root
        var workingDir = baseDirectory ?? Directory.GetCurrentDirectory();
        string relativeExecutablePath;
        
        try
        {
            // Calculate relative path from the working directory (package root) to the executable
            relativeExecutablePath = Path.GetRelativePath(workingDir, executablePath);
            
            // Ensure we use forward slashes for consistency in manifest
            relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
        }
        catch
        {
            // Fallback to just the filename if relative path calculation fails
            relativeExecutablePath = Path.GetFileName(executablePath);
        }
        
        modifiedContent = Regex.Replace(
            modifiedContent,
            @"(<Application[^>]*Executable\s*=\s*)[""']([^""']*)[""']",
            $@"$1""{relativeExecutablePath}""",
            RegexOptions.IgnoreCase);

        // Add required namespaces for sparse packaging
        if (!modifiedContent.Contains("xmlns:uap10"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<Package[^>]*)(>)",
                @"$1 xmlns:uap10=""http://schemas.microsoft.com/appx/manifest/uap/windows10/10""$2",
                RegexOptions.IgnoreCase);
        }
        
        if (!modifiedContent.Contains("xmlns:desktop6"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<Package[^>]*)(>)",
                @"$1 xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""$2",
                RegexOptions.IgnoreCase);
        }

        // Add sparse package properties
        if (!modifiedContent.Contains("<uap10:AllowExternalContent>"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(\s*</Properties>)",
                @"    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
$1",
                RegexOptions.IgnoreCase);
        }

        // Ensure Application has sparse packaging attributes
        if (!modifiedContent.Contains("uap10:TrustLevel"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<Application[^>]*)(>)",
                @"$1 uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""packagedClassicApp""$2",
                RegexOptions.IgnoreCase);
        }

        // Remove EntryPoint if present (not needed for sparse packages)
        modifiedContent = Regex.Replace(
            modifiedContent,
            @"\s*EntryPoint\s*=\s*[""'][^""']*[""']",
            "",
            RegexOptions.IgnoreCase);

        // Add AppListEntry="none" to VisualElements if not present
        if (!modifiedContent.Contains("AppListEntry"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<uap:VisualElements[^>]*)(>)",
                @"$1 AppListEntry=""none""$2",
                RegexOptions.IgnoreCase);
        }

        // Add sparse-specific capabilities if not present
        if (!modifiedContent.Contains("unvirtualizedResources"))
        {
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(\s*<rescap:Capability Name=""runFullTrust"" />)",
                @"$1
    <rescap:Capability Name=""unvirtualizedResources""/>
    <rescap:Capability Name=""allowElevation"" />",
                RegexOptions.IgnoreCase);
        }

        if (verbose)
        {
            Console.WriteLine("✏️  Modified manifest for sparse packaging and debug identity");
        }

        return modifiedContent;
    }

    /// <summary>
    /// Copies all files from the original manifest directory to the debug directory, excluding debug folders
    /// </summary>
    private async Task CopyAllAssetsAsync(string originalManifestPath, string debugDir, bool verbose, CancellationToken cancellationToken)
    {
        var originalManifestDir = Path.GetDirectoryName(originalManifestPath)!;

        if (verbose)
        {
            Console.WriteLine($"📋 Copying all files from: {originalManifestDir}");
        }

        var filesCopied = await CopyDirectoryRecursiveAsync(originalManifestDir, debugDir, verbose);

        if (verbose)
        {
            Console.WriteLine($"✅ Copied {filesCopied} files to debug directory");
        }
    }

    /// <summary>
    /// Recursively copies files and directories, excluding specified directories and manifest files
    /// </summary>
    private static async Task<int> CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, bool verbose)
    {
        var filesCopied = 0;
        
        // List of directories to exclude from copying
        var excludedDirectories = new List<string> { ".winsdk", ".git" };
        
        // Get all files in current directory
        var files = Directory.GetFiles(sourceDir);
        
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            
            // Skip appxmanifest.xml as we've already created the debug version
            if (fileName.Equals("appxmanifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                if (verbose)
                {
                    Console.WriteLine($"⏭️  Skipping manifest file: {file}");
                }
                continue;
            }
            
            var targetFile = Path.Combine(targetDir, fileName);
            
            // Ensure target directory exists
            Directory.CreateDirectory(targetDir);
            
            File.Copy(file, targetFile, overwrite: true);
            filesCopied++;
        }

        // Get all subdirectories and copy them recursively
        var directories = Directory.GetDirectories(sourceDir);
        
        foreach (var directory in directories)
        {
            var dirName = Path.GetFileName(directory);
            
            // Skip directories that are in the exclusion list
            if (excludedDirectories.Any(excluded => dirName.Equals(excluded, StringComparison.OrdinalIgnoreCase)))
            {
                if (verbose)
                {
                    Console.WriteLine($"⏭️  Skipping excluded directory: {directory}");
                }
                continue;
            }
            
            var targetSubDir = Path.Combine(targetDir, dirName);
            var subDirFilesCopied = await CopyDirectoryRecursiveAsync(directory, targetSubDir, verbose);
            filesCopied += subDirFilesCopied;
        }
        
        return filesCopied;
    }

    /// <summary>
    /// Checks if a package with the given name exists and unregisters it if found
    /// </summary>
    /// <param name="packageName">The name of the package to check and unregister</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(string packageName, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (verbose)
        {
            Console.WriteLine("🗑️  Checking for existing package...");
        }

        try
        {
            // First check if package exists
            var checkCommand = $"Get-AppxPackage -Name '{packageName}'";
            var (_, checkResult) = await _powerShellService.RunCommandAsync(checkCommand, verbose: false, cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(checkResult))
            {
                // Package exists, remove it
                if (verbose)
                {
                    Console.WriteLine($"📦 Found existing package '{packageName}', removing it...");
                }

                var unregisterCommand = $"Get-AppxPackage -Name '{packageName}' | Remove-AppxPackage";
                await _powerShellService.RunCommandAsync(unregisterCommand, verbose: verbose, cancellationToken: cancellationToken);

                if (verbose)
                {
                    Console.WriteLine("✅ Existing package unregistered successfully");
                }
                return true;
            }
            else
            {
                // No package found
                if (verbose)
                {
                    Console.WriteLine("ℹ️  No existing package found");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            // If check fails, package likely doesn't exist or we don't have permission
            if (verbose)
            {
                Console.WriteLine($"ℹ️  Could not check for existing package: {ex.Message}");
            }
            return false;
        }
    }

    /// <summary>
    /// Registers a sparse package with external location using Add-AppxPackage
    /// </summary>
    /// <param name="manifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="externalLocation">External location path (typically the working directory)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RegisterSparsePackageAsync(string manifestPath, string externalLocation, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (verbose)
        {
            Console.WriteLine("📋 Registering sparse package with external location...");
        }

        var registerCommand = $"Add-AppxPackage -Path '{manifestPath}' -ExternalLocation '{externalLocation}' -Register -ForceUpdateFromAnyVersion";
        
        try
        {
            var (exitCode, _) = await _powerShellService.RunCommandAsync(registerCommand, verbose: verbose, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
            }

            if (verbose)
            {
                Console.WriteLine("✅ Sparse package registered successfully");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register sparse package: {ex.Message}", ex);
        }
    }

}
